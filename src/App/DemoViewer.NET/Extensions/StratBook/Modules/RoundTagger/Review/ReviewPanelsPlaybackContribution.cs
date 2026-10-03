#region

using System.ComponentModel;
using Avalonia;
using Avalonia.Input;
using Avalonia.Threading;
using DemoViewer.NET.Configuration;
using DemoViewer.NET.Extensions;
using DemoViewer.NET.Modules.Abstractions;
using DemoViewer.NET.Modules.Playback2D;
using DemoViewer.NET.Modules.Playback2D.Timeline;
using DemoViewer.NET.Modules.RoundTagger.Palette;
using DemoViewer.NET.Modules.RoundTagger.Timeline;
using DemoViewer.NET.Modules.SuggestedTags;
using DemoViewer.NET.Playback2D.Core;
using DemoViewer.NET.Playback2D.Core.Levels;
using DemoViewer.NET.Playback2D.Core.Timeline;
using DemoViewer.NET.Services.Tags;
using DemoViewer.NET.Views.RoundTagger;
using DemoViewer.NET.Views.SuggestedTags;

#endregion

namespace DemoViewer.NET.Modules.RoundTagger.Review;

/// <summary>
///     Review mode's right-column panels in 2D Playback as one playback contribution (item 17): the Tag
///     Palette, the review panel (the Suggested / Labels toggle and the shared editor) and the Suggestion
///     Queue, in that order, each under its own gate. The contribution also carries what the panels do on
///     the tab: the palette's and the queue's keys ahead of the keymap, the pack's keymap actions, Click To
///     Tag Position, and the tag lane's menu, press, drag and label handlers over the lanes the tab still
///     registers (item 18 moves those with the lanes). Nothing here exists while the pack is off.
/// </summary>
/// <param name="post">Marshals change notifications onto the UI thread; synchronous when omitted (tests).</param>
public sealed class ReviewPanelsPlaybackContribution(Action<Action>? post = null) : IPlaybackContribution
{
    /// <summary>The palette's order in the column.</summary>
    public const int PaletteOrder = 0;

    /// <summary>The review panel's order in the column.</summary>
    public const int ReviewOrder = 1;

    /// <summary>The queue's order in the column.</summary>
    public const int QueueOrder = 2;

    private readonly Action<Action> _post = post ?? (action => action());
    private readonly List<IDisposable> _registrations = [];
    private IModuleContext? _context;
    private ProposalTrack? _proposalTrack;
    private SettingsService? _settings;
    private TagEditorViewModel? _spanEditor;
    private SuggestionQueueView? _queueView;
    private IPlaybackSurface? _surface;
    private TagTrack? _tagTrack;

    /// <summary>The palette while attached. For tests.</summary>
    public TagPaletteViewModel? Palette { get; private set; }

    /// <summary>The queue while attached. For tests.</summary>
    public SuggestionQueueViewModel? Queue { get; private set; }

    /// <summary>The review panel while it is open: closed when both narrower gates are off. For tests.</summary>
    public ReviewPanelViewModel? Review { get; private set; }

    /// <summary>The palette's panel handle while attached. For tests.</summary>
    public IPanelHandle? PalettePanel { get; private set; }

    /// <summary>The review panel's handle while attached. For tests.</summary>
    public IPanelHandle? ReviewPanel { get; private set; }

    /// <summary>The queue's panel handle while attached. For tests.</summary>
    public IPanelHandle? QueuePanel { get; private set; }

    /// <summary>The open demo's tag session, the lane's. For tests.</summary>
    public TagSession? Session => _tagTrack?.Session;

    /// <summary>The palette has the keyboard: it is on screen and focused.</summary>
    public bool IsPaletteFocused => PalettePanel is { IsShown: true } && Palette is { IsFocused: true };

    private bool TaggerOn => _context?.Features?.IsEnabled(RoundTaggerModule.PaletteFeatureId) ?? true;

    private bool SuggestionsOn => _context?.Features?.IsEnabled(SuggestedTagsService.FeatureId) ?? true;

