#region

using DemoViewer.NET.Services.RoundFacts;
using DemoViewer.NET.Extensions.StratBook.Services.RoundFactsPass;

#endregion

namespace DemoViewer.NET.Extensions.StratBook;

/// <summary>
///     The pack's share of the demo cache record: the Round Facts rows, read and written through
///     <see cref="RoundFactsRecords" />. Everything else the pack keeps per demo is its own per-demo data.
/// </summary>
public static class StratBookCache
{
    /// <summary>The pack's id, the key of its payload on every record. Equal to <c>StratBookPack.Id</c>.</summary>
    public const string PackId = RoundFactsRecords.PackId;

    /// <summary>The Round Facts rows' shape.</summary>
    public const int RoundFactsSchema = RoundFactsRecords.Schema;
}
