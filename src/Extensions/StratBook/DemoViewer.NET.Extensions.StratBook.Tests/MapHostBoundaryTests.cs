#region

using System.Reflection;
using DemoViewer.NET.Extensions.Sdk.Ui.Controls;
using DemoViewer.NET.Extensions.StratBook.Modules.Situations;
using DemoViewer.NET.Extensions.StratBook.Modules.UtilityBook;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The Query Canvas and the Utility Book map are <see cref="MapView" /> consumers: neither derives from
///     an app type, and neither holds anything of the app's map renderer, only the public view.
/// </summary>
public class MapHostBoundaryTests
{
    private static readonly Assembly App = typeof(App).Assembly;

    [Test]
    [Arguments(typeof(QueryCanvasHost))]
    [Arguments(typeof(UtilityMapHost))]
    public async Task AMapHost_DerivesFromNoAppType_AndHoldsAMapView(Type host)
    {
        List<Type> bases = [];
        for (Type? t = host.BaseType; t is not null; t = t.BaseType)
        {
            bases.Add(t);
        }

        FieldInfo[] fields = host.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

        using (Assert.Multiple())
        {
            await Assert.That(bases.Where(t => t.Assembly == App)).IsEmpty();
            await Assert.That(fields.Where(f => f.FieldType.Assembly == App)).IsEmpty();
            await Assert.That(fields.Count(f => f.FieldType == typeof(MapView))).IsEqualTo(1);
        }
    }
}
