#region

using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DemoViewer.NET.Extensions;
using DemoViewer.NET.Modules.Library;
using DemoViewer.NET.ViewModels.Library;
using DemoViewer.NET.Views.Library;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     A real <see cref="LibraryTabView" />'s card badge chip, over a fake <see cref="ILibraryContribution" />
///     that only tracks calls (the real Team/Provenance contributions are
///     <c>DemoViewer.NET.AppTests.Extensions.StratBook.LibraryContributionsTests</c>'s job). Opens the
///     real <c>MenuFlyout</c> and clicks a realized <c>MenuItem</c>, so <c>LibraryTabView.OnBadgeLabelPicked</c>'s
///     walk-up from the item (inside the flyout's popup) back to the card's <see cref="DemoEntry" /> is
///     actually exercised, not just the view-model's own <see cref="LibraryTabViewModel.SetBadgeLabel" />.
/// </summary>
[Category("Integration")]
public class LibraryBadgeMenuRenderTests
{
    // Tracks SetLabel calls; BadgeFor/BadgesFor hand back whatever is currently pinned, so a picked row's
    // effect (including the reset row clearing it) is observable without a real provenance source.
    private sealed class TrackingContribution : ILibraryContribution
    {
        private string? _pinned;

        public List<(DemoEntry Entry, string? Label)> SetLabelCalls { get; } = [];

        public event Action? Changed;

        public LibraryFilter? Filter => null;
        public bool HasBadge => true;
        public LibraryBadge? BadgeFor(DemoEntry entry) => new(_pinned ?? "unlabeled", null, _pinned is not null);
        public IReadOnlyList<string> BadgeLabels => ["official", "scrim", "our scrim", "matchmaking"];
        public string? BadgeResetLabel => "Automatic";
        public string? BadgeResetTooltip => "Let the clan tags, the header and Team Identity decide";

        public void SetLabel(DemoEntry entry, string? label)
        {
            SetLabelCalls.Add((entry, label));
            _pinned = label;
            Changed?.Invoke();
        }
    }

    private static (DemoLibraryService Library, DemoEntry Entry, LibraryTabView View, Window Window, Button Chip) Attach(TrackingContribution contribution)
    {
        DemoLibraryService lib = new(a => a(), Path.Combine(Path.GetTempPath(), "dvlibbadgemenu_" + Guid.NewGuid().ToString("N") + ".json"));
        DemoEntry entry = new()
        {
            FilePath = "/d/a.dem",
            FileName = "a.dem",
            Directory = "/d",
            FileSizeBytes = 1000,
            Modified = new DateTime(2026, 3, 1),
            State = DemoIndexState.Indexed
        };
        lib.Entries.Add(entry);

        LibraryTabViewModel vm = new(lib, _ => Task.CompletedTask,
            () => Task.FromResult<IReadOnlyList<string>>([]), contributions: [contribution]);
        LibraryTabView view = new() { DataContext = vm };
        Window window = new() { Width = 1280, Height = 800, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        Button chip = view.GetVisualDescendants().OfType<Button>()
            .Single(b => b.Classes.Contains("badgeChip") && b.DataContext == entry);
        return (lib, entry, view, window, chip);
    }

    [Test]
    public async Task OpeningTheFlyout_AndClickingALabel_SetsItOnTheCardsEntry() =>
        await HeadlessSession.RunOnUi(async () =>
        {
            TrackingContribution contribution = new();
            (_, DemoEntry entry, _, Window window, Button chip) = Attach(contribution);

            chip.Flyout!.ShowAt(chip);
            Dispatcher.UIThread.RunJobs();

            MenuItem official = window.GetVisualDescendants().OfType<MenuItem>()
                .Single(i => i.Tag is LibraryBadgeMenuEntry { IsReset: false, Label: "official" });
            official.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Dispatcher.UIThread.RunJobs();

            using (Assert.Multiple())
            {
                await Assert.That(contribution.SetLabelCalls.Count).IsEqualTo(1)
                    .Because("the walk-up from the realized MenuItem must reach the card's own DemoEntry");
                await Assert.That(contribution.SetLabelCalls[0].Entry).IsEqualTo(entry);
                await Assert.That(contribution.SetLabelCalls[0].Label).IsEqualTo("official");
                await Assert.That(entry.BadgeLabel).IsEqualTo("official");
            }

            window.Close();
        });

    [Test]
    public async Task OpeningTheFlyout_AndClickingTheResetRow_ClearsThePin() =>
        await HeadlessSession.RunOnUi(async () =>
        {
            TrackingContribution contribution = new();
            (_, DemoEntry entry, _, Window window, Button chip) = Attach(contribution);

            contribution.SetLabel(entry, "scrim");
            Dispatcher.UIThread.RunJobs();
            await Assert.That(entry.BadgeIsPinned).IsTrue().Because("pinned before the menu is opened, so the reset has something to undo");

            chip.Flyout!.ShowAt(chip);
            Dispatcher.UIThread.RunJobs();

            MenuItem reset = window.GetVisualDescendants().OfType<MenuItem>()
                .Single(i => i.Tag is LibraryBadgeMenuEntry { IsReset: true });
            await Assert.That(reset.Classes.Contains("reset")).IsTrue().Because("the divider style marks the reset row");

            reset.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Dispatcher.UIThread.RunJobs();

            using (Assert.Multiple())
            {
                await Assert.That(contribution.SetLabelCalls.Count).IsEqualTo(2);
                await Assert.That(contribution.SetLabelCalls[1].Entry).IsEqualTo(entry);
                await Assert.That(contribution.SetLabelCalls[1].Label).IsNull().Because("the reset row clears the pin");
                await Assert.That(entry.BadgeIsPinned).IsFalse();
            }

            window.Close();
        });
}
