#region

using System.Reflection;
using DemoViewer.NET.Extensions;
using DemoViewer.NET.Services;
using TUnit.Assertions.Enums;

#endregion

namespace DemoViewer.NET.AppTests.Extensions;

/// <summary>
///     <see cref="FirstPartyHost" /> is the Strat Book's one way past the SDK's storage, and it names the app's
///     zones overlay folder alone: no root folder, no store of the pack's own (those go through
///     <see cref="IExtensionContext.Stores" />), so the pack cannot reach settings.json, another extension's
///     data or the shared cache through it.
/// </summary>
[NotInParallel]
public class FirstPartyHostSurfaceTests
{
    /// <summary>Every public member, with the reason it is there. A new member needs a reason here first.</summary>
    private static readonly Dictionary<string, string> Allowed = new(StringComparer.Ordinal)
    {
        ["ZonesDirectory"] = "the app's zones overlay, read only; the zone loader reads it per call",
    };

    [Test]
    public async Task EveryPublicMember_HasAReason()
    {
        string[] members =
        [
            .. typeof(FirstPartyHost)
                .GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Where(m => m is PropertyInfo or FieldInfo or EventInfo or MethodInfo { IsSpecialName: false })
                .Select(m => m.Name)
                .Order(StringComparer.Ordinal)
        ];

        await Assert.That(members).IsEquivalentTo(Allowed.Keys.Order(StringComparer.Ordinal), CollectionOrdering.Matching)
            .Because("FirstPartyHost grows only by something of the app's the pack reads and cannot declare as a store, with its reason in Allowed");
    }

    [Test]
    public async Task TheZonesFolder_IsTheAppsOwn() =>
        await Assert.That(new FirstPartyHost().ZonesDirectory).IsEqualTo(AppPaths.ZonesDirectory);
}
