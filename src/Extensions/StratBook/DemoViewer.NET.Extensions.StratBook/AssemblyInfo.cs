#region

using System.Runtime.CompilerServices;

#endregion

// The pack's tests and the UiCapture variants read its internals (the lifecycle, the instances tracker)
// the same way the app's do; see the app's AssemblyInfo.
[assembly: InternalsVisibleTo("DemoViewer.NET.App.Tests")]
[assembly: InternalsVisibleTo("DemoViewer.NET.UiCapture")]
