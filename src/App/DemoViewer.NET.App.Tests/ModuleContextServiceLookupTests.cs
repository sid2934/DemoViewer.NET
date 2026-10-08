#region

using DemoViewer.NET.Extensions;
using DemoViewer.NET.Modules;
using DemoViewer.NET.Services;
using DemoViewer.NET.ViewModels.Playback;
using Microsoft.Extensions.DependencyInjection;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     <see cref="ModuleContext.GetService{T}" />: the typed lookup that replaced the
///     <c>StratCaptureHost</c> / <c>StratExportHost</c> properties. An explicit
///     <see cref="ModuleContext.RegisterService{T}" /> call wins over the DI container wired through
///     <see cref="ModuleContext.SetServices" />, which answers only for an extension's own types while it is
///     on; both are re-read on every call (not cached), and a type nobody wired resolves to null.
/// </summary>
public class ModuleContextServiceLookupTests
{
    private interface IWidget
    {
        int Value { get; }
    }

    private sealed record Widget(int Value) : IWidget;

    private static ModuleContext NewContext() => new(new PlaybackController(), () => null);

    private sealed class OffGate : Features.IFeatureGate
    {
        public Configuration.UserCategory Category => Configuration.UserCategory.Developer;

        public int HiddenCount => 0;

        public event EventHandler? Changed
        {
            add { }
            remove { }
        }

        public bool IsEnabled(string featureId) => false;
    }

    [Test]
    public async Task GetService_WithNothingWired_ReturnsNull()
    {
        ModuleContext context = NewContext();

        await Assert.That(context.GetService<IWidget>()).IsNull();
    }

    [Test]
    public async Task GetService_ResolvesAnExplicitlyRegisteredLookup()
    {
        ModuleContext context = NewContext();
        context.RegisterService<IWidget>(() => new Widget(7));

        await Assert.That(context.GetService<IWidget>()?.Value).IsEqualTo(7);
    }

    [Test]
    public async Task GetService_ReadsAnExplicitRegistration_FreshOnEveryCall()
    {
        ModuleContext context = NewContext();
        int current = 1;
        context.RegisterService<IWidget>(() => new Widget(current));

        await Assert.That(context.GetService<IWidget>()?.Value).IsEqualTo(1);
        current = 2;
        await Assert.That(context.GetService<IWidget>()?.Value).IsEqualTo(2);
    }

    [Test]
    public async Task GetService_FallsBackToTheWiredContainer_WhenNothingIsExplicitlyRegistered()
    {
        ModuleContext context = NewContext();
        ServiceCollection services = new();
        services.AddSingleton<IWidget>(new Widget(9));
        services.AddSingleton(new HeavyJobGate());
        using ServiceProvider provider = services.BuildServiceProvider();
        context.SetServices(provider);
        // The widget is this test assembly's type, which the stub extension owns.
        context.SetFaults(ExtensionFaults.For([new Extensions.StubExtension()], static a => a()));

        using (Assert.Multiple())
        {
            await Assert.That(context.GetService<IWidget>()?.Value).IsEqualTo(9);
            await Assert.That(context.GetService<HeavyJobGate>()).IsNull().Because("a host type is never reachable through the container");
        }
    }

    [Test]
    public async Task GetService_AnswersNothingFromTheContainer_ForAnExtensionThatIsOff()
    {
        ModuleContext context = NewContext();
        ServiceCollection services = new();
        services.AddSingleton<IWidget>(new Widget(9));
        services.AddSingleton<Features.IFeatureGate>(new OffGate());
        using ServiceProvider provider = services.BuildServiceProvider();
        context.SetServices(provider);
        context.SetFaults(ExtensionFaults.For([new Extensions.StubExtension()], static a => a()));

        await Assert.That(context.GetService<IWidget>()).IsNull();
    }

    [Test]
    public async Task GetService_WithNoExtensionOwningTheType_AnswersNothingFromTheContainer()
    {
        ModuleContext context = NewContext();
        ServiceCollection services = new();
        services.AddSingleton<IWidget>(new Widget(9));
        using ServiceProvider provider = services.BuildServiceProvider();
        context.SetServices(provider);

        await Assert.That(context.GetService<IWidget>()).IsNull();
    }

    [Test]
    public async Task GetService_PrefersAnExplicitRegistration_OverTheContainer()
    {
        ModuleContext context = NewContext();
        ServiceCollection services = new();
        services.AddSingleton<IWidget>(new Widget(9));
        using ServiceProvider provider = services.BuildServiceProvider();
        context.SetServices(provider);
        context.RegisterService<IWidget>(() => new Widget(99));

        await Assert.That(context.GetService<IWidget>()?.Value).IsEqualTo(99);
    }
}
