# DemoViewer.NET Module Abstractions

The read-only playback contract a DemoViewer.NET module sees: `IModuleContext`, the entity and player views,
and the snapshot pushed on every frame. It references no other package.

Extension authors install `DemoViewer.NET.Extensions.Sdk`, which depends on this package.
