#region

using DemoViewer.NET.Services.Startup;

#endregion

namespace DemoViewer.NET.AppTests.Startup;

/// <summary>Safe mode's decision: what the previous launch left behind, and the command line.</summary>
public class LaunchGuardTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);

    private static LaunchState State(LaunchPhase phase, bool hung = false, CrashRecord? crash = null) =>
        new(phase, At, false, 1, hung, crash);

    private static string NewRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), "dv-launch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    [Test]
    public async Task TheDecision_FollowsWhatThePreviousLaunchLeft()
    {
        CrashRecord inHello = new(At, "System.InvalidOperationException", "Hello", "pack.hello");
        CrashRecord unattributed = new(At, "System.InvalidOperationException", null, null);

        using (Assert.Multiple())
        {
            await Assert.That(LaunchGuard.Decide(null, false)).IsEqualTo(SafeModeState.Off).Because("a first launch");
            await Assert.That(LaunchGuard.Decide(State(LaunchPhase.Exited), false)).IsEqualTo(SafeModeState.Off);
            await Assert.That(LaunchGuard.Decide(State(LaunchPhase.Exited), true).Reason).IsEqualTo(SafeModeReason.Requested);
            await Assert.That(LaunchGuard.Decide(State(LaunchPhase.Starting), false).Reason).IsEqualTo(SafeModeReason.StartupFailed)
                .Because("a launch that never finished starting crashed or froze while starting");
            await Assert.That(LaunchGuard.Decide(State(LaunchPhase.Running, crash: inHello), false))
                .IsEqualTo(new SafeModeState(true, SafeModeReason.ExtensionCrashed, "Hello", "pack.hello"));
            await Assert.That(LaunchGuard.Decide(State(LaunchPhase.Running, crash: unattributed), false)).IsEqualTo(SafeModeState.Off)
                .Because("a crash with no extension on its stack is not a reason to turn every extension off");
            await Assert.That(LaunchGuard.Decide(State(LaunchPhase.Running, hung: true), false).Reason)
                .IsEqualTo(SafeModeReason.StoppedResponding);
            await Assert.That(LaunchGuard.Decide(State(LaunchPhase.Running), false)).IsEqualTo(SafeModeState.Off)
                .Because("a kill or a power loss mid-session says nothing about extensions");
        }
    }

    [Test]
    public async Task ALaunch_IsStartingUntilMarkedRunning_AndAnUnfinishedOneMeansSafeModeNextTime()
    {
        string root = NewRoot();
        try
        {
            LaunchGuard first = LaunchGuard.Begin(root, []);
            await Assert.That(first.Decision.IsActive).IsFalse();
            await Assert.That(LaunchGuard.Read(Path.Combine(root, LaunchGuard.FileName))!.Phase).IsEqualTo(LaunchPhase.Starting);

            LaunchGuard second = LaunchGuard.Begin(root, []);
            await Assert.That(second.Decision.Reason).IsEqualTo(SafeModeReason.StartupFailed);

            second.MarkRunning();
            second.MarkExited();
            LaunchGuard third = LaunchGuard.Begin(root, []);
            await Assert.That(third.Decision.IsActive).IsFalse().Because("the safe-mode launch exited cleanly");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task TheSwitch_TurnsSafeModeOn_InEitherSpelling()
    {
        using (Assert.Multiple())
        {
            await Assert.That(LaunchGuard.Begin(null, ["--safe-mode"]).Decision.Reason).IsEqualTo(SafeModeReason.Requested);
            await Assert.That(LaunchGuard.Begin(null, ["/Safe-Mode"]).Decision.Reason).IsEqualTo(SafeModeReason.Requested);
            await Assert.That(LaunchGuard.Begin(null, ["--other"]).Decision.IsActive).IsFalse();
        }
    }

    // A third-party extension loads its private dependencies into its own load context. A crash whose stack
    // is in one of those dependencies, not in the entry assembly, is still the extension's. An identity in the
    // app's default context claims only its entry assembly, never the rest of the app.
    [Test]
    public async Task ACrashInsideAnExtensionsOwnDependency_IsAttributedThroughItsLoadContext()
    {
        string bin = AppContext.BaseDirectory;
        System.Runtime.Loader.AssemblyLoadContext context = new("external:test@1.0", isCollectible: true);
        try
        {
            System.Reflection.Assembly entry = context.LoadFromAssemblyPath(Path.Combine(bin, "DemoViewer.NET.Extensions.StratBook.dll"));
            System.Reflection.Assembly dependency = context.LoadFromAssemblyPath(Path.Combine(bin, "DemoViewer.NET.Extensions.Sdk.dll"));
            System.Reflection.MethodInfo throws = dependency.GetType("DemoViewer.NET.Extensions.Sdk.ExtensionServiceProviderExtensions")!
                .GetMethod("GetExtensionContext")!;
            Exception thrown;
            try
            {
                throws.Invoke(null, [null, "x"]);
                throw new InvalidOperationException("expected a throw");
            }
            catch (System.Reflection.TargetInvocationException ex)
            {
                thrown = ex;
            }

            ExtensionIdentity external = new(entry, "External", "pack.external", context);
            ExtensionIdentity entryOnly = external with { LoadContext = null };
            ExtensionIdentity shipped = new(typeof(LaunchGuardTests).Assembly, "Shipped", "pack.shipped",
                System.Runtime.Loader.AssemblyLoadContext.Default);

            using (Assert.Multiple())
            {
                await Assert.That(LaunchGuard.Attribute(thrown, [shipped, external])).IsSameReferenceAs(external);
                await Assert.That(LaunchGuard.Attribute(thrown, [entryOnly])).IsNull()
                    .Because("matching the entry assembly alone misses the extension's own dependencies");
                await Assert.That(LaunchGuard.Attribute(new InvalidOperationException("app"), [shipped])).IsNull();
            }
        }
        finally
        {
            context.Unload();
        }
    }

    [Test]
    public async Task ACrash_NamesTheExtensionOnItsStack_AndTheNextLaunchSaysSo()
    {
        string root = NewRoot();
        try
        {
            LaunchGuard guard = LaunchGuard.Begin(root, []);
            guard.MarkRunning();
            Exception thrown;
            try
            {
                throw new InvalidOperationException("from the test assembly");
            }
            catch (InvalidOperationException ex)
            {
                thrown = ex;
            }

            ExtensionIdentity me = new(typeof(LaunchGuardTests).Assembly, "Test extension", "pack.test");
            ExtensionIdentity other = new(typeof(LaunchGuard).Assembly, "Not on the stack", "pack.other");
            guard.RecordCrash(thrown, [other, me]);

            SafeModeState next = LaunchGuard.Begin(root, []).Decision;
            using (Assert.Multiple())
            {
                await Assert.That(next.Reason).IsEqualTo(SafeModeReason.ExtensionCrashed);
                await Assert.That(next.ExtensionName).IsEqualTo("Test extension");
                await Assert.That(next.ExtensionFeatureId).IsEqualTo("pack.test");
                await Assert.That(next.Message).Contains("Test extension");
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task AFreeze_IsRecorded_AndCleared_WhenTheUiAnswersAgain()
    {
        List<bool> reports = [];
        List<Action> posted = [];
        using UiWatchdog watchdog = new(hung =>
        {
            lock (reports)
            {
                reports.Add(hung);
            }
        }, action =>
        {
            lock (posted)
            {
                posted.Add(action);
            }
        }, TimeSpan.FromMilliseconds(150));

        DateTime deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline && !Contains(reports, true))
        {
            await Task.Delay(20);
        }

        Action answer;
        lock (posted)
        {
            answer = posted[0];
        }

        answer();
        using (Assert.Multiple())
        {
            await Assert.That(Contains(reports, true)).IsTrue().Because("nothing answered the ping for longer than the limit");
            await Assert.That(reports[^1]).IsFalse().Because("the UI thread answered again");
        }
    }

    private static bool Contains(List<bool> list, bool value)
    {
        lock (list)
        {
            return list.Contains(value);
        }
    }
}

/// <summary>The safe-mode banner turns an extension off while that extension is not loaded.</summary>
public class SafeModeSettingsTests
{
    [Test]
    public async Task AnOverrideForAnExtensionNotLoaded_SurvivesWritesAndReloads()
    {
        string root = Path.Combine(Path.GetTempPath(), "dv-safemode-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Configuration.SettingsService first = new(root);
            first.Write(s => s.Features.Overrides["pack.notloaded"] = false);
            first.Write(s => s.Extensions.AllowUnverified = true);

            Configuration.SettingsService second = new(root);
            using (Assert.Multiple())
            {
                await Assert.That(second.Current.Features.Overrides).ContainsKey("pack.notloaded");
                await Assert.That(second.Current.Features.Overrides["pack.notloaded"]).IsFalse();
                await Assert.That(second.Current.Extensions.AllowUnverified).IsTrue();
                await Assert.That(DemoViewer.NET.Extensions.Loading.ExtensionStartup.ReadAllowUnverified(Path.Combine(root, "settings.json")))
                    .IsTrue().Because("the launch reads what the settings service wrote");
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
