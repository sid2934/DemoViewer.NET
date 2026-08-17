#region

using DemoViewer.NET.ViewModels.Diagnostics;
using Microsoft.Extensions.Logging;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     <see cref="DiagnosticsTelemetryHub" /> coverage. It is the App-side source of truth for BOTH
///     the internal ILogger pillar and the CSVG host logs, bound by the Diagnostics tab and mirrored
///     into the Output drawer. Facts under test: it stays bounded to a LIVE cap (the unbounded-log
///     leak this mirrors was a real regression), <see cref="DiagnosticsTelemetryHub.Enqueue" />
///     coalesces onto the injected UI-post and drains, and Clear resets it. Tests inject a synchronous
///     UI-post so no Avalonia dispatcher is required.
/// </summary>
public class DiagnosticsTelemetryHubTests
{
    private static TelemetryLogRow Row(string source, LogLevel level, string message) =>
        new(source, level, level.ToString(), "Test.Category", message, "00:00:00");

    // Synchronous UI-post so Enqueue drains inline under test.
    private static DiagnosticsTelemetryHub NewHub(int maxRows = 5000) =>
        new(() => maxRows, a => a());

    [Test]
    public async Task RingCap_DropsOldest_StaysBounded()
    {
        DiagnosticsTelemetryHub hub = NewHub();
        for (int i = 0; i < 5200; i++)
        {
            hub.AppendOnUiThread(Row("Analysis", LogLevel.Information, $"row-{i}"));
        }

        await Assert.That(hub.Logs.Count).IsEqualTo(5000);
        await Assert.That(hub.Logs[^1].Message).IsEqualTo("row-5199");
        await Assert.That(hub.Logs[0].Message).IsEqualTo("row-200"); // 0..199 evicted
    }

    [Test]
    public async Task Cap_IsReadLive()
    {
        int cap = 10;
        // ReSharper disable once AccessToModifiedClosure — deliberate: cap is read live per append.
        DiagnosticsTelemetryHub hub = new(() => cap, a => a());
        for (int i = 0; i < 20; i++)
        {
            hub.AppendOnUiThread(Row("App", LogLevel.Information, $"r{i}"));
        }

        await Assert.That(hub.Logs.Count).IsEqualTo(10);

        // Tightening the cap takes effect on the next append (the ring trims down to the new cap).
        cap = 3;
        hub.AppendOnUiThread(Row("App", LogLevel.Information, "r20"));
        await Assert.That(hub.Logs.Count).IsEqualTo(3);
        await Assert.That(hub.Logs[^1].Message).IsEqualTo("r20");
    }

    [Test]
    public async Task Enqueue_Coalesces_AndDrainsAllRows()
    {
        int posts = 0;
        // Count how many times a drain is scheduled: a burst enqueued before any drain runs should
        // schedule exactly one. We post synchronously but only after enqueuing the whole burst by
        // deferring the action into a list first.
        List<Action> deferred = [];
        DiagnosticsTelemetryHub hub = new(() => 5000, a =>
        {
            posts++;
            deferred.Add(a);
        });

        hub.Enqueue(Row("Analysis", LogLevel.Information, "a"));
        hub.Enqueue(Row("Analysis", LogLevel.Information, "b"));
        hub.Enqueue(Row("CSVG", LogLevel.Warning, "c"));

        // One coalesced drain scheduled for the burst (subsequent enqueues saw drainScheduled == 1).
        await Assert.That(posts).IsEqualTo(1);

        foreach (Action drain in deferred)
        {
            drain();
        }

        await Assert.That(hub.Logs.Count).IsEqualTo(3);
        await Assert.That(hub.Logs[^1].Message).IsEqualTo("c");
    }

    [Test]
    public async Task RowsAppended_FiresPerBatch()
    {
        DiagnosticsTelemetryHub hub = NewHub();
        int batches = 0;
        int rows = 0;
        hub.RowsAppended += b =>
        {
            batches++;
            rows += b.Count;
        };

        hub.AppendOnUiThread(Row("Analysis", LogLevel.Warning, "a"));
        hub.AppendOnUiThread(Row("CSVG", LogLevel.Error, "b"));

        await Assert.That(batches).IsEqualTo(2); // direct appends = one batch each
        await Assert.That(rows).IsEqualTo(2);
    }

    [Test]
    public async Task Clear_ResetsRows()
    {
        DiagnosticsTelemetryHub hub = NewHub();
        hub.AppendOnUiThread(Row("Analysis", LogLevel.Information, "x"));
        hub.AppendOnUiThread(Row("CSVG", LogLevel.Information, "y"));

        hub.Clear();

        await Assert.That(hub.Logs.Count).IsEqualTo(0);
    }
}
