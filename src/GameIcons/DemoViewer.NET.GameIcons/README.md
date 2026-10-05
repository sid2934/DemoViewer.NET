# DemoViewer.NET.GameIcons

The CS2 iconography DemoViewer.NET draws: weapons, kill modifiers, competitive and Wingman ranks, Premier
emblems, map icons and a few UI marks. The PNGs and their manifest are embedded in the assembly at every
baked scale, so the catalogue works wherever the assembly loads, the browser build included.

It has no dependencies. `IconCatalogue.Get(key)` returns an `IconRef` with the icon's intrinsic size and
whether it can be tinted; `IconRef.Open(scale)` and `IconRef.Bytes(scale)` hand back the PNG.

An extension that draws icons in XAML uses the `GameIcon` control from `DemoViewer.NET.Extensions.Sdk.Ui`,
which depends on this package.
