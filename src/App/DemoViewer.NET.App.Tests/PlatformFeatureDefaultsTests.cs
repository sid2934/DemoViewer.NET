#region

using DemoViewer.NET.Configuration;
using DemoViewer.NET.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The platform half of a feature default. <c>OperatingSystem.IsMacOS()</c> is an intrinsic the JIT
///     folds to a constant, so a rule keyed on it is only ever exercised on one platform by a real run:
///     these pass the answer in instead, so both sides are asserted on whichever machine is running.
/// </summary>
public class PlatformFeatureDefaultsTests
{
    private static readonly Dictionary<UserCategory, bool> _onForEveryone = new()
    {
        [UserCategory.Consumer] = true, [UserCategory.PowerUser] = true, [UserCategory.Developer] = true
    };

    /// <summary>
    ///     The owner's call: the Reels dashboard defaults off on macOS, which is not a supported target for
    ///     it, and keeps the catalog default everywhere else. Linux is deliberately NOT in the rule.
    /// </summary>
    [Test]
    [Arguments(UserCategory.Consumer)]
    [Arguments(UserCategory.PowerUser)]
    [Arguments(UserCategory.Developer)]
    public async Task TabHighlights_DefaultsOffOnMacOs_AndKeepsTheCatalogDefaultElsewhere(UserCategory category)
    {
        using (Assert.Multiple())
        {
            await Assert.That(PlatformFeatureDefaults.DefaultFor("tab.highlights", _onForEveryone, category, isMacOs: true))
                .IsFalse().Because("macOS is not a supported target for Reels, and an empty tab is worse than an absent one");
            await Assert.That(PlatformFeatureDefaults.DefaultFor("tab.highlights", _onForEveryone, category, isMacOs: false))
                .IsTrue().Because("every other platform keeps the catalog default, Linux included");
        }
    }

    [Test]
    public async Task AnIdWithNoPlatformRule_IsUnaffectedOnEitherPlatform()
    {
        using (Assert.Multiple())
        {
            await Assert.That(PlatformFeatureDefaults.DefaultFor("tab.playback2d", _onForEveryone, UserCategory.Consumer, isMacOs: true))
                .IsTrue();
            await Assert.That(PlatformFeatureDefaults.DefaultFor("tab.playback2d", _onForEveryone, UserCategory.Consumer, isMacOs: false))
                .IsTrue();
        }
    }

    /// <summary>
    ///     A category default of false is not revived by the platform rule: the platform can only take a
    ///     default away, never grant one.
    /// </summary>
    [Test]
    public async Task APlatformRule_NeverTurnsACategoryDefaultOn()
    {
        Dictionary<UserCategory, bool> offForConsumers = new() { [UserCategory.Consumer] = false };

        await Assert.That(PlatformFeatureDefaults.DefaultFor("tab.highlights", offForConsumers, UserCategory.Consumer, isMacOs: false))
            .IsFalse();
    }

    /// <summary>
    ///     The rule as a user meets it: the real gate, no override, on whatever platform is running. The
    ///     expectation branches because the behaviour is platform-specific by design, which is the point.
    /// </summary>
    [Test]
    public async Task WithNoOverride_TheRealGateFollowsThePlatform()
    {
        string dir = Path.Combine(Path.GetTempPath(), "dv-platform-nodefault-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            SettingsService svc = new(dir);
            ServiceCollection services = new();
            services.Configure<AppSettings>(svc.Configuration);
            services.AddSingleton<IFeatureGate>(s =>
                new FeatureGate(s.GetRequiredService<IOptionsMonitor<AppSettings>>(), false));
            using ServiceProvider sp = services.BuildServiceProvider();

            bool enabled = sp.GetRequiredService<IFeatureGate>().IsEnabled("tab.highlights");

            await Assert.That(enabled).IsEqualTo(!OperatingSystem.IsMacOS())
                .Because("Reels defaults off on macOS and keeps the catalog default elsewhere");
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    /// <summary>
    ///     The difference from <see cref="ShellModuleFeatureGate.DesktopOnlyIds" />, which refuses whatever
    ///     the user says: this is a DEFAULT, so a stored override wins. Asserted through the real gate on
    ///     this machine, since that is the path a user's override actually takes.
    /// </summary>
    [Test]
    public async Task AStoredOverride_BeatsThePlatformDefault()
    {
        string dir = Path.Combine(Path.GetTempPath(), "dv-platform-default-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "settings.json"),
                """{ "Features": { "Overrides": { "tab.highlights": true } } }""");

            SettingsService svc = new(dir);
            ServiceCollection services = new();
            services.Configure<AppSettings>(svc.Configuration);
            services.AddSingleton<IFeatureGate>(s =>
                new FeatureGate(s.GetRequiredService<IOptionsMonitor<AppSettings>>(), false));
            using ServiceProvider sp = services.BuildServiceProvider();

            await Assert.That(sp.GetRequiredService<IFeatureGate>().IsEnabled("tab.highlights")).IsTrue()
                .Because("the override is read before any default, so a user who switches Reels on keeps it on macOS too");
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
