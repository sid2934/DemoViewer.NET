namespace DemoViewer.NET.Modules.Playback2D.Timeline;

/// <summary>One entry of a timeline band's right-click menu: what it says and what it does.</summary>
/// <param name="Header">The menu text.</param>
/// <param name="Run">What the click does.</param>
public sealed record MenuEntry(string Header, Action Run);
