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
    /// <summary>
    ///     The marked view models today. A floor rather than an exact figure: a new view model is covered by
    ///     the rule the moment it ships, while a drop means one lost the marker, and with it the naming rule.
    /// </summary>
    private const int MarkedViewModelsToday = 13;

    private static readonly Assembly _pack = typeof(StratBookPack).Assembly;

    /// <summary>
    ///     Every marked view model resolves, with no exceptions. The marker is the opt-in: carrying it is what
    ///     makes the host's template match the view model at all, so a marked type the rule cannot place is one
    ///     the host will draw as text. A view model with no view the rule can place does not carry it.
    /// </summary>
    [Test]
    public async Task NamingRule_FindsAConstructibleView_ForEveryMarkedViewModel()
    {
        List<string> unresolved = [];
        List<string> resolved = [];

        foreach (Type viewModel in MarkedViewModels())
        {
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

        // A marker nothing carries would satisfy every assertion above.
        resolved.ForEach(Console.WriteLine);
        await Assert.That(resolved.Count).IsGreaterThanOrEqualTo(MarkedViewModelsToday);
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
