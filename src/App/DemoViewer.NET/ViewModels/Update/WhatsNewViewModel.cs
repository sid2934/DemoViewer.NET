#region

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DemoViewer.NET.Controls;
using DemoViewer.NET.Services.Update;

#endregion

namespace DemoViewer.NET.ViewModels.Update;

/// <summary>
///     Backs the post-update "What's new" window, shown once on the first launch after the
///     running version changes (gated by <c>AppSettings.LastSeenVersion</c>, advanced before the
///     window opens so a crash can never re-show it in a loop). Renders the RUNNING version's
///     release notes; the update notice renders the OFFERED version's.
/// </summary>
public sealed partial class WhatsNewViewModel : ViewModelBase
{
    private readonly IReleaseNotesService _notesService;

    /// <summary>True while the notes fetch is in flight.</summary>
    [ObservableProperty]
    private bool _isLoading;

    private Task? _loadTask;

    /// <summary>Shown instead of notes when the fetch failed.</summary>
    [ObservableProperty]
    private string? _notesFallback;

    /// <summary>The release-note markdown, rendered by <see cref="MarkdownBlock" />.</summary>
    [ObservableProperty]
    private string? _notesMarkdown;

    /// <summary>"Published 2 Aug 2026" line; null hides it.</summary>
    [ObservableProperty]
    private string? _publishedDisplay;

    /// <summary>Browser URL of the release page; null hides the "View on GitHub" button.</summary>
    [ObservableProperty]
    private string? _releaseUrl;

    /// <summary>Constructs for the given (already normalized x.y.z) running version.</summary>
    public WhatsNewViewModel(string version, IReleaseNotesService notesService)
    {
        Version = version;
        _notesService = notesService;
    }

    /// <summary>The running version whose notes are shown.</summary>
    public string Version { get; }

    /// <summary>Window headline.</summary>
    public string HeadlineText => $"What's new in {Version}";

    /// <summary>Raised when the window hosting this VM should close.</summary>
    public event EventHandler? CloseRequested;

    /// <summary>
    ///     Fetches the notes once; called by the window's <c>OnOpened</c> so tests and the
    ///     designer never touch the network.
    /// </summary>
    public Task EnsureLoadedAsync() => _loadTask ??= LoadAsync();

    private async Task LoadAsync()
    {
        IsLoading = true;
        try
        {
            ReleaseNotes? notes = await _notesService.GetForVersionAsync(Version).ConfigureAwait(true);
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
            IsLoading = false;
        }
    }

    /// <summary>Closes the window.</summary>
    [RelayCommand]
    private void Close() => CloseRequested?.Invoke(this, EventArgs.Empty);

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
