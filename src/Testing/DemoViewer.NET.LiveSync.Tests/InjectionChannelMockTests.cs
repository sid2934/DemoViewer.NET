#region

using System.Threading.Channels;
using Cs2VideoGenerator.Core;
using Cs2VideoGenerator.Core.Models;
using Cs2VideoGenerator.Core.ProcessManagement;
using DemoViewer.NET.Configuration;
using Microsoft.Extensions.Logging;
using TUnit.Core.Exceptions;
using TimeoutException = System.TimeoutException;

#endregion

namespace DemoViewer.NET.LiveSync.Tests;

/// <summary>
///     The injection-channel gate: in-game USER
///     actions — injected into the real mock_server through the v1.1
///     <see cref="IMockUserActionInjector" /> stdin channel — must surface through DV's own
///     hosting stack as <c>DemoStateChanged</c> events with <b>user</b> origin attribution and
///     values our mirroring (<see cref="InboundLogic.Decide" />, unit-tested separately)
///     maps to the right DV mutations. Together with <c>InboundLogicTests</c> this closes the
///     loop minus only the UI-thread dispatcher hop.
/// </summary>
[Category("Integration")]
[NotInParallel("csvg-port-50051")]
public class InjectionChannelMockTests
{
    private static async Task<DemoState> NextMatchingAsync(ChannelReader<DemoState> reader,
        Func<DemoState, bool> match, string what, CapturingLoggerProvider logs,
        CancellationToken cancellationToken)
    {
        List<string> seen = [];
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            while (true)
            {
                DemoState state = await reader.ReadAsync(timeout.Token);
                if (match(state))
                {
                    return state;
                }

                seen.Add(
                    $"(origin={state.Origin} playing={state.IsPlayingDemo} paused={state.IsPaused} tick={state.DemoTick} path={state.DemoFilePath})");
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"timed out waiting for {what}; non-matching DemoStates seen: [{string.Join(", ", seen)}]; "
                + $"host log tail:\n{logs.Tail(40)}");
        }
    }

    [Test]
    [Timeout(180_000)]
    public async Task InjectedUserActions_ArriveUserOriginated_AndMapToMirrorDecisions(
        CancellationToken cancellationToken)
    {
        CapturingLoggerProvider logs = new();
        CsvgWebHost host;
        try
        {
            host = await CsvgWebHost.StartAsync(new LiveSyncSettings
                {
                    MockMode = true
                }, logs,
                cancellationToken);
        }
        catch (LiveSyncPortInUseException ex)
        {
            throw new SkipTestException($"port {CsvgWebHost.GrpcPort} is owned by another process: {ex.Message}");
        }

        await using (host)
        {
            CsvgVideoSession session = host.Session;
            await session.StartWatchAsync(cancellationToken: cancellationToken);

            string demoPath = Path.Combine(Path.GetTempPath(), $"livesync-inject-{Guid.NewGuid():N}.dem");
            string otherDemoPath = Path.Combine(Path.GetTempPath(), $"livesync-inject-other-{Guid.NewGuid():N}.dem");
            await File.WriteAllBytesAsync(demoPath, [0x50, 0x42, 0x44, 0x45, 0x4D, 0x53, 0x32, 0x00],
                cancellationToken);
            await File.WriteAllBytesAsync(otherDemoPath, [0x50, 0x42, 0x44, 0x45, 0x4D, 0x53, 0x32, 0x00],
                cancellationToken);
            try
            {
                IMockUserActionInjector injector = host.MockInjector;
                await Assert.That(injector.IsAvailable).IsTrue()
                    .Because("mock mode must expose the live injection channel");

                Channel<DemoState> states = Channel.CreateUnbounded<DemoState>();
                session.DemoStateChanged += (_, state) =>
                {
                    states.Writer.TryWrite(state);
                    return Task.CompletedTask;
                };

                // Interactive UI requested — the same load path the engine uses on v1.1. The
                // load contract completes with the demo PAUSED at tick 0.
                await session.LoadDemoAsync(demoPath, true, cancellationToken);

                // Origin attribution is a ~3 s suppression window after any HOST command (A-P3):
                // an injected user action landing inside it is deliberately attributed
                // HOST_COMMAND. Let the load's window lapse so the injections below read as the
                // user actions they are.
                await Task.Delay(TimeSpan.FromSeconds(4), cancellationToken);

                // 1. In-game resume (the demo loaded paused): user origin + unpaused truth;
                //    maps to "play DV".
                await injector.InjectAsync(MockUserActions.Resume, cancellationToken);
                DemoState resumed = await NextMatchingAsync(states.Reader,
                    s => s.Origin == DemoStateOrigin.User && s.IsPaused == false,
                    "user-origin resumed DemoState", logs, cancellationToken);
                InboundLogic.Decision resumeDecision = InboundLogic.Decide(
                    resumed, false, 0, true, demoPath);
                await Assert.That(resumeDecision.SetPlaying).IsEqualTo(true);

                // 2. In-game pause: user origin + paused truth; maps to "pause DV".
                await injector.InjectAsync(MockUserActions.Pause, cancellationToken);
                DemoState paused = await NextMatchingAsync(states.Reader,
                    s => s.Origin == DemoStateOrigin.User && s.IsPaused == true,
                    "user-origin paused DemoState", logs, cancellationToken);
                InboundLogic.Decision pauseDecision = InboundLogic.Decide(
                    paused, true, 0, true, demoPath);
                await Assert.That(pauseDecision.SetPlaying).IsEqualTo(false);
                await Assert.That(pauseDecision.DemoChangedPath).IsNull();

                // 3. In-game seek: user seeks emit NO DemoStateEvent (transitions only) — the
                //    tick STREAM is the wire signal, which the pump's jump detection consumes.
                //    Assert the jump reaches DV's client through our hosting stack.
                await injector.InjectAsync(MockUserActions.Seek(3000), cancellationToken);
                DateTime seekDeadline = DateTime.UtcNow.AddSeconds(20);
                while ((session.Engine.LastTick ?? 0) < 2800)
                {
                    if (DateTime.UtcNow > seekDeadline)
                    {
                        throw new TimeoutException(
                            $"tick stream never reflected the user seek (LastTick={session.Engine.LastTick}); "
                            + $"host log tail:\n{logs.Tail(25)}");
                    }

                    await Task.Delay(50, cancellationToken);
                }

                // 4. In-game demo change: user origin + the NEW path; maps to the Open-in-DV
                //    offer (D7 — never a silent auto-load).
                await injector.InjectAsync(MockUserActions.PlayDemo(otherDemoPath), cancellationToken);
                DemoState changed = await NextMatchingAsync(states.Reader,
                    s => s.Origin == DemoStateOrigin.User
                         && !string.IsNullOrEmpty(s.DemoFilePath)
                         && s.DemoFilePath.EndsWith(Path.GetFileName(otherDemoPath), StringComparison.Ordinal),
                    "user-origin demo-change DemoState", logs, cancellationToken);
                InboundLogic.Decision changeDecision = InboundLogic.Decide(
                    changed, true, null, true, demoPath);
                await Assert.That(changeDecision.DemoChangedPath).IsEqualTo(changed.DemoFilePath);

                // 5. In-game demo END: IsPlayingDemo=false through the real wire —
                //    maps to end-as-pause, never a demo-change offer. (DV now tracks the demo
                //    CS2 switched to in step 4, hence dvDemoPath=otherDemoPath.)
                await injector.InjectAsync(MockUserActions.EndDemo, cancellationToken);
                DemoState ended = await NextMatchingAsync(states.Reader,
                    s => s.Origin == DemoStateOrigin.User && s.IsPlayingDemo == false,
                    "user-origin demo-end DemoState", logs, cancellationToken);
                InboundLogic.Decision endDecision = InboundLogic.Decide(
                    ended, true, null, true,
                    otherDemoPath);
                await Assert.That(endDecision.SetPlaying).IsEqualTo(false);
                await Assert.That(endDecision.DemoChangedPath).IsNull();
            }
            finally
            {
                File.Delete(demoPath);
                File.Delete(otherDemoPath);
                await session.StopAsync(CancellationToken.None);
            }
        }
    }

    /// <summary>Captures the CSVG host's log lines (incl. relayed mock stdout) for failure diagnostics.</summary>
    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly Lock _gate = new();
        private readonly List<string> _lines = [];

        public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

        public void Dispose()
        {
        }

        public string Tail(int count)
        {
            lock (_gate)
            {
                return string.Join("\n", _lines.TakeLast(count));
            }
        }

        private sealed class Logger(CapturingLoggerProvider owner, string category)
            : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel,
                EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                lock (owner._gate)
                {
                    owner._lines.Add($"[{category}] {formatter(state, exception)}");
                }
            }
        }
    }
}
