#region

using System.Collections;
using System.Globalization;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;

#endregion

namespace DemoViewer.NET.ViewModels;

// ── Span model ────────────────────────────────────────────────────────────────

// ── Row / cell models ─────────────────────────────────────────────────────────

// ── ViewModel ─────────────────────────────────────────────────────────────────

/// <summary>
///     Self-contained ViewModel for <c>BinaryPane</c>.
///     Manages a byte buffer, a virtualized row window, and a hierarchy of <see cref="HexSpan" />
///     highlight ranges.  Each instance is fully independent — create one per hex view.
/// </summary>
public sealed partial class HarvestHexViewModel : ObservableObject
{
    // ── Window constants ──────────────────────────────────────────────────────
    /// <summary>Rows per chunk (1 024 rows × 16 B = 16 KB).</summary>
    public const int ChunkRows = 1024;

    /// <summary>Chunks kept live in the ListBox at once (prev + current + next).</summary>
    public const int VisibleChunks = 3;

    /// <summary>Maximum rows in the ListBox at any time (3 072 = 49 152 bytes).</summary>
    public const int WindowRows = ChunkRows * VisibleChunks;

    // ── Level-to-brush palette ────────────────────────────────────────────────
    // Four built-in tiers, fading from fully saturated (selected) to barely-there (ancestor).
    // Callers never manage brushes — they only specify Level values.
    //
    // v0.6.0 code-color promotion: the values are now the HexSwatchSelected/Parent/Ancestor/
    // AncestorDeep THEME TOKENS, resolved by BinaryPane (which owns the visual-tree access) via
    // SetPalette on attach and on live theme switch. The array stays static because the theme is
    // process-wide; the defaults below are the Dark values, kept as no-Application fallbacks
    // (unit tests, designer). Deliberately resolved ONCE per theme — the per-cell hot path
    // (thousands of cells, LazyRowList materialization) still reads a plain array slot.

    private static IBrush[] _levelBrushes =
    [
        new SolidColorBrush(Color.FromArgb(0xCC, 0x4C, 0x9E, 0xF5)), // L0 sky-blue   (selected)
        new SolidColorBrush(Color.FromArgb(0x88, 0x55, 0xBB, 0x8A)), // L1 sage-green  (parent)
        new SolidColorBrush(Color.FromArgb(0x55, 0xC0, 0x7C, 0x28)), // L2 amber       (grandparent)
        new SolidColorBrush(Color.FromArgb(0x33, 0x90, 0x78, 0x90)) // L3 slate       (ancestor+)
    ];

    // ── State ─────────────────────────────────────────────────────────────────
    private readonly List<HexSpan> _spans = [];
    private byte[]? _data;

    /// <summary>Optional single-line text shown in the footer strip below the hex rows.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFooter))]
    private string? _footer;

    [ObservableProperty]
    private bool _hasData;

    // ── Observable properties ─────────────────────────────────────────────────

    /// <summary>Optional single-line banner shown above the hex rows.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasHeader))]
    private string? _header;

    [ObservableProperty]
    private string _placeholderText = "Load bytes to inspect";

    [ObservableProperty]
    private IList<HarvestHexRow> _rows = [];

    [ObservableProperty]
    private string _statusText = "";

    /// <summary>
    ///     In-window row index that the ListBox should scroll to.
    ///     Watched by <c>BinaryPane</c> code-behind.
    /// </summary>
    [ObservableProperty]
    private int _targetRowIndex;

    private int _totalRows;
    private int _windowChunkStart;

    [ObservableProperty]
    private string _windowRangeText = "";

    /// <summary>Can scroll back.</summary>
    public bool CanScrollBack => _windowChunkStart > 0;

    // ── Window navigation (called by code-behind) ─────────────────────────────

    /// <summary>Can scroll forward.</summary>
    public bool CanScrollForward =>
        _totalRows > 0 && _windowChunkStart + VisibleChunks <= (_totalRows - 1) / ChunkRows;

    /// <summary>Has footer.</summary>
    public bool HasFooter => Footer is { Length: > 0 };

    /// <summary>Has header.</summary>
    public bool HasHeader => Header is { Length: > 0 };

    /// <summary>
    ///     Swaps the process-wide 4-tier highlight palette (L0 selected → L3 deep ancestor).
    ///     Called by <c>BinaryPane</c> with token-resolved brushes; the caller then triggers
    ///     <see cref="RepaintHighlights" /> on its own VM so live rows re-materialize.
    /// </summary>
    public static void SetPalette(IBrush l0, IBrush l1, IBrush l2, IBrush l3) =>
        _levelBrushes = [l0, l1, l2, l3];

    /// <summary>Re-materializes the current window's rows so cells pick up a swapped palette.</summary>
    public void RepaintHighlights() => RebuildRows();

