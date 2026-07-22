using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;

namespace LeagueTracker.RenderAgent;

/// Captures the audio of ONE process tree (the game's) via Windows' process
/// loopback API and serves it to ffmpeg as raw PCM over a named pipe -
/// "just League audio": Discord, Spotify and system sounds never enter the
/// recording, which desktop-loopback recorders (Ascent's OBS included)
/// cannot promise. Windows 10 2004+; anything older just records video-only.
///
/// The pipe is paced against the wall clock with silence fill: process
/// loopback only delivers packets while the process renders sound, but an
/// audio track must be continuous or the mux drifts out of sync - so every
/// gap becomes explicit silence, and total samples always track elapsed time.
public sealed class ProcessAudioCapture : IDisposable
{
    public const int SampleRate = 48000;
    public const int Channels = 2;
    private const int BytesPerFrame = Channels * 2; // s16le
    private const string PipeName = "lt-game-audio";

    public string PipePath => $@"\\.\pipe\{PipeName}";

    /// s16le/48k/stereo, matching the ffmpeg input arguments.
    public static string FfmpegInputArgs => $@"-f s16le -ar {SampleRate} -ch_layout stereo -i \\.\pipe\{PipeName}";

    private readonly ConcurrentQueue<byte[]> _captured = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly NamedPipeServerStream _pipe;
    private readonly Thread _captureThread;
    private readonly Thread _writerThread;
    private IAudioClient? _client;

    private ProcessAudioCapture(int processId)
    {
        _pipe = new NamedPipeServerStream(PipeName, PipeDirection.Out, 1,
            PipeTransmissionMode.Byte, PipeOptions.None, 0, 1024 * 1024);
        _captureThread = new Thread(() => CaptureLoop(processId)) { IsBackground = true, Name = "audio-capture" };
        _writerThread = new Thread(WriterLoop) { IsBackground = true, Name = "audio-writer" };
        _captureThread.Start();
        _writerThread.Start();
    }

    /// Null when process loopback isn't available - the recording proceeds
    /// without an audio track rather than failing.
    public static ProcessAudioCapture? TryStart(int processId)
    {
        try
        {
            return new ProcessAudioCapture(processId);
        }
        catch (Exception ex)
        {
            Log.Warn($"Game audio capture unavailable: {ex.Message}");
            return null;
        }
    }

    private void CaptureLoop(int processId)
    {
        try
        {
            _client = ActivateProcessLoopback(processId);
            var format = new WaveFormatEx
            {
                FormatTag = 1, // PCM
                Channels = Channels,
                SamplesPerSec = SampleRate,
                BitsPerSample = 16,
                BlockAlign = BytesPerFrame,
                AvgBytesPerSec = SampleRate * BytesPerFrame,
            };
            using var bufferReady = new EventWaitHandle(false, EventResetMode.AutoReset);
            const uint LoopbackEventFlags = 0x00020000 | 0x00040000; // AUDCLNT_STREAMFLAGS_LOOPBACK | EVENTCALLBACK
            Check(_client.Initialize(ShareModeShared, LoopbackEventFlags, 2_000_000 /* 200ms */, 0, ref format, IntPtr.Zero), "Initialize");
            Check(_client.SetEventHandle(bufferReady.SafeWaitHandle.DangerousGetHandle()), "SetEventHandle");
            var captureIid = typeof(IAudioCaptureClient).GUID;
            Check(_client.GetService(ref captureIid, out var serviceObj), "GetService");
            var capture = (IAudioCaptureClient)serviceObj;
            Check(_client.Start(), "Start");

            while (!_cts.IsCancellationRequested)
            {
                bufferReady.WaitOne(100);
                while (capture.GetNextPacketSize(out var packetFrames) >= 0 && packetFrames > 0)
                {
                    Check(capture.GetBuffer(out var data, out var frames, out var flags, out _, out _), "GetBuffer");
                    var bytes = new byte[frames * BytesPerFrame];
                    const uint SilentFlag = 0x2; // AUDCLNT_BUFFERFLAGS_SILENT: buffer content is undefined
                    if ((flags & SilentFlag) == 0) Marshal.Copy(data, bytes, 0, bytes.Length);
                    _captured.Enqueue(bytes);
                    Check(capture.ReleaseBuffer(frames), "ReleaseBuffer");
                }
            }
            _client.Stop();
        }
        catch (Exception ex) when (!_cts.IsCancellationRequested)
        {
            // Capture died (process gone, device change): the writer keeps
            // padding silence, so the track stays valid to the end.
            Log.Warn($"Game audio capture stopped: {ex.Message}");
        }
    }

