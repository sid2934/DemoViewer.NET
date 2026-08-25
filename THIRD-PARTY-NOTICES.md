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

## e. perfect-freehand (MIT)

Two files in the Playback2D core assembly ship code adapted from
[perfect-freehand](https://github.com/steveruizok/perfect-freehand) v1.2.2, an MIT-licensed
pressure-sensitive stroke outliner by Steve Ruiz. The adaptation is a direct C# port of
`packages/perfect-freehand/src/getStrokePoints.ts` and
`packages/perfect-freehand/src/getStrokeOutlinePoints.ts`:

- `src/Playback2D/DemoViewer.NET.Playback2D.Core/Ink/FreehandOutline.cs`
- `src/Playback2D/DemoViewer.NET.Playback2D.Core/Ink/FreehandOptions.cs`

Both files carry a header comment pointing at this section. The port is pinned against reference
vectors generated from the upstream package and committed under
`tests/fixtures/playback2d/freehand/`.

The upstream license text, reproduced in full:

```text
MIT License

Copyright (c) 2021 Stephen Ruiz Ltd

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
