#region

using DemoViewer.NET.Modules;
using DemoViewer.NET.ViewModels.Playback;
using Microsoft.Extensions.DependencyInjection;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     <see cref="ModuleContext.GetService{T}" />: the typed lookup that replaced the
///     <c>StratCaptureHost</c> / <c>StratExportHost</c> properties (item 15). An explicit
///     <see cref="ModuleContext.RegisterService{T}" /> call wins over the DI container wired through
///     <see cref="ModuleContext.SetServices" />, both are re-read on every call (not cached), and a type
///     nobody wired resolves to null.
/// </summary>
public class ModuleContextServiceLookupTests
{
    private interface IWidget
    {
        int Value { get; }
    }

    private sealed record Widget(int Value) : IWidget;

    private static ModuleContext NewContext() => new(new PlaybackController(), () => null);

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
        using ServiceProvider provider = services.BuildServiceProvider();
        context.SetServices(provider);

        await Assert.That(context.GetService<IWidget>()?.Value).IsEqualTo(9);
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
