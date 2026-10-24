# DemoViewer.NET.GameIcons

The CS2 iconography DemoViewer.NET draws: weapons, kill modifiers, competitive and Wingman ranks, Premier
emblems, map icons and a few UI marks. The PNGs and their manifest are embedded in the assembly at every
baked scale, so the catalogue works wherever the assembly loads, the browser build included.

It has no dependencies. `IconCatalogue.Get(key)` returns an `IconRef` with the icon's intrinsic size and
whether it can be tinted; `IconRef.Open(scale)` and `IconRef.Bytes(scale)` hand back the PNG.

This assembly is not published to NuGet: the artwork is Valve's, so it travels inside the application
only (see THIRD-PARTY-NOTICES.md section b2). The `GameIcon` control in `DemoViewer.NET.Extensions.Sdk.Ui`
draws these icons for an extension's XAML, through a backend the app installs rather than a reference to
this project, which is why the SDK packages carry none of the artwork.