    /// <inheritdoc />
    public void Attach(IPlaybackSurface surface, IModuleContext context)
    {
        ArgumentNullException.ThrowIfNull(surface);
        ArgumentNullException.ThrowIfNull(context);
        Detach();

        // The lanes the tab registers are the session's way in; a surface without them has nothing to review.
        TagTrack? tagTrack = surface.Timeline.RegisteredTracks.OfType<TagTrack>().FirstOrDefault();
        ProposalTrack? proposalTrack = surface.Timeline.RegisteredTracks.OfType<ProposalTrack>().FirstOrDefault();
        if (tagTrack is null || proposalTrack is null)
        {
            return;
        }

        _surface = surface;
        _context = context;
        _tagTrack = tagTrack;
        _proposalTrack = proposalTrack;
        _settings = context.GetService<SettingsService>();
        TagSession session = tagTrack.Session;

        // The palette edits through the lane's session. The playhead it tags at is the shared clock's tick;
        // its button colours become the track's.
        TagPaletteViewModel palette = new(session, context.GetService<TagPaletteStore>(), Playhead, TickRate, _post);
        palette.SelectPalette(_settings?.Current.Playback2D.TagPaletteId);
        palette.PaletteChosen += SavePaletteSetting;
        palette.PropertyChanged += OnPaletteChanged;
        palette.ApplyKeymap(surface.Keymap);
        tagTrack.CodeColour = code => palette.Palette.ColourOf(code);
        Palette = palette;

        Queue = new SuggestionQueueViewModel(context.GetService<SuggestedTagsService>(), proposalTrack, Seek, TickRate, _post,
            () => _settings?.Current.Playback2D.SuggestedTagsBackground ?? false, SaveBackgroundSetting);

        PalettePanel = surface.AddPanel(PaletteOrder, () => palette,
            () => new TagPaletteView { Margin = new Thickness(0, 0, 0, 2) }, RoundTaggerModule.PaletteFeatureId);
        ReviewPanel = surface.AddPanel(ReviewOrder, BuildReview, () => new ReviewPanelView());
        QueuePanel = surface.AddPanel(QueueOrder, () => Queue, BuildQueueView, SuggestedTagsService.FeatureId);
        ReviewPanel.Closed += OnReviewClosed;
        PalettePanel.ShownChanged += RefreshLaneEditing;
        PalettePanel.Open();
        QueuePanel.Open();
        RefreshReviewPanel();

        _registrations.Add(surface.AddKeyHandler(OnKey));
        _registrations.Add(surface.AddActionHandler(OnAction));
        _registrations.Add(surface.AddMapClickHandler(OnMapClick));
        _registrations.Add(surface.AddBandMenu(LaneMenuFor));
        surface.KeymapChanged += OnKeymapChanged;
        surface.ReviewModeChanged += OnReviewModeChanged;
        surface.Deactivated += LeavePalette;
        surface.Timeline.EditSpanDragged += OnEditSpanDragged;
        surface.Timeline.LaneLabelRequested += OnLaneLabelRequested;
        surface.Timeline.BandPressed += OnBandPressed;
        surface.Timeline.PropertyChanged += OnTimelineChanged;
        session.Changed += OnSessionChanged;
        session.Detaching += LeavePalette;
        if (context.Features is { } features)
        {
            features.Changed += OnFeaturesChanged;
        }

        AttachQueue();
        RefreshLaneEditing();
    }

    /// <inheritdoc />
    public void Detach()
    {
        if (_surface is not { } surface)
        {
            return;
        }

        surface.KeymapChanged -= OnKeymapChanged;
        surface.ReviewModeChanged -= OnReviewModeChanged;
        surface.Deactivated -= LeavePalette;
        surface.Timeline.EditSpanDragged -= OnEditSpanDragged;
        surface.Timeline.LaneLabelRequested -= OnLaneLabelRequested;
        surface.Timeline.BandPressed -= OnBandPressed;
        surface.Timeline.PropertyChanged -= OnTimelineChanged;
        if (_context?.Features is { } features)
        {
            features.Changed -= OnFeaturesChanged;
        }

        if (_tagTrack is { } tagTrack)
        {
            tagTrack.Session.Changed -= OnSessionChanged;
            tagTrack.Session.Detaching -= LeavePalette;
            tagTrack.CodeColour = null;
        }

        foreach (IDisposable registration in _registrations)
        {
            registration.Dispose();
        }

        _registrations.Clear();
        HookSpanEditor(null);
        surface.Timeline.IsLaneEditable = false;
        surface.Timeline.SetEditSpan(null);

        // The pending tag is written before its palette goes; the panels' Dispose then disposes the view models.
        if (Palette is { } palette)
        {
            palette.PaletteChosen -= SavePaletteSetting;
            palette.PropertyChanged -= OnPaletteChanged;
            palette.Finish();
        }

        if (ReviewPanel is { } review)
        {
            review.Closed -= OnReviewClosed;
        }

        if (PalettePanel is { } palettePanel)
        {
            palettePanel.ShownChanged -= RefreshLaneEditing;
        }

        ReviewPanel?.Dispose();
        PalettePanel?.Dispose();
        QueuePanel?.Dispose();
        _queueView = null;
        if (Review is { } panel)
        {
            panel.PropertyChanged -= OnReviewChanged;
        }

        Review = null;
        Palette = null;
        Queue = null;
        PalettePanel = null;
        ReviewPanel = null;
        QueuePanel = null;
        _tagTrack = null;
        _proposalTrack = null;
        _settings = null;
        _surface = null;
        _context = null;
    }

