#region

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DemoViewer.NET.Controls;
using DemoViewer.NET.Services.Update;

#endregion

namespace DemoViewer.NET.ViewModels.Update;

/// <summary>
///     Backs the update-notice pop-up window — the richer replacement for the old banner-only
///     offer. Wraps the shared <see cref="UpdateViewModel" /> (which owns check/download/apply
///     state) and adds the release notes for the offered version, fetched lazily when the window
///     opens so the pop-up never waits on the network to appear.
///     <para>
///         One instance lives per shell (created on first show, reused after), so the notes fetch
///         happens at most once per run and re-opening the window via the banner's "Details…" is
///         instant.
///     </para>
/// </summary>
public sealed partial class UpdateNoticeViewModel : ViewModelBase
{
    private readonly IReleaseNotesService _notesService;
    private Task? _loadTask;

    /// <summary>Constructs over the shared updater VM and a notes source.</summary>
    public UpdateNoticeViewModel(UpdateViewModel update, IReleaseNotesService notesService)
    {
        Update = update;
        _notesService = notesService;
    }

    /// <summary>
    ///     The shared updater VM — the window binds its Update &amp; Restart command, download
    ///     progress, and status text straight through, so the pop-up and the banner can never
    ///     disagree about update state.
    /// </summary>
    public UpdateViewModel Update { get; }

    /// <summary>Raised when the window hosting this VM should close ("Later").</summary>
    public event EventHandler? CloseRequested;

    /// <summary>Window headline — offered version comes from the shared updater VM.</summary>
    public string HeadlineText => $"DemoViewer.NET {Update.AvailableVersion} is available";

    /// <summary>Secondary line: what the user is running now.</summary>
    public string SubHeadlineText => $"You're running {Update.CurrentVersionDisplay}.";

    /// <summary>True while the notes fetch is in flight (shows the loading line).</summary>
    [ObservableProperty]
    private bool _isLoadingNotes;

    /// <summary>The fetched release-note markdown, rendered by <see cref="MarkdownBlock" />.</summary>
    [ObservableProperty]
    private string? _notesMarkdown;

    /// <summary>Shown instead of notes when the fetch failed (offline, no release body).</summary>
    [ObservableProperty]
    private string? _notesFallback;

    /// <summary>"Published 2 Aug 2026" line under the headline; null hides it.</summary>
    [ObservableProperty]
    private string? _publishedDisplay;

    /// <summary>Browser URL of the release page; null hides the "View on GitHub" button.</summary>
    [ObservableProperty]
    private string? _releaseUrl;

    /// <summary>
    ///     Fetches the notes once; safe to call on every window open. Called by the window's
    ///     <c>OnOpened</c> rather than the constructor, so tests and the designer never touch the
    ///     network.
    /// </summary>
    public Task EnsureNotesLoadedAsync() => _loadTask ??= LoadNotesAsync();

    private async Task LoadNotesAsync()
    {
        string? version = Update.AvailableVersion;
        if (version is null)
        {
            NotesFallback = "Release notes unavailable.";
            return;
        }

        IsLoadingNotes = true;
        try
        {
            ReleaseNotes? notes = await _notesService.GetForVersionAsync(version).ConfigureAwait(true);
            if (notes is null)
            {
                NotesFallback = "Couldn't fetch the release notes — you can read them on the GitHub releases page.";
                ReleaseUrl = "https://github.com/sid2934/DemoViewer.NET/releases";
                return;
            }

            NotesMarkdown = notes.BodyMarkdown;
            ReleaseUrl = notes.HtmlUrl;
            if (notes.PublishedAt is { } published)
            {
                PublishedDisplay = $"Published {published.ToLocalTime():d MMMM yyyy}";
            }
        }
        finally
        {
            IsLoadingNotes = false;
        }
    }

    /// <summary>Closes the pop-up without dismissing the banner — the offer stays visible.</summary>
    [RelayCommand]
    private void Later() => CloseRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>Opens the release page in the default browser.</summary>
    [RelayCommand]
    private void OpenReleasePage()
    {
        if (ReleaseUrl is { } url)
        {
            OpenExternal.OpenUri(url);
        }
    }
}
