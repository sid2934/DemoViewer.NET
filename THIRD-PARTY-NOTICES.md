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

Two of them carry terms worth stating outright because the 2D-playback video export depends on
them:

- **FFMpegCore** (MIT) — a managed argument builder and process wrapper. It links no ffmpeg code;
  it starts ffmpeg as a **separate program** and pipes raw frames to its standard input. See §e.
- **SixLabors.ImageSharp** (Six Labors Split License 1.0) — used only by `ManagedGifSink`, the
  no-ffmpeg GIF floor. The split license grants Apache-2.0 terms to open-source projects, which
  this repository is (MIT — see `LICENSE`). A closed-source redistribution of this code would need
  a commercial Six Labors license; record that before any such change.

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

## e. ffmpeg (not redistributed)

DemoViewer **ships no ffmpeg binary and links no ffmpeg code**. The 2D-playback video export
(`docs/playback2d-v2/export.md`) invokes `ffmpeg` as a **separate program**, writing raw RGBA
frames to its standard input over a pipe and reading nothing back but its exit status. Under the
FSF's own reading, two programs communicating over a pipe are separate works, which is what keeps
this repository's MIT licence unaffected by ffmpeg's GPL/LGPL terms whichever build a user has
installed.

ffmpeg is resolved in this order:

1. an `ffmpeg` already on the user's `PATH` — whatever they installed, under whatever licence that
   build carries;
2. an optional, explicitly consented in-app download of a **pinned LGPL-2.1 build** produced by the
   [BtbN/FFmpeg-Builds](https://github.com/BtbN/FFmpeg-Builds) project, verified against a pinned
   SHA-256 and extracted under `<config>/tools/ffmpeg`. The consent sheet displays the `LICENSE.txt`
   found inside that archive and links the build's source before anything is written to disk;
3. no ffmpeg at all — export falls back to `ManagedGifSink`, which uses ImageSharp (§c) and
   produces GIF only.

Because the downloaded build is LGPL, it contains no H.264 encoder: MP4/H.264 export requires a
GPL ffmpeg the user installed themselves, and the export dialog says so rather than failing at
encode time. WebM/VP9 — the default format — is present in the LGPL build.

ffmpeg is a trademark of Fabrice Bellard, originator of the FFmpeg project. This project is
unaffiliated with it.