    private int Playhead() => _context?.CurrentTick ?? 0;

    private int TickRate() => _context?.TickRate ?? 64;

    private void Seek(int tick) => _context?.RequestSeekToTick(tick);

    private object BuildReview()
    {
        ReviewPanelViewModel panel = new(_tagTrack!.Session, Queue!, Palette!, Playhead, TickRate, Seek)
        {
            IsSuggestedAvailable = SuggestionsOn,
            IsLabelsAvailable = TaggerOn
        };
        panel.PropertyChanged += OnReviewChanged;
        Review = panel;
        FollowSuggestedTab();
        return panel;
    }

    // The queue is the review panel's Suggested tab: its own panel in the column, shown only while that
    // tab is selected, and the panel stays open so the queue keeps its demo and selection across the tabs.
    private Avalonia.Controls.Control BuildQueueView()
    {
        _queueView = new SuggestionQueueView { Margin = new Thickness(0, 0, 0, 2) };
        FollowSuggestedTab();
        return _queueView;
    }

    private void FollowSuggestedTab()
    {
        if (_queueView is { } view)
        {
            view.IsVisible = Review?.IsSuggestedTab ?? true;
        }
    }

    private void OnReviewClosed()
    {
        if (Review is { } panel)
        {
            panel.PropertyChanged -= OnReviewChanged;
            Review = null;
        }

        HookSpanEditor(null);
        UpdateEditSpan();
        FollowSuggestedTab();
    }

    // The review panel hosts the editor both lists share, so it exists while either gate is on.
    private void RefreshReviewPanel()
    {
        if (ReviewPanel is not { } review)
        {
            return;
        }

        if (TaggerOn || SuggestionsOn)
        {
            review.Open();
            if (Review is { } panel)
            {
                panel.IsSuggestedAvailable = SuggestionsOn;
                panel.IsLabelsAvailable = TaggerOn;
            }
        }
        else
        {
            review.Close();
        }
    }

    // The queue shows the demo the tag session holds, or nothing while its gate is off.
    private void AttachQueue()
    {
        if (Queue is { } queue && _tagTrack is { } track)
        {
            queue.Attach(SuggestionsOn ? track.Session.DemoPath : null, track.Session.Document?.Demo.Sha256);
        }
    }

    private void OnSessionChanged() => OnUi(() =>
    {
        AttachQueue();
        RefreshLaneEditing();
    });

    private void OnFeaturesChanged()
    {
        // Gated off, the palette gives the keyboard back: its keys would otherwise keep shadowing the
        // tab's behind a panel the user can no longer see.
        if (!TaggerOn && Palette is { IsFocused: true } palette)
        {
            palette.Leave();
        }

        AttachQueue();
        RefreshReviewPanel();
        RefreshLaneEditing();
    }

    // The keyboard is the tab's again when the tab goes (deactivation) or the document goes (a swap or a
    // detach): the pending tag is written to the document still current, the note is dropped, focus is off.
    private void LeavePalette() => Palette?.Leave();

    private void OnKeymapChanged()
    {
        if (_surface is { } surface)
        {
            Palette?.ApplyKeymap(surface.Keymap);
        }
    }

    private void OnReviewModeChanged()
    {
        if (_surface is { IsReviewMode: false })
        {
            // Leaving writes a tag in progress rather than stranding it, and drops the selection that
            // held J and K for the queue.
            if (Palette is { IsFocused: true } palette)
            {
                palette.Leave();
            }

            if (Queue is { } queue)
            {
                queue.Selected = null;
            }

            if (Review is { } panel)
            {
                panel.SelectedLabel = null;
                panel.CloseEditorCommand.Execute(null);
            }
        }

        RefreshLaneEditing();
    }