    // ── Reverse byte → node hit-testing (F5.2) ────────────────────────────────

    /// <summary>
    ///     Raised when the user clicks a valid byte cell. The argument is the absolute byte
    ///     offset within this view's buffer. Consumers (the parser tab) map the offset back to
    ///     the encompassing payload tree node, closing the hex → tree selection loop.
    /// </summary>
    public event Action<int>? ByteClicked;

    /// <summary>Clear the buffer and all highlights.</summary>
    public void Clear()
    {
        _data = null;
        _spans.Clear();
        HasData = false;
        StatusText = "";
        _windowChunkStart = 0;
        Rows = [];
        _totalRows = 0;
        WindowRangeText = "";
    }

    /// <summary>Remove all active highlight spans.</summary>
    public void ClearSpans()
    {
        _spans.Clear();
        UpdateStatusFromSpans();
        RebuildRows();
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    ///     Load a byte buffer.  Clears all active spans and scrolls to the top.
    /// </summary>
    public void Load(byte[] data)
    {
        _data = data;
        _spans.Clear();
        HasData = data.Length > 0;
        StatusText = $"{data.Length:N0} bytes";
        _windowChunkStart = 0;
        RebuildRows();
    }

    /// <summary>Invoked by the control's code-behind on a byte-cell click. Fires <see cref="ByteClicked" />.</summary>
    public void RaiseByteClicked(int absoluteOffset)
    {
        if (absoluteOffset >= 0)
        {
            ByteClicked?.Invoke(absoluteOffset);
        }
    }

    /// <summary>Scroll so that <paramref name="byteOffset" /> is visible.</summary>
    public void ScrollToOffset(int byteOffset) =>
        NavigateToAbsoluteRow(byteOffset / 16);

    /// <summary>
    ///     Convenience overload: highlight a single range at Level 0.
    /// </summary>
    public void SetSelection(int start, int length, string? label = null) =>
        SetSpans([new HexSpan(start, length, 0, label)]);

    /// <summary>
    ///     Set the active highlight spans.
    ///     <para>
    ///         Pass Level 0 for the innermost / currently-selected range, Level 1 for its
    ///         direct parent, Level 2 for the grandparent, etc.  Overlapping spans are
    ///         resolved by <b>(Level ASC, Length ASC)</b> — lower Level always wins; among
    ///         equal-Level spans the shorter one wins.
    ///     </para>
    ///     The component automatically navigates to the first Level-0 span.
    /// </summary>
    public void SetSpans(IReadOnlyList<HexSpan> spans)
    {
        _spans.Clear();
        _spans.AddRange(spans);
        UpdateStatusFromSpans();
        RebuildRows();

        // Navigate to the first (shortest) Level-0 span, falling back to Level-1, etc.
        HexSpan? primary = spans
            .OrderBy(s => s.Level)
            .ThenBy(s => s.Length)
            .Cast<HexSpan?>()
            .FirstOrDefault();

        if (primary is { Length: > 0 } p)
        {
            NavigateToAbsoluteRow(p.Start / 16);
        }
    }

    internal void AdvanceWindow()
    {
        if (!CanScrollForward)
        {
            return;
        }

        int maxStart = Math.Max(0, (_totalRows - 1) / ChunkRows - VisibleChunks + 1);
        _windowChunkStart = Math.Min(_windowChunkStart + 1, maxStart);
        RebuildRows();
    }

    internal void RetreatWindow()
    {
        if (!CanScrollBack)
        {
            return;
        }

        _windowChunkStart = Math.Max(0, _windowChunkStart - 1);
        RebuildRows();
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private void NavigateToAbsoluteRow(int absoluteRow)
    {
        if (_totalRows == 0)
        {
            return;
        }

        absoluteRow = Math.Max(0, Math.Min(absoluteRow, _totalRows - 1));

        int targetChunk = absoluteRow / ChunkRows;
        int maxStart = Math.Max(0, (_totalRows - 1) / ChunkRows - VisibleChunks + 1);
        int newStart = Math.Clamp(targetChunk - 1, 0, maxStart);

        if (newStart != _windowChunkStart)
        {
            _windowChunkStart = newStart;
            RebuildRows();
        }

        TargetRowIndex = absoluteRow - _windowChunkStart * ChunkRows;
    }

    private void RebuildRows()
    {
        byte[]? data = _data;
        if (data is not { Length: > 0 })
        {
            Rows = [];
            _totalRows = 0;
            WindowRangeText = "";
            return;
        }

        _totalRows = (data.Length + 15) / 16;

        int maxStart = Math.Max(0, (_totalRows - 1) / ChunkRows - VisibleChunks + 1);
        _windowChunkStart = Math.Min(_windowChunkStart, maxStart);

        int startByte = _windowChunkStart * ChunkRows * 16;
        int windowByteCount = Math.Min(WindowRows * 16, data.Length - startByte);

        Rows = new LazyRowList(data, _spans, startByte, windowByteCount);

        WindowRangeText = _totalRows > WindowRows
            ? $"0x{startByte:X6}–0x{startByte + windowByteCount - 1:X6}  /  0x{data.Length - 1:X6}"
            : "";
    }

    // ── Brush resolution ──────────────────────────────────────────────────────

    /// <summary>
    ///     For a given absolute byte offset, find the highest-priority covering span
    ///     and return its brush, or null if no span covers the byte.
    ///     Priority: lowest Level first; on equal Level, shortest span first.
    /// </summary>
    private static IBrush? ResolveBrush(List<HexSpan> spans, int abs)
    {
        int bestLevel = int.MaxValue;
        int bestLen = int.MaxValue;
        int bestIdx = -1;

        for (int i = 0; i < spans.Count; i++)
        {
            HexSpan s = spans[i];
            if (abs < s.Start || abs >= s.Start + s.Length)
            {
                continue;
            }

            if (s.Level < bestLevel || s.Level == bestLevel && s.Length < bestLen)
            {
                bestLevel = s.Level;
                bestLen = s.Length;
                bestIdx = i;
            }
        }

        if (bestIdx < 0)
        {
            return null;
        }

        return _levelBrushes[Math.Min(bestLevel, _levelBrushes.Length - 1)];
    }

    private static char ToAscii(byte b) => b is >= 32 and < 127 ? (char)b : '.';

    private void UpdateStatusFromSpans()
    {
        if (_data is null || _data.Length == 0)
        {
            StatusText = "";
            return;
        }

        HexSpan? primary = _spans
            .Where(s => s.Level == 0)
            .OrderBy(s => s.Length)
            .Cast<HexSpan?>()
            .FirstOrDefault();

        if (primary is { } p)
        {
            string label = p.Label is { Length: > 0 } l ? $"  ·  {l}" : "";
            StatusText = $"@0x{p.Start:X4} + {p.Length} B{label}";
        }
        else
        {
            StatusText = $"{_data.Length:N0} bytes";
        }
    }

    // ── Lazy row list ─────────────────────────────────────────────────────────

    private sealed class LazyRowList(byte[] data, List<HexSpan> spans, int byteOffset, int byteCount) : IList<HarvestHexRow>
    {
        /// <summary>Add.</summary>
        public void Add(HarvestHexRow item) => throw new NotSupportedException();

        /// <summary>Clear.</summary>
        public void Clear() => throw new NotSupportedException();

        /// <summary>Contains.</summary>
        public bool Contains(HarvestHexRow item) => throw new NotSupportedException();

        /// <summary>Copy to.</summary>
        public void CopyTo(HarvestHexRow[] a, int i) => throw new NotSupportedException();

        /// <summary>Count.</summary>
        public int Count { get; } = (byteCount + 15) / 16;

        /// <summary>Get enumerator.</summary>
        public IEnumerator<HarvestHexRow> GetEnumerator()
        {
            for (int i = 0; i < Count; i++)
            {
                yield return this[i];
            }
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        /// <summary>Index of.</summary>
        public int IndexOf(HarvestHexRow item) => throw new NotSupportedException();

        /// <summary>Insert.</summary>
        public void Insert(int index, HarvestHexRow i) => throw new NotSupportedException();

        /// <summary>Is read only.</summary>
        public bool IsReadOnly => true;

        /// <inheritdoc />
        public HarvestHexRow this[int index]
        {
            get
            {
                int absBase = byteOffset + index * 16;
                int count = Math.Min(16, data.Length - absBase);

                HarvestHexCell[] cells = new HarvestHexCell[16];
                char[] ascii = new char[16];

                for (int i = 0; i < 16; i++)
                {
                    bool valid = i < count;
                    int abs = absBase + i;
                    byte b = valid ? data[abs] : (byte)0;
                    char ac = valid ? ToAscii(b) : ' ';

                    cells[i] = new HarvestHexCell(
                        valid ? b.ToString("X2", CultureInfo.InvariantCulture) : "  ",
                        ac,
                        valid ? ResolveBrush(spans, abs) : null,
                        valid,
                        valid ? abs : -1);

                    ascii[i] = valid ? ac : ' ';
                }

                return new HarvestHexRow(
                    $"{absBase:X6}:",
                    cells[..8],
                    cells[8..],
                    new string(ascii));
            }
            set => throw new NotSupportedException();
        }

        /// <summary>Remove.</summary>
        public bool Remove(HarvestHexRow item) => throw new NotSupportedException();

        /// <summary>Remove at.</summary>
        public void RemoveAt(int index) => throw new NotSupportedException();
    }
}
