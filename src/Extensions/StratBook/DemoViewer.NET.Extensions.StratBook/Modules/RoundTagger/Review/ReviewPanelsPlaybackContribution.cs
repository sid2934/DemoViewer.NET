#region

using System.ComponentModel;
using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using DemoViewer.NET.Extensions;
using DemoViewer.NET.Extensions.StratBook;
using DemoViewer.NET.Modules.Abstractions;
using DemoViewer.NET.Modules.Playback2D;
using DemoViewer.NET.Modules.Playback2D.Timeline;
using DemoViewer.NET.Extensions.StratBook.Modules.RoundTagger.Palette;
using DemoViewer.NET.Extensions.StratBook.Modules.RoundTagger.Timeline;
using DemoViewer.NET.Extensions.StratBook.Modules.SuggestedTags;
using DemoViewer.NET.Playback2D.Core.Timeline;
using DemoViewer.NET.Playback2D.Pipeline.Annotations;
using DemoViewer.NET.Services.Facts;
using DemoViewer.NET.Extensions.Sdk;
using DemoViewer.NET.Extensions.StratBook.Services.Tags;
using DemoViewer.NET.Theming;
using DemoViewer.NET.Extensions.StratBook.Views.RoundTagger;
using DemoViewer.NET.Extensions.StratBook.Views.SuggestedTags;
using IPanelHandle = DemoViewer.NET.Extensions.Sdk.Playback.IPanelHandle;
using ModeToggle = DemoViewer.NET.Extensions.Sdk.Playback.ModeToggle;
using DemoViewer.NET.Modules;

#endregion

namespace DemoViewer.NET.Extensions.StratBook.Modules.RoundTagger.Review;