    /// Feeds the pipe at exactly real-time rate: captured packets when there
    /// are any, silence for the shortfall. ffmpeg derives timestamps from
    /// byte count, so pacing here IS the sync.
    private void WriterLoop()
    {
        try
        {
            _pipe.WaitForConnection(); // ffmpeg opening its -i argument
            var clock = Stopwatch.StartNew();
            long framesWritten = 0;
            byte[]? partial = null;
            var partialOffset = 0;
            while (!_cts.IsCancellationRequested)
            {
                Thread.Sleep(20);
                var targetFrames = clock.ElapsedMilliseconds * SampleRate / 1000;
                while (framesWritten < targetFrames)
                {
                    var needBytes = (int)(targetFrames - framesWritten) * BytesPerFrame;
                    if (partial is null && !_captured.TryDequeue(out partial))
                    {
                        var silence = new byte[needBytes];
                        _pipe.Write(silence, 0, silence.Length);
                        framesWritten = targetFrames;
                        break;
                    }
                    var take = Math.Min(needBytes, partial!.Length - partialOffset);
                    _pipe.Write(partial, partialOffset, take);
                    framesWritten += take / BytesPerFrame;
                    partialOffset += take;
                    if (partialOffset >= partial.Length) { partial = null; partialOffset = 0; }
                }
            }
            _pipe.Flush();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // ffmpeg closed its end (recording stopped) - normal shutdown.
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _captureThread.Join(TimeSpan.FromSeconds(2));
        _writerThread.Join(TimeSpan.FromSeconds(2));
        _pipe.Dispose();
        if (_client is not null) Marshal.ReleaseComObject(_client);
        _cts.Dispose();
    }

    // --- process-loopback activation (mmdevapi) --------------------------------

    private const int ShareModeShared = 0;

    private static IAudioClient ActivateProcessLoopback(int processId)
    {
        var activationParams = new AudioClientActivationParams
        {
            ActivationType = 1, // AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK
            TargetProcessId = (uint)processId,
            ProcessLoopbackMode = 0, // include the target's process tree
        };
        var paramsSize = Marshal.SizeOf<AudioClientActivationParams>();
        var paramsPtr = Marshal.AllocHGlobal(paramsSize);
        var propVariantPtr = Marshal.AllocHGlobal(Marshal.SizeOf<PropVariantBlob>());
        try
        {
            Marshal.StructureToPtr(activationParams, paramsPtr, false);
            var propVariant = new PropVariantBlob { Vt = 65 /* VT_BLOB */, BlobSize = (uint)paramsSize, BlobData = paramsPtr };
            Marshal.StructureToPtr(propVariant, propVariantPtr, false);

            var handler = new ActivationHandler();
            var iid = typeof(IAudioClient).GUID;
            Check(ActivateAudioInterfaceAsync("VAD\\Process_Loopback", ref iid, propVariantPtr, handler, out var operation),
                "ActivateAudioInterfaceAsync");
            if (!handler.Done.WaitOne(TimeSpan.FromSeconds(5)))
            {
                throw new InvalidOperationException("audio activation timed out");
            }
            Check(operation.GetActivateResult(out var activateHr, out var activated), "GetActivateResult");
            Check(activateHr, "activation");
            return (IAudioClient)activated;
        }
        finally
        {
            Marshal.FreeHGlobal(propVariantPtr);
            Marshal.FreeHGlobal(paramsPtr);
        }
    }

    private static void Check(int hr, string what)
    {
        if (hr < 0) throw new COMException($"{what} failed", hr);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AudioClientActivationParams
    {
        public int ActivationType;
        public uint TargetProcessId;
        public int ProcessLoopbackMode;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropVariantBlob
    {
        public ushort Vt;
        public ushort Reserved1, Reserved2, Reserved3;
        public uint BlobSize;
        public IntPtr BlobData;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct WaveFormatEx
    {
        public ushort FormatTag;
        public ushort Channels;
        public uint SamplesPerSec;
        public uint AvgBytesPerSec;
        public ushort BlockAlign;
        public ushort BitsPerSample;
        public ushort ExtraSize;
    }

    [DllImport("Mmdevapi.dll", ExactSpelling = true)]
    private static extern int ActivateAudioInterfaceAsync(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
        ref Guid riid,
        IntPtr activationParams,
        IActivateAudioInterfaceCompletionHandler completionHandler,
        out IActivateAudioInterfaceAsyncOperation activationOperation);

    /// Agile (free-threaded) so activation can complete without an STA pump.
    private sealed class ActivationHandler : IActivateAudioInterfaceCompletionHandler, IAgileObject
    {
        public ManualResetEvent Done { get; } = new(false);
        public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation) => Done.Set();
    }

    [ComImport, Guid("41D949AB-9862-444A-80F6-C261334DA5EB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IActivateAudioInterfaceCompletionHandler
    {
        void ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation);
    }

    [ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IActivateAudioInterfaceAsyncOperation
    {
        [PreserveSig]
        int GetActivateResult(out int activateResult, [MarshalAs(UnmanagedType.IUnknown)] out object activatedInterface);
    }

    [ComImport, Guid("94EA2B94-E9CC-49E0-C0FF-EE64CA8F5B90"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAgileObject
    {
    }

    [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioClient
    {
        [PreserveSig] int Initialize(int shareMode, uint streamFlags, long bufferDuration, long periodicity, ref WaveFormatEx format, IntPtr audioSessionGuid);
        [PreserveSig] int GetBufferSize(out uint bufferFrames);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out uint padding);
        [PreserveSig] int IsFormatSupported(int shareMode, ref WaveFormatEx format, IntPtr closestMatch);
        [PreserveSig] int GetMixFormat(out IntPtr format);
        [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr eventHandle);
        [PreserveSig] int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }

    [ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioCaptureClient
    {
        [PreserveSig] int GetBuffer(out IntPtr data, out uint frames, out uint flags, out long devicePosition, out long qpcPosition);
        [PreserveSig] int ReleaseBuffer(uint frames);
        [PreserveSig] int GetNextPacketSize(out uint frames);
    }
}
