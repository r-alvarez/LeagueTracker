# ScreenRecorderLib with HDR tone-mapping

The agent's WGC capture engine is [ScreenRecorderLib](https://github.com/sskodje/ScreenRecorderLib)
6.6.0 (MIT). Stock, it asks Windows Graphics Capture for 8-bit BGRA frames
whatever the display is doing; on an HDR desktop that is the *clamp* of scRGB,
not the picture - SDR content is fine, but anything Auto HDR / RTX HDR / a
native-HDR game pushes above SDR white clips to white and the VOD plays
bleached (Ben's recordings, 17 Aug 2026). Upstream forces the 8-bit format
([PR #97](https://github.com/sskodje/ScreenRecorderLib/pull/97)) and has the
Auto HDR complaint open ([#313](https://github.com/sskodje/ScreenRecorderLib/issues/313)).

`hdr-tonemap.patch` (against tag v6.6.0, commit `39ad1e2f`) makes the WGC path:

- probe the captured display's colour mode via DXGI (`IDXGIOutput6::GetDesc1`)
  and its SDR white level via `DisplayConfigGetDeviceInfo`;
- on an HDR desktop, create the frame pool as `R16G16B16A16Float` (scRGB) and
  draw each frame through `HdrToneMapShader.hlsl` into the usual BGRA8 texture:
  divide by SDR white (SDR content round-trips to its exact 8-bit values), roll
  off everything above a 0.85 knee towards the display peak with an
  extended-Reinhard curve on max(R,G,B) (hue kept, no clip), sRGB-encode;
- keep every SDR desktop, and everything downstream of the frame, unchanged.

Kill switch: the environment variable `SCREENRECORDERLIB_HDR_TONEMAP=0` in the
host process forces the stock 8-bit behaviour. The agent sets it from
`HdrToneMap: false` (appsettings / `LT_HDR_TONEMAP=0`), and the tracker can
push that to every agent that hasn't set it locally with
`Agent__Profile__HdrToneMap=false` on the container - the remote off switch
if a build misbehaves on someone's PC.

`build.ps1` clones the pinned commit, applies the patch and builds the x64
Release C++/CLI DLL into `out/`; `publish-agent.ps1` ships that over the NuGet
copy when present. The release workflow runs it on GitHub's Windows runner
(which has the C++/CLI toolset); a VS 2022/2026 install with the C++ workload
builds it locally too. Bumping upstream = re-pin `$commit` in
`build.ps1`, re-apply the patch, fix conflicts, regenerate with
`diff -ruN a b` (a = pristine, b = patched, `git apply -p1`).