/// <summary>
///     Review mode in 2D Playback as one playback contribution: the mode toggle, the tag
///     and suggestion lanes with their behaviour, and the right-column panels (the Tag Palette, the review
///     panel with the Suggested / Labels toggle and the shared editor, and the Suggestion Queue), each under
///     its own gate. The contribution owns the open demo's <see cref="TagSession" /> (one per tab: the store's
///     CheckOut is single-writer), built from the context's services on attach and attached to the demo on
///     every demo change. It also carries the palette's and the queue's keys ahead of the keymap, the pack's
///     keymap actions and Click To Tag Position. Nothing here exists while the pack is off.
/// </summary>
/// <param name="post">Marshals change notifications onto the UI thread; synchronous when omitted (tests).</param>
/// <param name="identity">Resolves a demo's identity for the session's attach; <see cref="TagSession.IdentityForAsync" /> when omitted (tests).</param>
/// <param name="library">The library a tag's round bounds come from; none when omitted (tests).</param>
public sealed class ReviewPanelsPlaybackContribution(Action<Action>? post = null,
    Func<string, string?, Task<DemoIdentity?>>? identity = null, IExtensionLibrary? library = null) : IPlaybackContribution, IDisposable
{
    /// <summary>The Review mode toggle's id.</summary>
    public const string ReviewModeId = "stratbook.review";

    /// <summary>The palette's order in the column.</summary>
    public const int PaletteOrder = 0;

    /// <summary>The review panel's order in the column.</summary>
    public const int ReviewOrder = 1;

    /// <summary>The queue's order in the column.</summary>
    public const int QueueOrder = 2;

    private readonly Func<string, string?, Task<DemoIdentity?>> _identity = identity ?? TagSession.IdentityForAsync;
    private readonly Action<Action> _post = post ?? (action => action());
    private readonly List<IDisposable> _registrations = [];
    private string? _attachingPath;
    private IModuleContext? _context;
    private ILaneHandle? _proposalLane;
    private ProposalTrack? _proposalTrack;
    private SuggestionQueueView? _queueView;
    private TagSession? _session;
    private StratBookSettings? _settings;
    private TagEditorViewModel? _spanEditor;
    private IPlaybackSurface? _surface;
    private ILaneHandle? _tagLane;
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

    /// <summary>Review mode while attached: the toggle the toolbar shows and Shift+R flips. For tests.</summary>
    public ModeToggle? ReviewMode { get; private set; }

    /// <summary>The open demo's tag session while attached.</summary>
    public TagSession? Session => _session;

    /// <summary>The palette has the keyboard: it is on screen and focused.</summary>
    public bool IsPaletteFocused => PalettePanel is { IsShown: true } && Palette is { IsFocused: true };

    private bool IsReviewOn => ReviewMode is { IsOn: true };

    private bool TaggerOn => _context?.Features?.IsEnabled(RoundTaggerModule.PaletteFeatureId) ?? true;

    private bool SuggestionsOn => _context?.Features?.IsEnabled(SuggestedTagsService.FeatureId) ?? true;

    /// <inheritdoc />
    public void Attach(IPlaybackSurface surface, IModuleContext context)
    {
        ArgumentNullException.ThrowIfNull(surface);
        ArgumentNullException.ThrowIfNull(context);
        Detach();
        _surface = surface;
        _context = context;
        _settings = context.GetService<StratBookSettings>();

        // No store means session-only tags, the annotation rule. The track re-queries on every session
        // version bump, posted to the UI thread because a save can raise Changed off it. Round Facts gives
        // a new tag its round's facts as it is made.
        TagSession session = new(context.GetService<TagStore>(),
            async path => library is null ? null : (await library.GetDetailAsync(path).ConfigureAwait(true))?.Rounds,
            context.GetService<IRoundFactsSource>());
        _session = session;
        _tagTrack = new TagTrack(session, _post);

        // The Suggested track sits on the tag lane after the tags, so an accepted proposal moves from one
        // band to the other in place. Its three confidence steps take theme tokens, never literals.
        _proposalTrack = new ProposalTrack { StepColour = ProposalStepColour };

        // Review mode starts as the user left it (off on a first run). The lanes and the panels follow it.
        ModeToggle mode = new(ReviewModeId, "Review", "Review mode (Shift+R): label rounds and review the suggested labels",
            nameof(Playback2DAction.ToggleReviewMode)) { IsOn = _settings?.ReviewMode ?? false };
        ReviewMode = mode;
        _registrations.Add(surface.AddModeToggle(mode));
        _tagLane = surface.AddLane(_tagTrack, TimelineBandRow.Lane, new TagLaneBehaviour(this));
        _proposalLane = surface.AddLane(_proposalTrack, TimelineBandRow.Lane, new ProposalLaneBehaviour(this));

        // The palette edits through the session. The playhead it tags at is the shared clock's tick; its
        // button colours become the track's.
        TagPaletteViewModel palette = new(session, context.GetService<TagPaletteStore>(), Playhead, TickRate, _post);
        palette.SelectPalette(_settings?.TagPaletteId);
        palette.PaletteChosen += SavePaletteSetting;
        palette.PropertyChanged += OnPaletteChanged;
        palette.ApplyKeymap(surface.Keymap);
        _tagTrack.CodeColour = code => palette.Palette.ColourOf(code);
        Palette = palette;

        Queue = new SuggestionQueueViewModel(context.GetService<SuggestedTagsService>(), _proposalTrack, Seek, TickRate, _post,
            () => _settings?.SuggestedTagsBackground ?? false, SaveBackgroundSetting);

        PalettePanel = surface.AddPanel(PaletteOrder, () => palette,
            () => new TagPaletteView { Margin = new Thickness(0, 0, 0, 2) }, RoundTaggerModule.PaletteFeatureId, mode);
        ReviewPanel = surface.AddPanel(ReviewOrder, BuildReview, () => new ReviewPanelView(), mode: mode);
        QueuePanel = surface.AddPanel(QueueOrder, () => Queue, BuildQueueView, SuggestedTagsService.FeatureId, mode);
        ReviewPanel.Closed += OnReviewClosed;
        PalettePanel.ShownChanged += RefreshLaneEditing;
        PalettePanel.Open();
        QueuePanel.Open();
        RefreshReviewPanel();

        _registrations.Add(surface.AddKeyHandler(OnKey));
        _registrations.Add(surface.AddActionHandler(OnAction));
        _registrations.Add(surface.AddPointerPreHandler(OnPointerPreHandler));
        _registrations.Add(surface.OnDemoChanged(AttachTagsToCurrentDemo));
        _registrations.Add(surface.OnPlayheadChanged(OnPlayheadChanged));
        surface.KeymapChanged += OnKeymapChanged;
        surface.Deactivated += OnDeactivated;
        session.Changed += OnSessionChanged;
        session.Detaching += LeavePalette;
        if (context.Features is { } features)
        {
            features.Changed += OnFeaturesChanged;
        }

        // Subscribed after the panels, which subscribed on AddPanel: they have re-read the mode when this runs.
        mode.Changed += OnReviewModeChanged;
        ApplyReviewMode();
        AttachQueue();
        RefreshLaneEditing();
        AttachTagsToCurrentDemo();
    }

    /// <inheritdoc />
    public void Detach()
    {
        if (_surface is not { } surface)
        {
            return;
        }

        surface.KeymapChanged -= OnKeymapChanged;
        surface.Deactivated -= OnDeactivated;
        if (_context?.Features is { } features)
        {
            features.Changed -= OnFeaturesChanged;
        }

        if (ReviewMode is { } mode)
        {
            mode.Changed -= OnReviewModeChanged;
        }

        if (_session is { } session)
        {
            session.Changed -= OnSessionChanged;
            session.Detaching -= LeavePalette;
        }

        foreach (IDisposable registration in _registrations)
        {
            registration.Dispose();
        }

        _registrations.Clear();
        HookSpanEditor(null);

        // The pending tag is written before its palette goes; the panels' Dispose then disposes the view
        // models; the session goes last, with its document still there for the write to land in.
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

        _tagLane?.Dispose();
        _proposalLane?.Dispose();
        _tagTrack?.Dispose();
        _session?.Dispose(); // detaches, which flushes

        Review = null;
        Palette = null;
        Queue = null;
        PalettePanel = null;
        ReviewPanel = null;
        QueuePanel = null;
        ReviewMode = null;
        _tagLane = null;
        _proposalLane = null;
        _tagTrack = null;
        _proposalTrack = null;
        _session = null;
        _attachingPath = null;
        _settings = null;
        _surface = null;
        _context = null;
    }

    /// <summary><see cref="Detach" />: the session, the track and the panels are the attachment's.</summary>
    public void Dispose() => Detach();

    private int Playhead() => _context?.CurrentTick ?? 0;

    private int TickRate() => _context?.TickRate ?? 64;

    private void Seek(int tick) => _context?.RequestSeekToTick(tick);

    // Binds the session to whatever demo the context is on. Fire-and-forget like the annotations: the hash
    // may have to be computed off the UI thread (a demo Content Identity has not reached), and an activation
    // must not wait on it. The demo already attached keeps the in-memory document; a swap raises Detaching
    // and flushes the old one inside AttachAsync. The attach and the first activation's demo-change signal
    // both land here, so a path already being attached is not attached twice: the session's DemoPath moves
    // only after AttachAsync's first await.
    private void AttachTagsToCurrentDemo()
    {
        if (_context is not { } ctx || _session is not { } session || string.IsNullOrEmpty(ctx.DemoPath)
            || string.Equals(session.DemoPath, ctx.DemoPath, StringComparison.OrdinalIgnoreCase)
            || string.Equals(_attachingPath, ctx.DemoPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _attachingPath = ctx.DemoPath;
        _ = AttachTagsAsync(ctx, session, ctx.DemoPath).ContinueWith(static _ => { }, TaskScheduler.Default);
    }

    private async Task AttachTagsAsync(IModuleContext ctx, TagSession session, string demoPath)
    {
        try
        {
            // Resumes on the calling (UI) context: the session is UI-thread affine.
            DemoIdentity? demo = await _identity(demoPath, ctx.DemoSha256);
            if (demo is null || !ReferenceEquals(_session, session)
                             || !string.Equals(ctx.DemoPath, demoPath, StringComparison.OrdinalIgnoreCase))
            {
                return; // unreadable, detached, or the user moved on while it hashed
            }

            await session.AttachAsync(demo, FrameClock.IdentityFor(ctx), demoPath);
        }
        finally
        {
            if (string.Equals(_attachingPath, demoPath, StringComparison.OrdinalIgnoreCase))
            {
                _attachingPath = null;
            }
        }
    }

    // Label Mode's tag is the one under the playhead unless one is picked.
    private void OnPlayheadChanged(int tick) => Palette?.RefreshLabelTarget();

    private object BuildReview()
    {
        ReviewPanelViewModel panel = new(_session!, Queue!, Palette!, Playhead, TickRate, Seek)
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

    // The review panel hosts the editor both lists share, so it exists while either gate is on; the mode
    // is offered on the same terms.
    private void RefreshReviewPanel()
    {
        if (ReviewMode is { } mode)
        {
            mode.IsAvailable = TaggerOn || SuggestionsOn;
        }

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
        if (Queue is { } queue && _session is { } session)
        {
            queue.Attach(SuggestionsOn ? session.DemoPath : null, session.Document?.Demo.Sha256);
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

    // The keyboard is the tab's again when the document goes (a swap or a detach): the pending tag is
    // written to the document still current, the note is dropped, focus is off.
    private void LeavePalette() => Palette?.Leave();

    // The tab deactivated: the same, then the flush the tab used to do for the session it owned.
    private void OnDeactivated()
    {
        LeavePalette();
        _session?.Flush();
    }

    private void OnKeymapChanged()
    {
        if (_surface is { } surface)
        {
            Palette?.ApplyKeymap(surface.Keymap);
        }
    }

    // The tag and suggestion lanes are Review mode's: hidden by mode, never by the user's own toggle.
    private void ApplyReviewMode()
    {
        bool on = IsReviewOn;
        if (_tagLane is { } tagLane)
        {
            tagLane.IsSuppressed = !on;
        }

        if (_proposalLane is { } proposalLane)
        {
            proposalLane.IsSuppressed = !on;
        }
    }

    private void OnReviewModeChanged()
    {
        ApplyReviewMode();
        SaveReviewModeSetting(IsReviewOn);
        if (!IsReviewOn)
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
    // history per document kind, resolved by focus. ToggleReviewMode is the mode
    // toggle's, through the surface.
    private bool OnAction(Playback2DAction action)
    {
        bool focused = IsPaletteFocused;
        switch (action)
        {
            case Playback2DAction.Undo when focused:
                Palette!.Finish();
                _session!.Undo();
                return true;

            case Playback2DAction.Redo when focused:
                _session!.Redo();
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
    private bool OnPointerPreHandler(ScenePointer pointer)
    {
        if (Palette is not { } palette)
        {
            return false;
        }

        bool toEditor = IsReviewOn && Review is { HasEditor: true };
        if (!toEditor && (!IsPaletteFocused || palette.IsEditingNote))
        {
            return false;
        }

        TagPosition position = TagPositionResolver.Resolve(pointer.WorldX, pointer.WorldY, pointer.Level, Playhead(),
            pointer.Zones(), pointer.Frame.Markers);
        return toEditor ? Review!.AddPosition(position) : palette.AttachPosition(position);
    }

    // Lane editing: editable in Review mode with the palette's gate on and a demo attached; the open
    // editor's span drawn with handles.
    private void RefreshLaneEditing()
    {
        if (_tagLane is { } lane && _session is { } session)
        {
            lane.IsEditable = IsReviewOn && TaggerOn && session.Document is not null;
        }

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
        if (_tagLane is not { } lane || _context is not { } ctx)
        {
            return;
        }

        if (!IsReviewOn || Review?.ActiveEditor?.CurrentSpan is not { } span)
        {
            lane.EditSpan = null;
            return;
        }

        int start = ctx.FrameIndexAtTick(span.From);
        int end = ctx.FrameIndexAtTick(span.To);
        lane.EditSpan = start < 0 ? null : (start, end < 0 ? Math.Max(start, ctx.TotalFrames - 1) : end);
    }

    private void OnEditSpanDragged(int startFrame, int endFrame)
    {
        if (_context is { } ctx && Review?.ActiveEditor is { } editor)
        {
            editor.SetSpan(ModuleTimelineData.TickAtFrame(ctx, startFrame), ModuleTimelineData.TickAtFrame(ctx, endFrame));
        }
    }

    private void OnLabelRequested(int frame)
    {
        if (!IsReviewOn || _context is not { } ctx || Review is not { } panel)
        {
            return;
        }

        int tick = ModuleTimelineData.TickAtFrame(ctx, frame);
        int rate = ctx.TickRate > 0 ? ctx.TickRate : 64;
        panel.NewTag(tick, tick + 10 * rate, "New label");
    }

    // A tag band clicked in Label Mode picks its tag for the palette; the seek to the band's start still
    // happens, so the pick is also on screen. A second press on a merged band walks to its next member.
    private void OnTagBandPressed(TimelineBandViewModel band, ITimelineData data)
    {
        if (_tagTrack is { } track && TaggerOn && Palette is { IsLabelMode: true } palette)
        {
            palette.SelectForLabels(track.InstancesInRun(data, band.StartFrameIndex));
        }
    }

    // A Suggested band picks its proposal for the queue, which is how the mouse starts a review.
    private void OnProposalBandPressed(TimelineBandViewModel band, ITimelineData data)
    {
        if (_proposalTrack is { } track && SuggestionsOn)
        {
            Queue?.SelectFromTrack(track.ProposalsInRun(data, band.StartFrameIndex));
        }
    }

    // The labels in a tag band, each with what can be done to it.
    private List<MenuEntry> TagMenuFor(TimelineBandViewModel band, ITimelineData data)
    {
        List<MenuEntry> entries = [];
        if (!IsReviewOn || _tagTrack is not { } track || _session?.Document is not { } document)
        {
            return entries;
        }

        foreach (Guid id in track.InstancesInRun(data, band.StartFrameIndex))
        {
            if (document.Instances.FirstOrDefault(i => i.Id == id) is not { } instance)
            {
                continue;
            }

            string name = instance.Round is { } round ? $"{instance.Code} (round {round})" : instance.Code;
            entries.Add(new MenuEntry($"Edit {name}", () => Review?.EditTag(id)));
            entries.Add(new MenuEntry($"Delete {name}", () => DeleteTag(id)));
        }

        return entries;
    }

    // The suggestions in a Suggested band, each opening in the queue.
    private List<MenuEntry> ProposalMenuFor(TimelineBandViewModel band, ITimelineData data)
    {
        List<MenuEntry> entries = [];
        if (!IsReviewOn || _proposalTrack is not { } track)
        {
            return entries;
        }

        foreach (string id in track.ProposalsInRun(data, band.StartFrameIndex))
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

        return entries;
    }

    private void DeleteTag(Guid id)
    {
        try
        {
            _session?.Apply(new TagDelta.Remove(id));
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            // Already gone: nothing to delete.
        }
    }

    // The settings store logs a failed write and keeps the value for the session.
    private void SavePaletteSetting(string id)
    {
        if (_settings is { } settings)
        {
            settings.TagPaletteId = id;
        }
    }

    private void SaveBackgroundSetting(bool on)
    {
        if (_settings is { } settings)
        {
            settings.SuggestedTagsBackground = on;
        }
    }

    private void SaveReviewModeSetting(bool on)
    {
        if (_settings is { } settings)
        {
            settings.ReviewMode = on;
        }
    }

    // The Suggested track's steps as theme tokens at the lane's wash alpha: the track hands back ARGB and
    // never names a colour. Off the UI thread (a test's layout) the track's own washes stand.
    private static uint? ProposalStepColour(ConfidenceStep step)
    {
        if (Application.Current is not { } app || !Dispatcher.UIThread.CheckAccess())
        {
            return null;
        }

        (string key, uint fallback, byte alpha) = step switch
        {
            ConfidenceStep.High => ("Pb2dPositive", 0xFF5AB05Au, (byte)0x80),
            ConfidenceStep.Medium => ("Pb2dBomb", 0xFFE08040u, (byte)0x60),
            _ => ("Pb2dTextDim", 0xFFA0A8B0u, (byte)0x40)
        };
        Color colour = ThemeColors.Get(key, app.ActualThemeVariant, Color.FromUInt32(fallback));
        return Color.FromArgb(alpha, colour.R, colour.G, colour.B).ToUInt32();
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

    private sealed class TagLaneBehaviour(ReviewPanelsPlaybackContribution owner) : ILaneBehaviour
    {
        public void OnBandPressed(TimelineBandViewModel band, ITimelineData data) => owner.OnTagBandPressed(band, data);

        public IEnumerable<MenuEntry> MenuFor(TimelineBandViewModel band, ITimelineData data) => owner.TagMenuFor(band, data);

        public void OnLabelRequested(int frame) => owner.OnLabelRequested(frame);

        public void OnEditSpanDragged(int startFrame, int endFrame) => owner.OnEditSpanDragged(startFrame, endFrame);
    }

    private sealed class ProposalLaneBehaviour(ReviewPanelsPlaybackContribution owner) : ILaneBehaviour
    {
        public void OnBandPressed(TimelineBandViewModel band, ITimelineData data) => owner.OnProposalBandPressed(band, data);

        public IEnumerable<MenuEntry> MenuFor(TimelineBandViewModel band, ITimelineData data) => owner.ProposalMenuFor(band, data);

        public void OnLabelRequested(int frame)
        {
        }

        public void OnEditSpanDragged(int startFrame, int endFrame)
        {
        }
    }
}
