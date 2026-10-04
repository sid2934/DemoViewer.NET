#region

using System.Reflection;
using System.Xml.Linq;
using DemoViewer.NET.Extensions;
using DemoViewer.NET.Extensions.Manifest;
using DemoViewer.NET.Extensions.StratBook;

#endregion

namespace DemoViewer.NET.AppTests.Extensions;

/// <summary>
///     Guards against a release shipping an app and an extension that would refuse to load together:
///     every axis of the shipped manifest against this build's host (not just the first failure
///     <see cref="PackCompatibility.Check" /> returns), the extension assembly's compiled references
///     against the versions this process actually loads, the <c>requiresCs2DemoKit</c> exact pin, and
///     <see cref="PackCompatibility" />'s semantics over a representative matrix.
/// </summary>
public class CompatibilityMatrixTests
{
    // ── 1: the shipped manifest against this build's host, every axis ─────────────────────────────

    // The repo copy is a template: its version is the literal "{nbgv}" and the build stamps the value
    // Nerdbank.GitVersioning computes from src/Extensions/StratBook/version.json (§7.11). The placeholder
    // must not parse as a version, so an unstamped copy can never load.
    private const string VersionPlaceholder = "\"{nbgv}\"";

    [Test]
    public async Task ManifestTemplate_InTheRepo_CarriesThePlaceholderOnce_AndStampsToTheBinCopy()
    {
        string repoRoot = RepoRoot();
        string repoPath = Path.Combine(repoRoot, "src", "Extensions", "StratBook", ExtensionManifest.FileName);
        string binPath = Path.Combine(AppContext.BaseDirectory, ExtensionManifest.FileName);
        string template = await File.ReadAllTextAsync(repoPath);
        string stampedText = await File.ReadAllTextAsync(binPath);
        ExtensionManifest fromBin = ExtensionManifest.Parse(stampedText);

        int first = template.IndexOf(VersionPlaceholder, StringComparison.Ordinal);
        using (Assert.Multiple())
        {
            await Assert.That(first).IsGreaterThanOrEqualTo(0).Because($"{repoPath} must carry {VersionPlaceholder}");
            await Assert.That(template.IndexOf(VersionPlaceholder, first + 1, StringComparison.Ordinal)).IsEqualTo(-1)
                .Because("the placeholder appears exactly once");
            Assert.Throws<ExtensionManifestException>(() => ExtensionManifest.Parse(template));
            await Assert.That(template.Replace(VersionPlaceholder, $"\"{fromBin.Version}\"", StringComparison.Ordinal)).IsEqualTo(stampedText)
                .Because("the stamped copy is the template with only its version filled in; anything else means a template edit without a rebuild");
            await AssertSatisfiesEveryAxis(fromBin);
        }
    }

    [Test]
    public async Task ShippedManifest_BesideTheExtensionBinary_MatchesTheEmbeddedCopy_AndTheAssemblyVersion()
    {
        string binPath = Path.Combine(AppContext.BaseDirectory, ExtensionManifest.FileName);
        await Assert.That(File.Exists(binPath)).IsTrue().Because($"the bin copy must exist at {binPath}");

        ExtensionManifest fromBin = ExtensionManifest.Parse(await File.ReadAllTextAsync(binPath));
        ExtensionManifest fromEmbedded = new StratBookPack().Manifest;
        string? informational = typeof(StratBookPack).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        using (Assert.Multiple())
        {
            await Assert.That(fromEmbedded).IsEqualTo(fromBin)
                .Because("the loader judges the bundled pack by its embedded copy; a mismatch here ships a pack that judges itself differently than its own file does");
            await Assert.That(SemVersion.TryParseInformational(informational, out SemVersion? stamped)).IsTrue()
                .Because($"the extension assembly carries an NBGV informational version; got '{informational}'");
            await Assert.That((fromBin.Version.Major, fromBin.Version.Minor, fromBin.Version.Patch))
                .IsEqualTo((stamped!.Major, stamped.Minor, stamped.Patch))
                .Because("the manifest version and the assembly version come from the same version.json");
            await AssertSatisfiesEveryAxis(fromBin);
        }
    }

