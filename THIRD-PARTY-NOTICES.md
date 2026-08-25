# Third-Party Notices

This repository is licensed under the MIT License (see `LICENSE`). It also contains code
adapted from a third-party MIT-licensed project, and generates code from third-party protobuf
definitions. Both are documented below.

## a. demofile-net (MIT)

Eight files in the parser ship code adapted from
[demofile-net](https://github.com/saul/demofile-net), an MIT-licensed CS2/Source demo parser.
These files all ship in the `Cs2DemoKit.Parser` assembly (`src/Parser/Cs2DemoKit.Parser/`), which
is published as the `Cs2DemoKit.Parser` package:

- `BitBuffer.cs`
- `RuntimeField.cs`
- `EntityTracker.cs`
- `HuffmanNode.cs`
- `FieldDecoderFactory.cs`
- `FieldPathEncoding.cs`
- `FieldPath.cs`
- `FieldEncodingInfo.cs`

The upstream license text, reproduced in full:

```text
The MIT License (MIT)

Copyright (c) 2023 Saul Rennison

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
```

## b. Valve protobuf definitions

The parser assembly contains C# code generated from protobuf definitions originating from
Valve's Counter-Strike 2, as tracked by [SteamDatabase/GameTracking-CS2](https://github.com/SteamDatabase/GameTracking-CS2)
(consumed via the `sid2934/CS2-OpenDevDocs` submodule). These definitions describe a wire
format. Valve Corporation owns Counter-Strike 2 and its data formats. This project is
unaffiliated with Valve. No game assets are redistributed in the packages.

## c. Other package dependencies

This project depends on the following NuGet packages, each under its own license, resolved
via NuGet and not reproduced here: Google.Protobuf, Snappier, YamlDotNet,
Microsoft.Extensions.Logging.Abstractions, CS2OpenDev.Sdk.

## d. Inter font (SIL Open Font License 1.1)

`src/Playback2D/DemoViewer.NET.Playback2D.Core/Assets/Inter-Regular.ttf` is the Inter Regular
typeface by Rasmus Andersson (<https://rsms.me/inter/>), redistributed under the
[SIL Open Font License, Version 1.1](https://openfontlicense.org/). It is embedded in the
Playback2D core assembly and used by `TextBlobCache` to draw scene labels.

An embedded face is a correctness requirement, not a preference: the golden-image lane must
rasterise identically on a developer's Windows machine and on the Linux CI runner, and
`SKTypeface.Default` / a family-name lookup resolve to different fonts on each. The bytes were
extracted from the `Avalonia.Fonts.Inter` package this repository already depends on, so the
scene renders in the same face as the rest of the application.

The OFL requires that the font not be sold by itself, that this notice accompany it, and that any
modified version be renamed. This project redistributes it unmodified.

## e. ANGLE (BSD-3-Clause)

The Windows build links ANGLE (`av_libglesv2.dll`) **in-process** to create windowless EGL /
OpenGL ES contexts for GPU-accelerated offscreen rendering (2D playback video export and headless
rendering — see `docs/playback2d-v2/design.md` §5.8). The binary is redistributed exactly as
published in the `Avalonia.Angle.Windows.Natives` NuGet package, built from
<https://github.com/AvaloniaUI/angle> (commit `cb8b4e1307a9d8f5ff56b8c5973bea4158ffead8`).
Upstream project: <https://github.com/google/angle>.

Because the linkage is in-process rather than a separate program, the obligation is to reproduce
the copyright notice, the licence conditions and the disclaimer — which this section does. Nothing
is bundled on Linux or macOS: there, EGL (when present at all) is a system library supplied by the
driver stack, and its absence simply makes the probe fall back to the CPU provider.

```
Copyright 2018 The ANGLE Project Authors.
All rights reserved.

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions
are met:

    Redistributions of source code must retain the above copyright
    notice, this list of conditions and the following disclaimer.

    Redistributions in binary form must reproduce the above
    copyright notice, this list of conditions and the following
    disclaimer in the documentation and/or other materials provided
    with the distribution.

    Neither the name of TransGaming Inc., Google Inc., 3DLabs Inc.
    Ltd., nor the names of their contributors may be used to endorse
    or promote products derived from this software without specific
    prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS
"AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT
LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS
FOR A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE
COPYRIGHT OWNER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT,
INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING,
BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER
CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT
LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN
ANY WAY OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF THE
POSSIBILITY OF SUCH DAMAGE.
```
