#region

using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DemoViewer.NET.Services.Strats;

#endregion

namespace DemoViewer.NET.ViewModels.StratBook;

/// <summary>
///     Role View And LAN Print (plan.md §3; strat-model.md §3.14, decision 8): one slot's parts on screen,
///     and a button that writes every slot's sheet as one self-contained HTML page and opens it in the
///     system browser. The derivation and the writer are Strat Model's own (§9 step 6,
///     <see cref="RoleSheet.Derive" /> and <see cref="RoleSheetHtmlWriter.Html" />); this view model only
///     picks a slot to show and wires the button, so "a strat prints to one sheet per slot" (the item's own
///     done line) needs no new model.
///     <para>
///         <b>The picker is not the print scope.</b> <see cref="Print" /> always writes all five slots'
///         sheets, whichever one <see cref="SelectedSlot" /> is showing on screen; §3.14 says "one page per
///         slot", not "the page for the slot in view".
///     </para>
///     <para>
///         <b>The print action is injected.</b> Defaults to <see cref="LanPrint.WriteAndOpen" />; a test
///         swaps in a delegate that never touches disk or launches a browser, the way the tab's export job
///         gives a test <c>ExportJobFactory</c>. <see cref="IsBrowser" /> is likewise given by the caller,
///         not read from the runtime here, the Highlights precedent the tab itself follows.
///     </para>
/// </summary>
public sealed partial class StratRoleViewPanelViewModel : ViewModelBase
{
    private readonly Func<string, string, string?> _print;

    private CalloutResolver? _callouts;
    private StratDocument? _document;
    private Func<Guid, StratDocument?>? _lookup;
    private IReadOnlyDictionary<string, string?>? _roster;

    [ObservableProperty]
    private RoleSheetHeader? _header;

    [ObservableProperty]
    private string _metaLine = "";

    [ObservableProperty]
    private string _selectedSlot = StratVocabulary.Slots[0];

    [ObservableProperty]
    private string _slotLine = "";

    [ObservableProperty]
    private string _statusFooter = "";

    [ObservableProperty]
    private string _statusLine = "";

    [ObservableProperty]
    private string _triggerLine = "";

    /// <param name="isBrowser">Whether the host is the WASM head; Print has nothing to write to there.</param>
    /// <param name="print">Writes the HTML and opens it, returning the path opened or null on failure.</param>
    public StratRoleViewPanelViewModel(bool isBrowser = false, Func<string, string, string?>? print = null)
    {
        IsBrowser = isBrowser;
        _print = print ?? LanPrint.WriteAndOpen;
    }

    /// <summary>The slots the picker offers, always the five in order.</summary>
    public static IReadOnlyList<string> Slots { get; } = StratVocabulary.Slots;

    public bool IsBrowser { get; }

    public bool HasStrat => _document is not null;

    /// <summary>This slot's own and context lines, in step order.</summary>
    public ObservableCollection<RoleViewLineRow> Lines { get; } = [];

    /// <summary>This slot's own branches, as "if … → …".</summary>
    public ObservableCollection<string> Branches { get; } = [];

    public bool HasBranches => Branches.Count > 0;

    public bool HasTriggerLine => TriggerLine.Length > 0;

    /// <summary>How many of the slot's positions Step Authoring has written; the mini-map's point count.</summary>
    public int PositionCount { get; private set; }

    public bool HasPositions => PositionCount > 0;

    /// <summary>True when Print has something to open: a strat is open and the host can write and launch a file.</summary>
    public bool CanPrint => HasStrat && !IsBrowser;

    /// <summary>What the Print slot says when the button cannot exist, the Export button's own wording.</summary>
    public string PrintUnavailableNote => IsBrowser ? "Print role sheets: unavailable in the browser" : "";

    public bool HasPrintUnavailableNote => PrintUnavailableNote.Length > 0;