    // Each axis asserted on its own, named in the message: a future regression says which axis broke,
    // not just "not compatible".
    private static async Task AssertSatisfiesEveryAxis(ExtensionManifest manifest)
    {
        ExtensionHostInfo host = ExtensionHost.Current;
        using (Assert.Multiple())
        {
            await Assert.That(manifest.RequiresHost.Satisfies(host.ContractVersion)).IsTrue()
                .Because($"requiresHost {manifest.RequiresHost} must admit ContractVersion {host.ContractVersion}");
            await Assert.That(manifest.RequiresCs2DemoKit.Satisfies(host.Cs2DemoKitVersion)).IsTrue()
                .Because($"requiresCs2DemoKit {manifest.RequiresCs2DemoKit} must admit Cs2DemoKitVersion {host.Cs2DemoKitVersion}");

            bool appAxis = manifest.MinAppVersion is not { } floor || host.AppVersion is not { } app || app >= floor;
            await Assert.That(appAxis).IsTrue()
                .Because($"minAppVersion {manifest.MinAppVersion?.ToString() ?? "none"} vs AppVersion {host.AppVersion?.ToString() ?? "unstamped"}");

            PackCompatibility result = PackCompatibility.Check(manifest, host);
            await Assert.That(result.IsCompatible).IsTrue().Because(result.Describe(manifest, manifest.Id) ?? "compatible");
        }
    }

    // ── 2: the extension's compiled references against what this build of the app actually loads ────

