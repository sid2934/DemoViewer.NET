#region

using DemoViewer.NET.ViewModels.PlayerOverview;
using DemoViewer.NET.ViewModels.PlayerOverview.Sections;

#endregion

namespace DemoViewer.NET.AppTests;

internal static class PlayerOverviewTestExtensions
{
    // A page renders only while it is on show, so a test reads a section the way a user does: on its page.
    public static T Reveal<T>(this PlayerOverviewTabViewModel tab) where T : PlayerOverviewSection
    {
        T section = tab.Sections.OfType<T>().Single();
        if (!section.IsPersistent)
        {
            tab.ShowPage(section.Page);
        }

        return section;
    }
}
