#region

using DemoViewer.NET.Services.Diagnostics;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     <see cref="DiagnosticsFileLog" /> coverage — the rolling file mirror of the diagnostics logs.
///     Facts under test: it writes lines to <c>diagnostics.log</c>, rolls to <c>diagnostics.N.log</c>
///     at the byte cap, and retains at most <c>maxFiles</c> rolled files (bounded disk). Uses the
///     internal directory seam so no real app-data path is touched. Dispose drains the async pump.
/// </summary>
public class DiagnosticsFileLogTests
{
    private static string NewTempDir()
    {
        // Deterministic per-test dir under the OS temp root (no Guid — Math.random is unavailable in
        // some harness contexts, and the dir is cleaned each run).
        string dir = Path.Combine(Path.GetTempPath(), "dvnet-difilelog-test",
            TestContext.Current?.TestDetails.TestId ?? "default");
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, true);
        }

        Directory.CreateDirectory(dir);
        return dir;
    }

    [Test]
    public async Task WritesLines_ToActiveFile()
    {
        string dir = NewTempDir();
        try
        {
            DiagnosticsFileLog? log = DiagnosticsFileLog.TryCreateInDirectory(dir, () => 1_000_000, () => 3);
            await Assert.That(log).IsNotNull();

            log!.Write("hello");
            log.Write("world");
            log.Dispose(); // drains the pump + flushes

            string active = Path.Combine(dir, "diagnostics.log");
            await Assert.That(File.Exists(active)).IsTrue();
            string[] lines = File.ReadAllLines(active);
            await Assert.That(lines).Contains("hello");
            await Assert.That(lines).Contains("world");
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Test]
    public async Task ReadTail_Works_WhileSinkHoldsFileOpen()
    {
        // The copy-diagnostics path reads the log file while the sink still holds it open for writing
        // (a demo has been loaded). This must NOT throw a sharing violation and must return content.
        string dir = NewTempDir();
        try
        {
            DiagnosticsFileLog? log = DiagnosticsFileLog.TryCreateInDirectory(dir, () => 1_000_000, () => 3);
            await Assert.That(log).IsNotNull();

            log!.Write("alpha");
            log.Write("bravo");
            // Give the async pump a beat to flush without disposing (sink stays open).
            await Task.Delay(150);

            string active = Path.Combine(dir, "diagnostics.log");
            List<string> tail = DiagnosticsFileLog.ReadTail(active, 100);

            await Assert.That(tail).Contains("alpha");
            await Assert.That(tail).Contains("bravo");

            log.Dispose();
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Test]
    public async Task Rolls_AndRetainsAtMostMaxFiles()
    {
        string dir = NewTempDir();
        try
        {
            // Tiny byte cap (10 bytes) forces a roll on nearly every line; keep 2 rolled files.
            DiagnosticsFileLog? log = DiagnosticsFileLog.TryCreateInDirectory(dir, () => 10, () => 2);
            await Assert.That(log).IsNotNull();

            for (int i = 0; i < 20; i++)
            {
                log!.Write($"line-{i}-padding-to-exceed-cap");
            }

            log!.Dispose();

            // Bounded disk: active (may be transiently absent right after a roll) + at most maxFiles
            // (2) rolled files → never more than 3 total.
            string[] logFiles = Directory.GetFiles(dir, "diagnostics*.log");
            await Assert.That(logFiles.Length).IsLessThanOrEqualTo(3);
            // Rolling actually happened (a rolled file exists)…
            await Assert.That(File.Exists(Path.Combine(dir, "diagnostics.1.log"))).IsTrue();
            // …and the retention cap held (the 3rd rolled file was never kept).
            await Assert.That(File.Exists(Path.Combine(dir, "diagnostics.3.log"))).IsFalse();
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