    /// <summary>
    ///     Points the panel at the open strat and what resolves its places and names (called from the tab's
    ///     <c>OnSessionChanged</c>, so a field or slot-pin edit rebuilds the sheet the same frame). Null
    ///     clears the panel.
    /// </summary>
    /// <param name="document">The open strat, or null.</param>
    /// <param name="callouts">The owner's callouts for the strat's map; null prints canonical names.</param>
    /// <param name="roster">Slot letter to resolved player name, strat-pin level only (§3.5); null for none.</param>
    /// <param name="lookup">Resolves a branch's target strat when it is not this one.</param>
    public void Configure(StratDocument? document, CalloutResolver? callouts,
        IReadOnlyDictionary<string, string?>? roster, Func<Guid, StratDocument?>? lookup)
    {
        _document = document;
        _callouts = callouts;
        _roster = roster;
        _lookup = lookup;
        OnPropertyChanged(nameof(HasStrat));
        PrintCommand.NotifyCanExecuteChanged();
        Rebuild();
    }

    partial void OnSelectedSlotChanged(string value) => Rebuild();

    [RelayCommand(CanExecute = nameof(CanPrint))]
    private void Print()
    {
        if (_document is not { } document)
        {
            return;
        }

        List<RoleSheet> sheets =
        [
            .. StratVocabulary.Slots.Select(slot => RoleSheet.Derive(document, slot, _callouts, _roster, _lookup))
        ];
        string html = RoleSheetHtmlWriter.Html(document, sheets);
        string? path = _print(html, StratBookTabViewModel.ExportFileStem(document.Name) + "-roles");
        StatusLine = path is null ? "could not open the role sheets" : "opened " + Path.GetFileName(path);
    }

    private void Rebuild()
    {
        Lines.Clear();
        Branches.Clear();

        if (_document is not { } document || !StratVocabulary.Slots.Contains(SelectedSlot))
        {
            Header = null;
            SlotLine = "";
            MetaLine = "";
            TriggerLine = "";
            StatusFooter = "";
            PositionCount = 0;
            RaiseComputed();
            return;
        }

        RoleSheet sheet = RoleSheet.Derive(document, SelectedSlot, _callouts, _roster, _lookup);
        ApplyHeader(sheet.Header);
        foreach (RoleSheetLine line in sheet.Lines)
        {
            Lines.Add(new RoleViewLineRow(line));
        }

        foreach (RoleSheetBranchLine branch in sheet.Branches)
        {
            Branches.Add(branch.Text);
        }

        PositionCount = sheet.Positions.Count;
        RaiseComputed();
    }

    // Worded the same way RoleSheetHtmlWriter's masthead is, so the on-screen preview and the printed
    // page never disagree about what a slot's sheet says.
    private void ApplyHeader(RoleSheetHeader header)
    {
        Header = header;
        SlotLine = "Slot " + header.Slot
                   + (header.SlotName is { Length: > 0 } name ? ": " + name : "")
                   + (header.Role is { Length: > 0 } role ? " (" + role + ")" : "");

        List<string> meta =
        [
            header.Map,
            header.Side + " " + header.Type + (header.TargetSite is { Length: > 0 } site ? $" (site {site})" : "")
        ];
        if (header.Economy is { Length: > 0 } economy)
        {
            meta.Add(economy + " economy");
        }

        if (header.Tempo is { Length: > 0 } tempo)
        {
            meta.Add(tempo + " tempo");
        }

        MetaLine = string.Join(" · ", meta);
        TriggerLine = header.TriggerText is { Length: > 0 } trigger ? "Trigger: " + trigger : "";
        StatusFooter = "Status: " + header.Status + " · revision " + header.Revision.ToString(CultureInfo.InvariantCulture);
    }

    private void RaiseComputed()
    {
        OnPropertyChanged(nameof(HasBranches));
        OnPropertyChanged(nameof(HasTriggerLine));
        OnPropertyChanged(nameof(PositionCount));
        OnPropertyChanged(nameof(HasPositions));
    }
}

/// <summary>One printed line on screen: the round-clock time, the phrased text, and whether it is context.</summary>
public sealed class RoleViewLineRow(RoleSheetLine line)
{
    public string AtText { get; } = StratClock.Format(line.AtSeconds);

    public string Text { get; } = line.Text;

    public bool IsContext { get; } = line.IsContext;
}
