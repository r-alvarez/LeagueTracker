# Third-party notices — LeagueTracker agent

The agent zip and Setup.exe bundle the following third-party software.
Nothing below is endorsed by or affiliated with LeagueTracker.

## FFmpeg (`ffmpeg.exe`)

Copyright (c) the FFmpeg developers. Licensed under the **GNU General Public
License, version 3 or later** (the bundled binary is a "full" build from
gyan.dev configured with `--enable-gpl --enable-version3`, so GPLv3 applies
to the whole binary). FFmpeg is a separate program that the agent runs as a
child process; the agent itself is not a derivative work of it.

- Licence text: https://www.gnu.org/licenses/gpl-3.0.html
- Version: run `ffmpeg.exe -version` next to the agent (the release workflow
  pins it — see `.github/workflows/agent-release.yml` in the repository).
- Corresponding source: https://ffmpeg.org/releases/ (the exact release
  version) and the build configuration printed by `ffmpeg.exe -version`,
  as built by https://www.gyan.dev/ffmpeg/builds/. If you cannot obtain the
  source from those locations, LeagueTracker will supply it on request via
  the repository's issue tracker, as GPLv3 §6 requires.

## ScreenRecorderLib (`ScreenRecorderLib.dll`)

Copyright (c) 2018 Sverre Kristoffer Skodje. Licensed under the **MIT
License**. https://github.com/sskodje/ScreenRecorderLib

**Modified.** The bundled build is upstream v6.6.0 plus LeagueTracker's HDR
tone-mapping patch (`deploy/screenrecorderlib/hdr-tonemap.patch` in the
repository, MIT-licensed as well). Its `WindowsGraphicsCapture` path captures
HDR desktops as scRGB and tone-maps them to SDR on the GPU.

    MIT License

    Copyright (c) 2018 Sverre Kristoffer Skodje

    Permission is hereby granted, free of charge, to any person obtaining a copy
    of this software and associated documentation files (the "Software"), to deal
    in the Software without restriction, including without limitation the rights
    to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
    copies of the Software, and to permit persons to whom the Software is
    furnished to do so, subject to the following conditions:

    The above copyright notice and this permission notice shall be included in all
    copies or substantial portions of the Software.

    THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
    IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
    FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
    AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
    LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
    OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
    SOFTWARE.

## .NET runtime

The agent is published self-contained and carries the Microsoft .NET runtime,
licensed under the MIT License. https://github.com/dotnet/runtime/blob/main/LICENSE.TXT

## Inno Setup (Setup.exe only)

The installer is built with Inno Setup, copyright (c) Jordan Russell and
Martijn Laan, distributed under the Inno Setup License, which permits
redistribution of installers built with it. https://jrsoftware.org/files/is/license.txt
