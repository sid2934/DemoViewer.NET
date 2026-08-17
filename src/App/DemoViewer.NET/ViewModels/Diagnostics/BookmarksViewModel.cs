#region

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DemoViewer.NET.Models;
using DemoViewer.NET.Services;
using DemoViewer.NET.ViewModels.Common;

#endregion

namespace DemoViewer.NET.ViewModels.Diagnostics;

/// <summary>
///     Bookmarks panel (F8.5 / A4). In-memory model with best-effort desktop persistence via
///     <see cref="BookmarkStore" /> (no-ops on WASM). Selecting a bookmark seeks to its frame through
///     the shared <see cref="FrameNavigationViewModel" />.
/// </summary>
public sealed partial class BookmarksViewModel : ObservableObject
{
    private readonly FrameNavigationViewModel _nav;
    private readonly BookmarkStore _store = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBookmarks))]
    private Bookmark? _selected;

    /// <summary>Initializes a new <see cref="BookmarksViewModel" /> instance.</summary>
    public BookmarksViewModel(FrameNavigationViewModel nav)
    {
        _nav = nav;
        foreach (Bookmark b in _store.Load())
        {
            Bookmarks.Add(b);
        }
    }

    /// <summary>Bookmarks.</summary>
    public ObservableCollection<Bookmark> Bookmarks { get; } = [];

    /// <summary>Has bookmarks.</summary>
    public bool HasBookmarks => Bookmarks.Count > 0;

    /// <summary>
    ///     Adds a bookmark for the given frame (with a label) and persists. Replaces any existing
    ///     bookmark on the same frame so re-bookmarking just updates the label.
    /// </summary>
    public void Add(int frameIndex, int tick, string label)
    {
        if (frameIndex < 0)
        {
            return;
        }

        Bookmark? existing = Bookmarks.FirstOrDefault(b => b.FrameIndex == frameIndex);
        if (existing is not null)
        {
            Bookmarks.Remove(existing);
        }

        Bookmarks.Add(new Bookmark(frameIndex, tick, string.IsNullOrWhiteSpace(label) ? $"frame {frameIndex}" : label));
        Persist();
    }

    partial void OnSelectedChanged(Bookmark? value)
    {
        if (value is not null)
        {
            _nav.SeekToFrame(value.FrameIndex);
        }
    }

    private void Persist()
    {
        OnPropertyChanged(nameof(HasBookmarks));
        _store.Save(Bookmarks);
    }

    [RelayCommand]
    private void Remove(Bookmark? bookmark)
    {
        if (bookmark is null)
        {
            return;
        }

        Bookmarks.Remove(bookmark);
        if (ReferenceEquals(Selected, bookmark))
        {
            Selected = null;
        }

        Persist();
    }
}
