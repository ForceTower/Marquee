# Third-party notices

Marquee is licensed under the GNU General Public License v3.0 (see [LICENSE](LICENSE)). It includes
or uses the third-party work below.

## Included in this repository

`src/Marquee/Themes/ToggleSwitch.xaml` is adapted from the Windows Community Toolkit's
`RightAlignedCompactToggleSwitchStyle`, which is itself WinUI's default `ToggleSwitch` template with
a different layout.

### Windows Community Toolkit

https://github.com/CommunityToolkit/Windows

Copyright © .NET Foundation and Contributors

All rights reserved.

MIT License (MIT)

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files (the “Software”), to deal in the Software without restriction, including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED *AS IS*, WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NON-INFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.

### WinUI

https://github.com/microsoft/microsoft-ui-xaml

MIT License

Copyright (c) Microsoft Corporation. All rights reserved.

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
SOFTWARE

## Downloaded or restored at build time, and bundled with builds

None of these are in the repository. The `tools/fetch-*.ps1` and `tools/build-libass.ps1` scripts,
Cargo and NuGet fetch them, each under its own license.

| Component | License | Source |
| --- | --- | --- |
| mpv (libmpv) | GPL-2.0-or-later (`tools/fetch-libmpv.ps1` downloads a GPL build) | https://mpv.io, builds by https://github.com/shinchiro/mpv-winbuild-cmake |
| FFmpeg (avcodec, avformat, avutil, swresample) | LGPL-2.1-or-later (shared LGPL build) | https://ffmpeg.org, builds by https://github.com/BtbN/FFmpeg-Builds |
| `truehd` crate | Apache-2.0 | https://github.com/truehdd/truehdd |
| `dolby_vision` crate | MIT | https://github.com/quietvoid/dovi_tool |
| libass | ISC (built into `libass.dll` by `tools/build-libass.ps1`) | https://github.com/libass/libass |
| FreeType | FreeType License or GPL-2.0-or-later (dual), plus its bundled zlib (Zlib); statically linked into `libass.dll` | https://freetype.org |
| FriBidi | LGPL-2.1-or-later (statically linked into `libass.dll`) | https://github.com/fribidi/fribidi |
| HarfBuzz | MIT ("Old MIT"; statically linked into `libass.dll`) | https://github.com/harfbuzz/harfbuzz |
| libunibreak | Zlib (statically linked into `libass.dll`) | https://github.com/adah1972/libunibreak |
| FFmpeg.AutoGen | MIT | https://github.com/Ruslan-B/FFmpeg.AutoGen |
| Microsoft.Data.Sqlite | MIT | https://github.com/dotnet/efcore |
| System.Security.Cryptography.ProtectedData | MIT | https://github.com/dotnet/runtime |
| CommunityToolkit.Mvvm | MIT | https://github.com/CommunityToolkit/dotnet |
| Windows App SDK | Microsoft Software License Terms | https://github.com/microsoft/WindowsAppSDK |