    private void OnPaletteChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TagPaletteViewModel.IsFocused) && PalettePanel is { } panel && Palette is { } palette)
        {
            panel.HasKeyboard = palette.IsFocused;
        }
    }

    private void OnTimelineChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Playback2DTimelineViewModel.CurrentTick))
        {
            Palette?.RefreshLabelTarget(); // Label Mode's tag is the one under the playhead unless one is picked
        }
    }

    // The palette's turn at a key while it has focus, then the queue's while a proposal is selected: the
    // WhenPaletteFocused rows and the panel hotkeys, then the WhenSuggestionSelected rows (J and K walk the
    // queue there and the Situations result set otherwise).
    private bool OnKey(Key key, KeyModifiers modifiers)
    {
        if (IsPaletteFocused && Palette!.TryHandleKey(key, modifiers))
        {
            return true;
        }

        return QueuePanel is { IsShown: true } && Queue is { HasSelection: true } queue && _surface is { } surface
               && surface.Keymap.TryResolveInScope(Playback2DBindingScope.WhenSuggestionSelected, key, modifiers,
                   out Playback2DAction action)
               && queue.Execute(action);
    }

    // The pack's keymap actions. Undo and redo reach here first while the palette has the keyboard, so one
    // history per document kind, resolved by focus (tag-store.md §3.9).
    private bool OnAction(Playback2DAction action)
    {
        bool focused = IsPaletteFocused;
        switch (action)
        {
            case Playback2DAction.Undo when focused:
                Palette!.Finish();
                _tagTrack!.Session.Undo();
                return true;

            case Playback2DAction.Redo when focused:
                _tagTrack!.Session.Redo();
                return true;

            case Playback2DAction.FocusTagPalette:
                return ToggleFocus();

            case Playback2DAction.TagPaletteBack:
            case Playback2DAction.TagNote:
            case Playback2DAction.TagClearSticky:
            case Playback2DAction.TagLabelMode:
            case Playback2DAction.TagLabelGroupNext:
                return focused && Palette!.Execute(action);

            case Playback2DAction.SuggestionNext:
            case Playback2DAction.SuggestionPrev:
            case Playback2DAction.SuggestionAccept:
            case Playback2DAction.SuggestionReject:
            case Playback2DAction.SuggestionEdit:
            case Playback2DAction.SuggestionAcceptAll:
                return QueuePanel is { IsShown: true } && Queue!.Execute(action);

            default:
                return false;
        }
    }

    // Outside Review mode C does nothing: the palette is not on screen, and entering the mode is Shift+R's.
    private bool ToggleFocus()
    {
        if (PalettePanel is not { IsShown: true } || Palette is not { } palette)
        {
            return false;
        }

        if (palette.IsFocused)
        {
            palette.Leave();
            return true;
        }

        return palette.Focus();
    }

    // Click To Tag Position: with an editor open the point goes on the tag being edited; else, while the
    // palette has focus, on the tag being made or the last one written. The place comes from the map's
    // zones when it has them, else from the nearest pawn on that floor in the frame on screen.
    private bool OnMapClick(MapLevel level, double worldX, double worldY)
    {
        if (_surface is not { } surface || Palette is not { } palette)
        {
            return false;
        }

        bool toEditor = surface.IsReviewMode && Review is { HasEditor: true };
        if (!toEditor && (!IsPaletteFocused || palette.IsEditingNote))
        {
            return false;
        }

        Scene2DFrame frame = surface.CurrentFrame;
        TagPosition position = TagPositionResolver.Resolve(worldX, worldY, level, Playhead(), surface.Zones, frame.Markers);
        return toEditor ? Review!.AddPosition(position) : palette.AttachPosition(position);
    }

    // Lane editing (item 18 moves this with the lanes): editable in Review mode with the palette's gate on
    // and a demo attached; the open editor's span drawn with handles.
    private void RefreshLaneEditing()
    {
        if (_surface is not { } surface || _tagTrack is not { } track)
        {
            return;
        }

        surface.Timeline.IsLaneEditable = surface.IsReviewMode && TaggerOn && track.Session.Document is not null;
        UpdateEditSpan();
    }

    private void OnReviewChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ReviewPanelViewModel.ActiveEditor):
                HookSpanEditor(Review?.ActiveEditor);
                UpdateEditSpan();
                break;

            case nameof(ReviewPanelViewModel.IsSuggestedTab):
                FollowSuggestedTab();
                break;
        }
    }

    private void HookSpanEditor(TagEditorViewModel? editor)
    {
        if (_spanEditor is not null)
        {
            _spanEditor.PropertyChanged -= OnSpanEditorChanged;
        }

        _spanEditor = editor;
        if (_spanEditor is not null)
        {
            _spanEditor.PropertyChanged += OnSpanEditorChanged;
        }
    }

    private void OnSpanEditorChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TagEditorViewModel.CurrentSpan))
        {
            UpdateEditSpan();
        }
    }

    private void UpdateEditSpan()
    {
        if (_surface is not { } surface || _context is not { } ctx)
        {
            return;
        }

        if (!surface.IsReviewMode || Review?.ActiveEditor?.CurrentSpan is not { } span)
        {
            surface.Timeline.SetEditSpan(null);
            return;
        }

        int start = ctx.FrameIndexAtTick(span.From);
        int end = ctx.FrameIndexAtTick(span.To);
        surface.Timeline.SetEditSpan(start < 0 ? null : (start, end < 0 ? Math.Max(start, ctx.TotalFrames - 1) : end));
    }

    private void OnEditSpanDragged(int startFrame, int endFrame)
    {
        if (_context is { } ctx && Review?.ActiveEditor is { } editor)
        {
            editor.SetSpan(ModuleTimelineData.TickAtFrame(ctx, startFrame), ModuleTimelineData.TickAtFrame(ctx, endFrame));
        }
    }

    private void OnLaneLabelRequested(int frame)
    {
        if (_surface is not { IsReviewMode: true } || _context is not { } ctx || Review is not { } panel)
        {
            return;
        }

        int tick = ModuleTimelineData.TickAtFrame(ctx, frame);
        int rate = ctx.TickRate > 0 ? ctx.TickRate : 64;
        panel.NewTag(tick, tick + 10 * rate, "New label");
    }

    // A tag band clicked in Label Mode picks its tag for the palette; the seek to the band's start still
    // happens, so the pick is also on screen. A Suggested band picks its proposal for the queue, which is
    // how the mouse starts a review; a second press on a merged band walks to its next member.
    private void OnBandPressed(TimelineBandViewModel band)
    {
        if (_surface?.Timeline.Data is not { } data || _tagTrack is not { } tagTrack || _proposalTrack is not { } proposalTrack)
        {
            return;
        }

        if (band.TrackId == ProposalTrack.TrackId)
        {
            if (SuggestionsOn)
            {
                Queue?.SelectFromTrack(proposalTrack.ProposalsInRun(data, band.StartFrameIndex));
            }

            return;
        }

        if (band.TrackId == TagTrack.TrackId && TaggerOn && Palette is { IsLabelMode: true } palette)
        {
            palette.SelectForLabels(tagTrack.InstancesInRun(data, band.StartFrameIndex));
        }
    }

    // The labels or suggestions in a lane band, each with what can be done to it.
    private IEnumerable<MenuEntry> LaneMenuFor(TimelineBandViewModel band)
    {
        List<MenuEntry> entries = [];
        if (_surface is not { IsReviewMode: true } surface || surface.Timeline.Data is not { } data
            || _tagTrack is not { } tagTrack || _proposalTrack is not { } proposalTrack)
        {
            return entries;
        }

        if (band.TrackId == TagTrack.TrackId && tagTrack.Session.Document is { } document)
        {
            foreach (Guid id in tagTrack.InstancesInRun(data, band.StartFrameIndex))
            {
                if (document.Instances.FirstOrDefault(i => i.Id == id) is not { } instance)
                {
                    continue;
                }

                string name = instance.Round is { } round ? $"{instance.Code} (round {round})" : instance.Code;
                entries.Add(new MenuEntry($"Edit {name}", () => Review?.EditTag(id)));
                entries.Add(new MenuEntry($"Delete {name}", () => DeleteTag(id)));
            }
        }
        else if (band.TrackId == ProposalTrack.TrackId)
        {
            foreach (string id in proposalTrack.ProposalsInRun(data, band.StartFrameIndex))
            {
                string pick = id;
                entries.Add(new MenuEntry($"Review {id}", () =>
                {
                    if (Review is { } panel)
                    {
                        panel.IsSuggestedTab = true;
                    }

                    Queue?.SelectFromTrack([pick]);
                }));
            }
        }

        return entries;
    }

    private void DeleteTag(Guid id)
    {
        try
        {
            _tagTrack?.Session.Apply(new TagDelta.Remove(id));
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            // Already gone: nothing to delete.
        }
    }

    private void SavePaletteSetting(string id)
    {
        try
        {
            _settings?.Write(s => s.Playback2D.TagPaletteId = id);
        }
        catch (Exception)
        {
            // A read-only config directory must not take the palette down over which palette it shows.
        }
    }

    private void SaveBackgroundSetting(bool on)
    {
        try
        {
            _settings?.Write(s => s.Playback2D.SuggestedTagsBackground = on);
        }
        catch (Exception)
        {
            // A read-only config directory keeps the sweep's state for the session.
        }
    }

    // Session changes can arrive off the UI thread after a save; the panels and the timeline are UI-thread affine.
    private void OnUi(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
        }
        else
        {
            _post(action);
        }
    }
}
