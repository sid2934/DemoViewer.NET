#region

using System.Reflection;
using Avalonia;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Guards the issue-#6 harness contract: a failure in Avalonia's per-dispatch
///     isolated-application setup must be reported with its real cause and attributed to the
///     harness, while a failure in the test body must still be reported as the test's own, run
///     exactly once, unretried.
/// </summary>
[NotInParallel]
public class HeadlessSessionDiagnosticsTests
{
    /// <summary>
    ///     The exact shape issue #6 produced and could never explain: Avalonia reaches the app
    ///     through reflection, so the useful cause sits two wrappers down and the only identifier
    ///     for the dead static initializer is <c>TypeInitializationException.TypeName</c>.
    /// </summary>
    [Test]
    public async Task DescribeFault_UnwrapsTheReflectionAndTypeInitializerChain()
    {
        Exception root = new InvalidOperationException("font manager unavailable");
        Exception typeInit = new TypeInitializationException("Avalonia.StyledElement", root);
        Exception fault = new TargetInvocationException(typeInit);

        string described = HeadlessSession.DescribeFault(fault);

        await Assert.That(described).Contains("TargetInvocationException");
        await Assert.That(described).Contains("Avalonia.StyledElement");
        await Assert.That(described).Contains("font manager unavailable");
    }

    [Test]
    public async Task DescribeFault_ReportsLoaderExceptions()
    {
        ReflectionTypeLoadException fault = new(
            [null],
            [new FileNotFoundException("Avalonia.Skia could not be located")]);

        string described = HeadlessSession.DescribeFault(fault);

        await Assert.That(described).Contains("loader:");
        await Assert.That(described).Contains("Avalonia.Skia could not be located");
    }

    [Test]
    public async Task DescribeFault_TerminatesOnASelfReferencingChain()
    {
        // AggregateException flattening plus a deep chain must not spin: the describe runs on the
        // failure path, where a hang would be indistinguishable from the wedge it is describing.
        Exception nested = new InvalidOperationException("innermost");
        for (int i = 0; i < 50; i++)
        {
            nested = new AggregateException($"layer {i}", nested);
        }

        string described = HeadlessSession.DescribeFault(nested);

        await Assert.That(described).Contains("--- innermost stack ---");
    }

    /// <summary>
    ///     The regression that the retry could have introduced. A throwing body has already had
    ///     observable effects, so retrying it would double-run test side effects and misreport a
    ///     product failure as a harness failure. The body-entered discriminator must keep the two
    ///     apart.
    /// </summary>
    [Test]
    public async Task RunOnUi_BodyFailure_PropagatesUnchangedAndRunsExactlyOnce()
    {
        int bodyRuns = 0;

        InvalidOperationException? thrown = await Assert.ThrowsAsync<InvalidOperationException>(async () => await HeadlessSession.RunOnUi(() =>
        {
            bodyRuns++;
            throw new InvalidOperationException("the body's own failure");
        }));

        await Assert.That(thrown!.Message).IsEqualTo("the body's own failure");
        await Assert.That(bodyRuns).IsEqualTo(1);
    }

    /// <summary>
    ///     Same contract for a body that fails after its first yield, where the exception travels
    ///     back through the dispatched task rather than being thrown synchronously.
    /// </summary>
    [Test]
    public async Task RunOnUi_AsyncBodyFailure_PropagatesUnchangedAndRunsExactlyOnce()
    {
        int bodyRuns = 0;

        InvalidOperationException? thrown = await Assert.ThrowsAsync<InvalidOperationException>(async () => await HeadlessSession.RunOnUi(async () =>
        {
            bodyRuns++;
            await Task.Yield();
            throw new InvalidOperationException("the async body's own failure");
        }));

        await Assert.That(thrown!.Message).IsEqualTo("the async body's own failure");
        await Assert.That(bodyRuns).IsEqualTo(1);
    }

    /// <summary>
    ///     The warm-up's job: by the time any test body runs, the session and one full isolated
    ///     application have already been built, so no test is the one that discovers a cold-start
    ///     setup fault and no <c>[NotInParallel]</c> body can touch Avalonia statics first.
    /// </summary>
    [Test]
    public async Task WarmUp_HasAlreadyBuiltAnApplicationBeforeAnyTestBodyRuns()
    {
        // Asserted on the warm-up's own flag, not on Application.Current: this dispatch would set
        // Application.Current by itself, so that check would pass with the hook deleted.
        await Assert.That(HeadlessSession.WarmUpBuiltAnApplication).IsTrue();

        bool applicationPresent = false;

        await HeadlessSession.RunOnUi(() =>
        {
            applicationPresent = Application.Current is not null;
            return Task.CompletedTask;
        });

        await Assert.That(applicationPresent).IsTrue();
    }
}
