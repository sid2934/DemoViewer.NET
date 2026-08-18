#region

using System.Runtime.CompilerServices;

#endregion

// Surface internal seams (e.g. PlaybackController.AuthoritativeTracker) to the headless UI test
// assembly so framework-correctness tests can assert instance identity / ownership without widening
// the public API. Mirrors the InternalsVisibleTo convention in CS2DemoKit.Parser.EntityTracking.
[assembly: InternalsVisibleTo("DemoViewer.NET.App.Tests")]
// UiCapture renders design variants over the same internal test seams (e.g. the Playback2D
// vision engine's synchronous load hook) — headless Avalonia can't pump the async production path.
[assembly: InternalsVisibleTo("DemoViewer.NET.UiCapture")]
