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
