#region

using System.Reflection;
using TUnit.Assertions.Enums;

#endregion

namespace DemoViewer.NET.AppTests.Extensions;

/// <summary>The SDK packages are what third parties build against, so they may reference nothing of the app's.</summary>
public class ExtensionSdkArchitectureTests
{
    [Test]
    public async Task TheSdk_ReferencesOnlyTheModuleAbstractionsAndTheScene_OfThisRepo()
    {
        string[] ours =
        [
            .. typeof(IExtension).Assembly.GetReferencedAssemblies()
                .Select(a => a.Name!)
                .Where(n => n.StartsWith("DemoViewer.NET", StringComparison.Ordinal))
        ];

        await Assert.That(ours).IsEquivalentTo(["DemoViewer.NET.Modules.Abstractions", "DemoViewer.NET.Playback2D.Scene"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task TheUiKit_ReferencesOnlyTheSdkTheIconCatalogueAndTheScene_OfThisRepo()
    {
        string[] ours =
        [
            .. typeof(DisplayText).Assembly.GetReferencedAssemblies()
                .Select(a => a.Name!)
                .Where(n => n.StartsWith("DemoViewer.NET", StringComparison.Ordinal))
        ];

        string[] allowed =
        [
            "DemoViewer.NET.Extensions.Sdk", "DemoViewer.NET.Modules.Abstractions", "DemoViewer.NET.GameIcons",
            "DemoViewer.NET.Playback2D.Scene"
        ];

        await Assert.That(ours.Except(allowed)).IsEmpty();
    }

    [Test]
    public async Task TheSdkFamily_CarriesOneVersion()
    {
        // The package version, not the assembly version: assemblyVersion.precision is "major", so every 1.x
        // assembly stamps 1.0.0.0 and a drift below the major is invisible there. The family ships in
        // lockstep, and Directory.Build.targets pins each intra-family dependency at exactly the sibling's
        // own computed version, so a drift ships as a set with no one version to ask for, under an sdk/v tag
        // that names the SDK's alone.
        //
        // GameIcons is not published but belongs here anyway: it reads the SDK's version.json through
        // GitVersionBaseDirectory, so it is on the same line whether or not it is packed.
        string sdk = ReleaseVersion(typeof(IExtension).Assembly);

        using (Assert.Multiple())
        {
            await Assert.That(ReleaseVersion(typeof(Modules.Abstractions.IModuleContext).Assembly)).IsEqualTo(sdk);
            await Assert.That(ReleaseVersion(typeof(DisplayText).Assembly)).IsEqualTo(sdk);
            await Assert.That(ReleaseVersion(typeof(Playback2D.Scene2DFrame).Assembly)).IsEqualTo(sdk);
            await Assert.That(ReleaseVersion(typeof(GameIcons.IconCatalogue).Assembly)).IsEqualTo(sdk);
        }
    }

    [Test]
    public async Task TheSceneContract_ReferencesNothingOfThisRepo()
    {
        string[] ours =
        [
            .. typeof(Playback2D.Scene2DFrame).Assembly.GetReferencedAssemblies()
                .Select(a => a.Name!)
                .Where(n => n.StartsWith("DemoViewer.NET", StringComparison.Ordinal) || n.StartsWith("Avalonia", StringComparison.Ordinal))
        ];

        await Assert.That(ours).IsEmpty();
    }

    /// <summary>
    ///     <see cref="TheSceneContract_ReferencesNothingOfThisRepo" /> pins the Scene <i>assembly</i>, which is
    ///     a different claim: <c>MapAssets</c> lives in the UI kit, and the UI kit does reference Avalonia.
    ///     A headless picture is drawn from a console process with no toolkit loaded, so the map surface may
    ///     name only the Scene contract, SkiaSharp and the BCL.
    /// </summary>
    [Test]
    public async Task TheMapSurface_NamesOnlyTheSceneContractSkiaAndTheBcl()
    {
        Dictionary<Type, string> seen = [];
        List<string> offenders = [];
        List<string> walked = [];

        foreach (Type root in (Type[]) [typeof(MapAssets), typeof(MapIcons)])
        {
            foreach (MemberInfo member in root.GetMembers(BindingFlags.Public | BindingFlags.Static
                                                          | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                walked.Add($"{root.Name}.{member.Name}");
                foreach (Type named in Named(member))
                {
                    Walk(named, $"{root.Name}.{member.Name}", seen, offenders);
                }
            }
        }

        using (Assert.Multiple())
        {
            // These three exist because the assertion below passes on an empty walk. A BindingFlags set
            // missing Static enumerates no members at all, a walk that stops at the outermost generic
            // never reaches what the list is a list of, and one that stops at an Sdk.Ui type never
            // reaches the resolver only a lease names.
            await Assert.That(walked).Contains("MapAssets.RenderPng");
            await Assert.That(seen.Keys).Contains(typeof(Playback2D.Compositing.ISceneLayer));
            await Assert.That(seen.Keys).Contains(typeof(Playback2D.Zones.PlaceResolver));
            await Assert.That(offenders.Order(StringComparer.Ordinal)).IsEmpty();
        }
    }

    [Test]
    public async Task TheModuleAbstractions_ReferenceNothingOfThisRepo()
    {
        string[] ours =
        [
            .. typeof(Modules.Abstractions.IModuleContext).Assembly.GetReferencedAssemblies()
                .Select(a => a.Name!)
                .Where(n => n.StartsWith("DemoViewer.NET", StringComparison.Ordinal) || n.StartsWith("Avalonia", StringComparison.Ordinal))
        ];

        await Assert.That(ours).IsEmpty();
    }

    [Test]
    public async Task TheSdkAssemblyVersion_IsMajorOnly_SoEveryMinorBindsToTheSameExtensions()
    {
        Version version = typeof(IExtension).Assembly.GetName().Version!;

        await Assert.That(version.Minor + version.Build + version.Revision).IsEqualTo(0);
    }

    // Every type a member names: parameters and returns, not the types those in turn name.
    private static IEnumerable<Type> Named(MemberInfo member) => member switch
    {
        MethodInfo method => [.. method.GetParameters().Select(p => p.ParameterType), method.ReturnType],
        ConstructorInfo constructor => constructor.GetParameters().Select(p => p.ParameterType),
        PropertyInfo property => [property.PropertyType],
        FieldInfo field => [field.FieldType],
        EventInfo @event => [@event.EventHandlerType!],
        _ => []
    };

    // Generic arguments and element types are the whole point: a shallow look at RenderPng's layer
    // parameter sees IReadOnlyList<> and Func<>, both BCL, and would pass whatever they hold.
    private static void Walk(Type type, string path, Dictionary<Type, string> seen, List<string> offenders)
    {
        string trail = $"{path} -> {type.Name}";
        if (!seen.TryAdd(type, trail))
        {
            return;
        }

        if (type.HasElementType)
        {
            Walk(type.GetElementType()!, trail, seen, offenders);
            return;
        }

        if (type.IsConstructedGenericType)
        {
            Walk(type.GetGenericTypeDefinition(), path, seen, offenders);
            foreach (Type argument in type.GenericTypeArguments)
            {
                Walk(argument, trail, seen, offenders);
            }

            return;
        }

        if (type.IsGenericParameter)
        {
            foreach (Type constraint in type.GetGenericParameterConstraints())
            {
                Walk(constraint, trail, seen, offenders);
            }

            return;
        }

        switch (type.Assembly.GetName().Name)
        {
            case "DemoViewer.NET.Playback2D.Scene":
            case "SkiaSharp":
                return;
            case "DemoViewer.NET.Extensions.Sdk.Ui":
                // Published, but it is the assembly that references Avalonia, so its own types are
                // followed rather than trusted: a control on the surface would otherwise read as clean.
                foreach (Type named in Surface(type))
                {
                    Walk(named, trail, seen, offenders);
                }

                return;
            case { } name when Bcl(name):
                return;
            default:
                offenders.Add($"{trail} [{type.FullName} from {type.Assembly.GetName().Name}]");
                return;
        }
    }

    private static IEnumerable<Type> Surface(Type type) =>
    [
        .. type.BaseType is { } @base ? (Type[]) [@base] : [],
        .. type.GetInterfaces(),
        .. type.GetMembers(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance
                           | BindingFlags.DeclaredOnly).SelectMany(Named)
    ];

    private static bool Bcl(string assembly) =>
        assembly is "System" or "netstandard" or "mscorlib"
        || assembly.StartsWith("System.", StringComparison.Ordinal);

    // Nerdbank.GitVersioning writes the release version, then '+' and the commit id, into the informational
    // version. The NuGet package version is this plus a -g<commit> suffix off a release ref, which is the same
    // suffix for every assembly built from one commit, so the part before the '+' is what can drift.
    private static string ReleaseVersion(Assembly assembly) =>
        assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion.Split('+')[0];
}
