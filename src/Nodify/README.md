# Nodify, vendored

This directory holds a **fork of a third-party library, committed as source**. It is not something
this project wrote, and `Nodify/` should be read as somebody else's code with a small patch on top.

| | |
|---|---|
| Upstream | <https://github.com/BAndysc/nodify-avalonia> |
| Tag | `v6.6.0`, commit `af468753371d075abb479f75ed5c2c35ad95272d` (2026-02-14) |
| Published as | `NodifyAvalonia` 6.6.0 on nuget.org |
| Licence | MIT. Full text in `LICENSE` beside this file, and recorded in `THIRD-PARTY-NOTICES.md` §h |
| Vendored | 2026-09-20 |
| Copied | the whole of upstream's `Nodify/` project directory, 128 files, plus its root `LICENSE` |
| Changed | 10 of those files, +127 / -59 |

`NodifyAvalonia` is itself a port of WPF [Nodify](https://github.com/miroiu/nodify) by Miroiu
Emanuel, tracked by merging upstream tags. `docs/rule-graph/design.md` §9 decision 7 chose this port
over `trrahul/Nodify.Avalonia` for that reason: a fork of it re-merges from upstream rather than
diverging permanently.

## Why this is vendored rather than referenced

The published 6.6.0 package **cannot run on Avalonia 12 at all**. It targets `netstandard2.0`;
Avalonia 12.1.2 dropped its `netstandard2.0` target, so NuGet happily feeds an assembly built
against Avalonia 11 to a `net10.0` consumer and nothing resolves a typeref until load time. The
first method that touches a Nodify type then throws, before any XAML is parsed:

```
TypeLoadException: Could not load type 'Avalonia.Controls.Primitives.IScrollable'
                   from assembly 'Avalonia.Controls'
```

`IScrollable` moved to `Avalonia.Base.dll` with no type forwarder and `NodifyEditor` implements
`ILogicalScrollable`, whose base typeref is baked into the shipped `Nodify.dll`. The spike behind
`docs/rule-graph/design.md` §6.5 ran a metadata resolver over every Avalonia-scoped typeref and
memberref in the published `Nodify.dll` and found 8 type-level and 6 member-level breaks against
12.1.2, and zero against 11.3.12. Any one of them is fatal.

`v6.6.0` is the tip of upstream's `avalonia_port` branch and there is no Avalonia 12 tag, so there
is nothing to wait for. Re-evaluate if upstream publishes one.

## What was changed

Ten files. Every one of them carries a `VENDORED` comment at the point it diverges, so the reason is
next to the code rather than only here.

### The retarget itself

| File | Change |
|---|---|
| `Nodify/Nodify.csproj` | `netstandard2.0` → `net10.0`; `Avalonia` version comes from this repository's central package management (12.1.2) instead of upstream's `$(AvaloniaVersion)` (11.1.0); signing, packing and the icon/readme `None` items dropped; this repository's analyzer and warning policy for third-party source added |

### Avalonia 12 API moves

| File | Change |
|---|---|
| `Nodify/GlobalUsings.cs` | `GotFocusEventArgs` is now `Avalonia.Input.FocusChangedEventArgs`. Absorbed with a `global using` alias, in the same style as the file's existing `ModifierKeys` and `UIElement` aliases, so the two `Compatibility/Commands` files that name the type stay byte-identical to upstream. The duplicate `global using Avalonia.Interactivity;` was also removed (CS0105) |
| `Nodify/NodifyEditor.Scrolling.cs` | Avalonia 12's `IScrollable` declares `CanHorizontallyScroll` / `CanVerticallyScroll`, and `ILogicalScrollable` now hides them rather than inheriting, so both interfaces need an explicit implementation. The `IScrollable` pair reads the `ILogicalScrollable` pair, so there is still one stored value |
| `Nodify/NodifyEditor.cs` | `ItemContainerGenerator.ContainerFromIndex` is gone (it was `[Obsolete("Use ItemsControl.ContainerFromIndex")]` in 11.3 and forwarded straight to it) → `ContainerFromIndex`. `Visual.GetVisualRoot()` is gone and `Visual.VisualRoot` is internal → `TopLevel.GetTopLevel(editor)?.RenderScaling`. `Gestures` is internal → the same event off `InputElement`. `public new void BringIntoView(Rect)` no longer hides anything (CS0109) |
| `Nodify/Helpers/SelectionHelper.cs` | Same `ContainerFromIndex` move, four call sites |
| `Nodify/Compatibility/Commands/RoutedCommand.cs` | `IHostedVisualTreeRoot` is internal, so the hop out of a popup cannot go through it. Replaced with `PopupHostOf`, built from the public `PopupRoot.Parent`, `PopupRoot.ParentTopLevel` and `TopLevel.GetTopLevel` |

### This repository's build policy meeting upstream's warnings

These are not Avalonia 12 fallout. Pristine `v6.6.0` emits the same families against Avalonia
11.1.0, 11.3.12 and 12.1.2; they only matter here because `Directory.Build.props` turns warnings
into errors.

| File | Change |
|---|---|
| `Nodify/Helpers/UnscaleTransformConverter.cs` | Three converter signatures took non-nullable `object` where `IValueConverter` / `IMultiValueConverter` declare `object?` (CS8767). `ScalePointConverter` in the same file was already written the correct way upstream |
| `Nodify/Compatibility/Commands/CommandBinding.cs` | Two optional parameters defaulted to `null` while typed non-nullable (CS8625) |

Everything else upstream warns about is suppressed rather than edited, through an enumerated
`NoWarn` list in the csproj. Blanket-suppressing, or re-styling 128 files of someone else's code to
this repository's conventions, would make every future merge a manual reconciliation.

### The XAML break, which is the one to watch

| File | Change |
|---|---|
| `Nodify/Themes/Styles/Minimap.xaml` | Five bindings |
| `Nodify/Themes/Styles/NodifyEditor.xaml` | One binding |

Inside a nested `ItemsPanelTemplate`, the templated parent is now the `ItemsPresenter` rather than
the outer `ControlTemplate`'s target, so `{TemplateBinding X}` and
`{Binding X, RelativeSource={RelativeSource TemplatedParent}}` resolve against the wrong object:

```
AVLN2000: Unable to resolve suitable regular or attached property ViewportSize
          on type Avalonia.Controls:Avalonia.Controls.Presenters.ItemsPresenter
```

Both files are rewritten to `RelativeSource={RelativeSource AncestorType=local:...}`, which is what
upstream already uses for the same job in `NodifyEditor.xaml`'s `DecoratorsControl` panel.

**This is the change a clean textual merge can silently get wrong** (risk R15 in
`docs/rule-graph/design.md` §8). It is a semantic change in binding resolution, not an API rename:
upstream's spelling still parses, so re-applying an upstream revision of either file compiles and
then fails at XAML load. Any merge that touches an `ItemsPanelTemplate` has to be re-read by hand.

Measured while vendoring, and worth knowing: this break is **not new in Avalonia 12**. Pristine
`v6.6.0` produces the same six `AVLN2000` errors against **Avalonia 11.3.12** and none against
11.1.0, upstream's own pin. It arrived somewhere in the 11.2 / 11.3 line.

## What was lost

Nothing, so far. The spike recorded in `docs/rule-graph/design.md` §6.5 expected to lose two
capabilities because their APIs went internal. Both turned out to have public replacements:

- **Touchpad pinch-zoom.** `Gestures` is internal in Avalonia 12, but
  `PointerTouchPadGestureMagnifyEvent` simply moved to `InputElement`, where it is public. The
  editor still subscribes to it.
- **The popup hop in command routing.** `IHostedVisualTreeRoot` is internal, and so is the
  `Visual.VisualRoot` its implementation reads, but `PopupRoot.Parent`, `PopupRoot.ParentTopLevel`
  and `TopLevel.GetTopLevel` are all public and say the same thing. `PopupHostOf` reproduces
  Avalonia's own `Host` getter from them.

`PopupHostOf` is a re-implementation of an internal, which is the one place here that can rot
quietly. Re-read it against Avalonia's `PopupRoot.cs` whenever Avalonia's major version moves.

## The guard

`DemoViewer.NET.Nodify.Tests/` exists because **compiling Nodify proves nothing**: the
published package compiles clean against Avalonia 12 and then fails at load. The suite runs at
three depths, cheapest first:

1. every type in the assembly loads and every member signature resolves, with no Avalonia
   application at all, which is the layer that catches the published package's exact failure;
2. `avares://Nodify/Theme.axaml` merges and all 19 type-keyed `ControlTheme`s plus its three named
   ones resolve, under both the Light and the Dark variant;
3. a `NodifyEditor` with an `ItemsSource` realises real `ItemContainer`s, each applying its template
   around the `Node` the item template built, and the whole thing rasterises to a frame that is not
   the background colour.

It runs at the `full` tier: `scripts/test.sh -t full -p nodify`.

## Re-merging a new upstream release

1. `git clone --branch <tag> https://github.com/BAndysc/nodify-avalonia`.
2. Diff the new `Nodify/` against `src/Nodify/Nodify/` ignoring line endings
   (`diff -ru --strip-trailing-cr`). The ten files in the tables above are the ones with local
   changes; everything else should apply as a straight copy.
3. Re-apply the csproj's retarget and policy blocks, which are entirely ours.
4. Re-read every `ItemsPanelTemplate` in the incoming XAML by hand. See the warning above.
5. `scripts/test.sh -t full -p nodify`. A green build means nothing on its own.
