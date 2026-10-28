#region

using System.Reflection;
using Avalonia.Controls;
using DemoViewer.NET.Extensions.Sdk;
using DemoViewer.NET.Extensions.StratBook;

#endregion

namespace SdkOnlyHost;

/// <summary>
///     The pack's views, found the way the host finds them. The host's locator lives in the app assembly, so
///     nothing here can call it; what it does is a published rule, and the rule is reproduced below character
///     for character: the view model's full type name with every <c>ViewModel</c> replaced by <c>View</c>,
///     looked up in the view model's own assembly, built through a public parameterless constructor and cast
///     to <c>Control</c>.
///     <para>
///         Every way that rule can fail is silent. A view the rule cannot name renders as
///         <c>"Not Found: " + name</c>; one whose constructor the host cannot call is swallowed by the fault
///         guard and replaced by a placeholder. Neither throws, neither fails a build, and a third party
///         discovers both by looking at a blank pane.
///     </para>
/// </summary>
public class PackViewsThroughTheSdkTests
{
    private static readonly Assembly _pack = typeof(StratBookPack).Assembly;

    /// <summary>
    ///     The marked view models the pack renders from its own markup, so no host looks a view up for one:
    ///     each is bound through a <c>DataTemplate</c> or an inlined <c>DataContext</c> of the pack's own, and
    ///     <c>StratRecordCountCell</c> only ever has its properties bound. Named rather than derived, because
    ///     nothing in the assembly tells the two cases apart; the staleness test below re-derives every row,
    ///     so a type that grows a view leaves the list.
    /// </summary>
    private static readonly string[] _renderedByThePacksOwnMarkup =
    [
        "DemoViewer.NET.Extensions.StratBook.ViewModels.Review.ReviewRowViewModel",
        "DemoViewer.NET.Extensions.StratBook.ViewModels.RoundTagger.TagMatrixCellViewModel",
        "DemoViewer.NET.Extensions.StratBook.ViewModels.Situations.QueryRailSlotViewModel",
        "DemoViewer.NET.Extensions.StratBook.ViewModels.Situations.ResultCardViewModel",
        "DemoViewer.NET.Extensions.StratBook.ViewModels.Situations.WatchedSituationRowViewModel",
        "DemoViewer.NET.Extensions.StratBook.ViewModels.StratBook.StratHistoryPanelViewModel",
        "DemoViewer.NET.Extensions.StratBook.ViewModels.StratBook.StratRecordCountCell",
        "DemoViewer.NET.Extensions.StratBook.ViewModels.StratBook.StratRecordPanelViewModel",
        "DemoViewer.NET.Extensions.StratBook.ViewModels.StratBook.StratRoleViewPanelViewModel"
    ];

    /// <summary>
    ///     Every marked view model a host has to resolve, resolved. The marker is the opt-in: carrying it is
    ///     what makes the host's template match the view model at all, so a marked type the rule cannot place
    ///     is one the host will draw as text.
    /// </summary>
    [Test]
    public async Task NamingRule_FindsAConstructibleView_ForEveryMarkedViewModelAHostResolves()
    {
        List<string> unresolved = [];
        List<string> resolved = [];

        foreach (Type viewModel in MarkedViewModels())
        {
            if (_renderedByThePacksOwnMarkup.Contains(viewModel.FullName, StringComparer.Ordinal))
            {
                continue;
            }

            string name = ViewNameFor(viewModel);
            Type? view = _pack.GetType(name, throwOnError: false);

            if (view is null)
            {
                unresolved.Add($"{viewModel.FullName} -> no type named {name}");
            }
            else if (view.IsAbstract)
            {
                unresolved.Add($"{viewModel.FullName} -> {name} is abstract");
            }
            else if (!typeof(Control).IsAssignableFrom(view))
            {
                unresolved.Add($"{viewModel.FullName} -> {name} is not a Control");
            }
            else if (view.GetConstructor(Type.EmptyTypes) is null)
            {
                // The host builds the view with Activator.CreateInstance(Type), which sees public
                // constructors only; an internal view type with a public constructor is fine.
                unresolved.Add($"{viewModel.FullName} -> {name} has no public parameterless constructor");
            }
            else
            {
                resolved.Add($"{viewModel.FullName} -> {view.FullName}");
            }
        }

        unresolved.ForEach(Console.WriteLine);
        await Assert.That(unresolved).IsEmpty();

        // A rule that placed nothing would pass every assertion above.
        resolved.ForEach(Console.WriteLine);
        await Assert.That(resolved.Count).IsGreaterThan(0);
    }

    /// <summary>
    ///     The exempt list, checked against the assembly rather than trusted. A name that no longer resolves,
    ///     no longer carries the marker or has since grown a view is a row to delete, and deleting it is what
    ///     puts the type back under the rule.
    /// </summary>
    [Test]
    public async Task TheMarkupRenderedViewModels_StillCarryTheMarkerAndStillHaveNoView()
    {
        List<string> stale = [];

        foreach (string name in _renderedByThePacksOwnMarkup)
        {
            Type? viewModel = _pack.GetType(name, throwOnError: false);

            if (viewModel is null)
            {
                stale.Add($"{name} is not in the pack any more: drop the row");
                continue;
            }

            if (!typeof(IExtensionViewModel).IsAssignableFrom(viewModel))
            {
                stale.Add($"{name} no longer carries IExtensionViewModel: drop the row");
                continue;
            }

            Type? view = _pack.GetType(ViewNameFor(viewModel), throwOnError: false);
            if (view is not null && typeof(Control).IsAssignableFrom(view))
            {
                stale.Add($"{name} now resolves to {view.FullName}: drop the row so the rule covers it");
            }
        }

        stale.ForEach(Console.WriteLine);
        await Assert.That(stale).IsEmpty();
        await Assert.That(_renderedByThePacksOwnMarkup.Distinct(StringComparer.Ordinal).Count())
            .IsEqualTo(_renderedByThePacksOwnMarkup.Length);
    }

    /// <summary>
    ///     The host's transform: a replace over the whole name, so the <c>ViewModels</c> namespace segment
    ///     becomes <c>Views</c> in the same pass that turns <c>FooViewModel</c> into <c>FooView</c>. A suffix
    ///     rule would read the same on the pack's tabs and diverge from the host everywhere else.
    /// </summary>
    private static string ViewNameFor(Type viewModel) =>
        viewModel.FullName!.Replace("ViewModel", "View", StringComparison.Ordinal);

    /// <summary>
    ///     The pack's view models by the marker, not by name: a type that inherits it is as much the host's
    ///     to draw as one that names it.
    /// </summary>
    private static Type[] MarkedViewModels()
    {
        Type[] types;
        try
        {
            types = _pack.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            // A pack type that cannot load beside the published libraries alone is itself the finding.
            throw new InvalidOperationException(
                "the pack's types do not all load in an SDK-only host: "
                + string.Join("; ", ex.LoaderExceptions.Select(e => e?.Message)), ex);
        }

        return
        [
            .. types.Where(t => typeof(IExtensionViewModel).IsAssignableFrom(t))
                .Where(t => t is { IsAbstract: false, IsInterface: false, IsGenericTypeDefinition: false })
                .OrderBy(t => t.FullName, StringComparer.Ordinal)
        ];
    }
}
