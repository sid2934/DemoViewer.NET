#region

using CommunityToolkit.Mvvm.ComponentModel;

#endregion

namespace DemoViewer.NET.ViewModels.PlayerOverview.Sections;

/// <summary>
///     One panel of the Player Overview: a view model here and a view of the same name under
///     <c>Views/PlayerOverview/Sections</c>, which the view locator pairs by name. The tab builds every section once
///     from <see cref="PlayerOverviewSections.Create" />, in that order, and hands each one the same
///     <see cref="PlayerOverviewContext" /> whenever the subject or the library moves.
/// </summary>
public abstract partial class PlayerOverviewSection : ViewModelBase
{
    /// <summary>False until the section has something to show for the current context.</summary>
    [ObservableProperty]
    private bool _isVisible;

    /// <summary>A stable id for tests and captures.</summary>
    public abstract string Id { get; }

    /// <summary>Which column the section takes on a wide tab.</summary>
    public virtual SectionColumn Column => SectionColumn.Main;

    /// <summary>
    ///     Renders <paramref name="context" />. Called on the UI thread while the tab is shown, after any change to
    ///     the subject, the library or the queue; a section that reads files starts that work off the thread here
    ///     and drops a result whose subject is no longer current.
    /// </summary>
    /// <param name="context">The subject and the demos they are in.</param>
    public abstract void Update(PlayerOverviewContext context);
}