    // "Everything the app ships": the app assembly's transitive references, loaded by simple name
    // (never the compile-time AssemblyName with its version), minus anything that resolves to the
    // shared runtime directory (the BCL, which every build on this TFM carries identically).
    private static Dictionary<string, Version?> AppShippedAssemblyVersions()
    {
        string runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        Assembly app = typeof(ExtensionHost).Assembly;

        Dictionary<string, Version?> shipped = new(StringComparer.Ordinal) { [app.GetName().Name!] = app.GetName().Version };
        HashSet<string> seen = new(StringComparer.Ordinal) { app.GetName().Name! };
        Queue<Assembly> queue = new();
        queue.Enqueue(app);

        while (queue.Count > 0)
        {
            Assembly asm = queue.Dequeue();
            foreach (AssemblyName refName in asm.GetReferencedAssemblies())
            {
                string name = refName.Name!;
                if (!seen.Add(name))
                {
                    continue;
                }

                Assembly loaded;
                try
                {
                    loaded = System.Reflection.Assembly.Load(name);
                }
                catch (Exception ex) when (ex is FileNotFoundException or FileLoadException or BadImageFormatException)
                {
                    continue;
                }

                if (string.Equals(Path.GetDirectoryName(loaded.Location), runtimeDir, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                shipped[name] = loaded.GetName().Version;
                queue.Enqueue(loaded);
            }
        }

        return shipped;
    }

    // GetReferencedAssemblies() records only the AssemblyVersion attribute: 0.13.0.0 for every
    // CS2DemoKit.* package regardless of prerelease label, so a beta0001-to-beta0002 drift cannot be
    // caught here. RequiresCs2DemoKit_EqualsTheDirectoryPackagesPropsPin_Exactly (item 3) covers that axis.
    [Test]
    public async Task ExtensionReferences_ToAssembliesTheAppShips_MatchTheVersionLoadedInThisProcess()
    {
        Dictionary<string, Version?> appShipped = AppShippedAssemblyVersions();
        AssemblyName[] extensionRefs = typeof(StratBookPack).Assembly.GetReferencedAssemblies();
        AssemblyName[] verified = [.. extensionRefs.Where(r => appShipped.ContainsKey(r.Name!))];

        using (Assert.Multiple())
        {
            // The derivation did not silently come back empty or miss a whole family.
            await Assert.That(verified.Select(r => r.Name!)).Contains("DemoViewer.NET");
            await Assert.That(verified.Select(r => r.Name!)).Contains("CommunityToolkit.Mvvm");
            await Assert.That(verified.Any(r => r.Name!.StartsWith("CS2DemoKit.", StringComparison.Ordinal))).IsTrue();
            await Assert.That(verified.Any(r => r.Name!.StartsWith("Avalonia", StringComparison.Ordinal))).IsTrue();
            await Assert.That(verified.Any(r => r.Name!.StartsWith("Microsoft.Extensions.", StringComparison.Ordinal))).IsTrue();

            foreach (AssemblyName reference in verified)
            {
                Version? loaded = System.Reflection.Assembly.Load(reference.Name!).GetName().Version;
                await Assert.That(reference.Version).IsEqualTo(loaded)
                    .Because($"{reference.Name}: the extension was built against {reference.Version}; this process loads {loaded}");
            }
        }
    }

    // Empty today: every non-BCL reference the extension carries is also something the app ships.
    private static readonly string[] _extensionPrivateReferences = [];

    [Test]
    public async Task ExtensionReferences_NotCoveredByTheAppOrTheBcl_AreOnThePrivateAllowlist()
    {
        Dictionary<string, Version?> appShipped = AppShippedAssemblyVersions();
        string runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location)!;

        List<string> uncovered = [];
        foreach (AssemblyName reference in typeof(StratBookPack).Assembly.GetReferencedAssemblies())
        {
            string name = reference.Name!;
            if (appShipped.ContainsKey(name) || _extensionPrivateReferences.Contains(name))
            {
                continue;
            }

            bool isBcl;
            try
            {
                isBcl = string.Equals(Path.GetDirectoryName(System.Reflection.Assembly.Load(name).Location), runtimeDir,
                    StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex) when (ex is FileNotFoundException or FileLoadException or BadImageFormatException)
            {
                isBcl = false;
            }

            if (!isBcl)
            {
                uncovered.Add(name);
            }
        }

        await Assert.That(uncovered).IsEmpty()
            .Because($"not covered by the app-shipped derivation, the BCL, or the private allowlist: {string.Join(", ", uncovered)}");
    }

    // ── 3: requiresCs2DemoKit is the pin EXACTLY; requiresHost is bounded below the next major ────────

    // The repo template with its placeholder filled by a stand-in version: these axes do not depend on it.
    private static async Task<ExtensionManifest> RepoTemplateManifest(string repoRoot)
    {
        string path = Path.Combine(repoRoot, "src", "Extensions", "StratBook", ExtensionManifest.FileName);
        string template = await File.ReadAllTextAsync(path);
        return ExtensionManifest.Parse(template.Replace(VersionPlaceholder, "\"0.0.0\"", StringComparison.Ordinal));
    }

    [Test]
    public async Task RequiresCs2DemoKit_EqualsTheDirectoryPackagesPropsPin_Exactly()
    {
        string repoRoot = RepoRoot();
        XDocument props = XDocument.Load(Path.Combine(repoRoot, "Directory.Packages.props"));
        string[] pins =
        [
            .. props.Descendants("PackageVersion")
                .Where(e => ((string?)e.Attribute("Include"))?.StartsWith("CS2DemoKit.", StringComparison.Ordinal) == true)
                .Select(e => e.Attribute("Version")!.Value)
        ];

        ExtensionManifest manifest = await RepoTemplateManifest(repoRoot);

        using (Assert.Multiple())
        {
            await Assert.That(pins.Distinct().Count()).IsEqualTo(1)
                .Because("every CS2DemoKit.* package moves in one commit; a split pin means the manifest has nothing single to pin to");
            await Assert.That(manifest.RequiresCs2DemoKit).IsEqualTo(VersionRange.Exactly(SemVersion.Parse(pins[0])))
                .Because("requiresCs2DemoKit must be the exact pin, not a range the pin merely satisfies");
        }
    }

    [Test]
    public async Task RequiresHost_IsSatisfiedByTheCurrentContract_AndWouldBeViolatedByTheNextMajor()
    {
        string repoRoot = RepoRoot();
        ExtensionManifest manifest = await RepoTemplateManifest(repoRoot);
        SemVersion nextMajor = new(ExtensionHost.ContractVersion.Major + 1, 0, 0);

        using (Assert.Multiple())
        {
            await Assert.That(manifest.RequiresHost.Satisfies(ExtensionHost.ContractVersion)).IsTrue();
            await Assert.That(manifest.RequiresHost.Satisfies(nextMajor)).IsFalse()
                .Because($"requiresHost {manifest.RequiresHost} must be bounded below the next major {nextMajor}; " +
                         "a contract bump must fail this test before it can ship an unloadable pair");
        }
    }

    // ── 4: the matrix, over PackCompatibility.Check ────────────────────────────────────────────────
    // One fixed host per row-family, the manifest varied: item 36's situation (one installed app,
    // many feed entries). hostKit 0.13.0-beta0001 is the real pin; two extra rows hold the manifest's
    // pin fixed and vary the host instead, covering the mismatch from the other side.

    [Test]
    [Arguments("^1.0", "0.13.0-beta0001", null, "1.0.0", "0.13.0-beta0001", "0.6.0", "ok")]
    [Arguments("^2.0", "0.13.0-beta0001", null, "1.0.0", "0.13.0-beta0001", "0.6.0", "host")]
    // host fixed at the real pin; a release manifest fails the Eq comparator (it is not the beta it pins).
    [Arguments("^1.0", "0.13.0", null, "1.0.0", "0.13.0-beta0001", "0.6.0", "kit")]
    // host fixed at the real pin; a different core version is a plain mismatch.
    [Arguments("^1.0", "0.14.0", null, "1.0.0", "0.13.0-beta0001", "0.6.0", "kit")]
    // manifest fixed at the exact pin; a release host fails the same Eq comparator from the other side.
    [Arguments("^1.0", "0.13.0-beta0001", null, "1.0.0", "0.13.0", "0.6.0", "kit")]
    // manifest fixed at the exact pin; a newer host is a plain mismatch.
    [Arguments("^1.0", "0.13.0-beta0001", null, "1.0.0", "0.14.0", "0.6.0", "kit")]
    // comparators hold numerically (0.12.0 <= 0.13.0-beta0001 < 0.14.0); the prerelease rule excludes it
    // because no comparator in the set names a prerelease of the same core.
    [Arguments("^1.0", ">=0.12.0 <0.14.0", null, "1.0.0", "0.13.0-beta0001", "0.6.0", "kit")]
    [Arguments("^1.0", "0.13.0-beta0001", "0.7.0", "1.0.0", "0.13.0-beta0001", "0.6.0", "app")]
    [Arguments("^1.0", "0.13.0-beta0001", "0.7.0", "1.0.0", "0.13.0-beta0001", null, "ok")]
    public async Task Check_OverRepresentativeHostManifestPairs_MatchesTheExpectedReason(
        string requiresHost, string requiresCs2DemoKit, string? minAppVersion,
        string hostContract, string hostKit, string? hostApp, string expected)
    {
        ExtensionManifest manifest = FakeManifests.For("net.demoviewer.pack.matrix", "Matrix", "1.0.0", requiresHost, requiresCs2DemoKit, minAppVersion);
        ExtensionHostInfo host = new(SemVersion.Parse(hostContract), hostApp is null ? null : SemVersion.Parse(hostApp), SemVersion.Parse(hostKit));

        PackCompatibility result = PackCompatibility.Check(manifest, host);
        switch (expected)
        {
            case "ok":
                await Assert.That(result.IsCompatible).IsTrue().Because(result.Describe(manifest, manifest.Id) ?? "compatible");
                break;
            case "host":
                await Assert.That(result is PackCompatibility.HostContractMismatch).IsTrue().Because(result.GetType().Name);
                break;
            case "kit":
                await Assert.That(result is PackCompatibility.Cs2DemoKitMismatch).IsTrue().Because(result.GetType().Name);
                break;
            case "app":
                await Assert.That(result is PackCompatibility.AppTooOld).IsTrue().Because(result.GetType().Name);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(expected), expected, "expected one of ok, host, kit, app");
        }
    }

    // ── 5: CompatibilityReport's wording ───────────────────────────────────────────────────────────

    private static readonly ExtensionHostInfo _reportHost =
        new(SemVersion.Parse("1.0.0"), SemVersion.Parse("0.6.2"), SemVersion.Parse("0.13.0-beta0001"));

    [Test]
    public async Task Describe_Compatible()
    {
        ExtensionManifest m = FakeManifests.For("net.demoviewer.pack.x", "X", "1.0.0", "^1.0", "0.13.0-beta0001", "0.6.0");
        string report = CompatibilityReport.Describe(m, _reportHost);
        using (Assert.Multiple())
        {
            await Assert.That(report).IsEqualTo(
                "X 1.0.0 (net.demoviewer.pack.x): requiresHost ^1.0, requiresCs2DemoKit 0.13.0-beta0001, minAppVersion 0.6.0; "
                + "host contract 1.0.0, CS2DemoKit 0.13.0-beta0001, app 0.6.2. Compatible.");
            await Assert.That(report).DoesNotContain("\n");
        }
    }

    [Test]
    public async Task Describe_HostContractMismatch()
    {
        ExtensionManifest m = FakeManifests.For("net.demoviewer.pack.x", "Strat Book", "1.2.0", "^2.0", "*");
        string report = CompatibilityReport.Describe(m, _reportHost);
        await Assert.That(report).IsEqualTo(
            "Strat Book 1.2.0 (net.demoviewer.pack.x): requiresHost ^2.0, requiresCs2DemoKit *, minAppVersion any; "
            + "host contract 1.0.0, CS2DemoKit 0.13.0-beta0001, app 0.6.2. "
            + "Incompatible: Strat Book 1.2.0 needs app contract ^2.0; this app provides 1.0.0.");
    }

    [Test]
    public async Task Describe_Cs2DemoKitMismatch()
    {
        ExtensionManifest m = FakeManifests.For("net.demoviewer.pack.x", "Strat Book", "1.0.0", "^1.0", "0.14.0");
        string report = CompatibilityReport.Describe(m, _reportHost);
        await Assert.That(report).IsEqualTo(
            "Strat Book 1.0.0 (net.demoviewer.pack.x): requiresHost ^1.0, requiresCs2DemoKit 0.14.0, minAppVersion any; "
            + "host contract 1.0.0, CS2DemoKit 0.13.0-beta0001, app 0.6.2. "
            + "Incompatible: Strat Book 1.0.0 needs CS2DemoKit 0.14.0; this app ships 0.13.0-beta0001.");
    }

    [Test]
    public async Task Describe_AppTooOld()
    {
        ExtensionManifest m = FakeManifests.For("net.demoviewer.pack.x", "Strat Book", "1.0.0", "^1.0", "*", "0.7.0");
        string report = CompatibilityReport.Describe(m, _reportHost);
        await Assert.That(report).IsEqualTo(
            "Strat Book 1.0.0 (net.demoviewer.pack.x): requiresHost ^1.0, requiresCs2DemoKit *, minAppVersion 0.7.0; "
            + "host contract 1.0.0, CS2DemoKit 0.13.0-beta0001, app 0.6.2. "
            + "Incompatible: Strat Book 1.0.0 needs app 0.7.0 or newer; this is 0.6.2.");
    }

    [Test]
    public async Task Describe_UnstampedApp_ReportsItAsUnstamped_AndIsCompatible()
    {
        ExtensionManifest m = FakeManifests.For("net.demoviewer.pack.x", "Strat Book", "1.0.0", "^1.0", "*", "0.7.0");
        ExtensionHostInfo unstamped = _reportHost with { AppVersion = null };
        string report = CompatibilityReport.Describe(m, unstamped);
        await Assert.That(report).IsEqualTo(
            "Strat Book 1.0.0 (net.demoviewer.pack.x): requiresHost ^1.0, requiresCs2DemoKit *, minAppVersion 0.7.0; "
            + "host contract 1.0.0, CS2DemoKit 0.13.0-beta0001, app unstamped. Compatible.");
    }

    // A release gate must fail, not skip: unlike DemoTestHelper.FindRepoRoot (capped at 8 levels, for
    // tests where a detached binary is a legitimate case to shrug off), this walks to the filesystem
    // root with no cap, and trims BaseDirectory's trailing separator so the first comparison counts.
    private static string RepoRoot()
    {
        string? dir = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "DemoViewer.NET.slnx")))
            {
                return dir;
            }

            dir = Path.GetDirectoryName(dir);
        }

        throw new InvalidOperationException(
            $"could not find DemoViewer.NET.slnx above {AppContext.BaseDirectory}; the compatibility gate cannot verify the shipped manifest without it");
    }
}
