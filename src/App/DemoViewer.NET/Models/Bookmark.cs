namespace DemoViewer.NET.Models;

/// <summary>
///     A user bookmark on a frame (F8.5 / A4). IDA/Ghidra pattern: "bookmark as 'first round_start
///     with a phantom entity'". Persisted to <c>SessionState.json</c> on desktop; in-memory only on
///     WASM (no filesystem). <see cref="Tick" /> is captured for display; <see cref="FrameIndex" /> is
///     the seek target.
/// </summary>
public sealed record Bookmark(int FrameIndex, int Tick, string Label);
