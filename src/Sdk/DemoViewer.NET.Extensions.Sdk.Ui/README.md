# DemoViewer.NET Extensions SDK: UI kit

The controls, theme names and view-model base an extension's views use so they look like the app they run
in. It builds on `DemoViewer.NET.Extensions.Sdk` and is versioned with it.

```xml
<ItemGroup>
  <PackageReference Include="DemoViewer.NET.Extensions.Sdk" Version="1.1.*" />
  <PackageReference Include="DemoViewer.NET.Extensions.Sdk.Ui" Version="1.1.*" />
  <PackageReference Include="Avalonia" Version="12.1.2" />
  <PackageReference Include="CommunityToolkit.Mvvm" Version="8.4.0" />
</ItemGroup>
```

Reference Avalonia and CommunityToolkit.Mvvm directly: the XAML compiler and the MVVM source generators run
only in a project that references their package itself.
The app ships this assembly, so a copy in your extension's folder is never loaded.

## View models and views

Derive view models from `ExtensionViewModel`, or implement `IExtensionViewModel` from the SDK on your own
`ObservableObject`. The host then builds the view for one by naming wherever it shows it: a pane, a panel, a
status chip flyout or any `ContentControl` in your own views. `MyExtension.ViewModels.StatsViewModel`
resolves to `MyExtension.Views.StatsView`, looked up in the view model's own assembly; the view needs a
public parameterless constructor. A view model without the marker renders as its type name.

## Controls

Namespace `DemoViewer.NET.Extensions.Sdk.Ui.Controls`, in XAML
`xmlns:ui="using:DemoViewer.NET.Extensions.Sdk.Ui.Controls"`.

| Control | What it is |
|---|---|
| `StatusChip`, `StatusChipViewModel` | A dot and a label that opens a flyout. Set `DotState` (`Off`, `Working`, `Good`, `Degraded`, `Error`), `IsPulsing`, `IsHollow`, `Label`, `Tooltip` and `FlyoutContent`; the colour follows the theme. |
| `KeyValueTable`, `KvpRow` | A two-column key/value list; delta rows show the previous value struck through. |
| `MarkdownBlock` | Headings, paragraphs, lists, bold and inline code from a Markdown string. |
| `GameIcon` | A CS2 icon by key (`equipment/ak47`, `modifier/headshot`), sized by `IconHeight` and tinted by `Foreground`. |
| `GifView` | An animated GIF from a file path. |
| `ParseLinkChip`, `OpenExternal` | A monospace link that opens a file in the user's editor or a URL in the browser. |

`DisplayText.Sanitize` strips the invisible Unicode format characters that make Avalonia's line wrapping
throw on some player names; run every player name through it before showing it.
`BulkObservableCollection<T>` replaces or extends its contents with one `Reset` instead of an event per item.

## Theme

The app's palette and shared styles are loaded once for the whole process, so your XAML uses them directly:
`{DynamicResource TextMid}` and `Classes="ghost"`. A misspelt or renamed name resolves to nothing without an
error, so the names are published as constants in `DemoViewer.NET.Extensions.Sdk.Ui.Theming`:

- `ThemeTokens` lists every palette key with its dark and light value. Every key exists in every theme,
  built-in or a user's.
- `StyleClasses` lists every class the shared styles select on, with the controls it styles: `Primary`,
  `Ghost`, `Chip`, `Mono`, `Card`, `SectionLabel`, `ColLabel`, `DataList` and the rest.

```xml
<Border Background="{DynamicResource {x:Static theme:ThemeTokens.PanelBg}}" />
```

Both are generated from the app's own palette and style files when the SDK is built, so they match the app
release the package version ships with. A control that draws itself resolves a token with
`ThemeColors.Get(ThemeTokens.AccentInteractive, ActualThemeVariant, fallback)`.

## Testing your views

`src/Testing/DemoViewer.NET.Extensions.Testing` in the DemoViewer.NET repository holds the headless test
support the app's own suites use: a shared headless session (`HeadlessSession.RunOnUi`) and a recording
`IModuleContext` fake. Import its `DemoViewer.NET.Extensions.Testing.props` from a TUnit test project that
references Avalonia.Headless and Avalonia.Skia, and name your app builder with
`[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]`.
