#region

using Avalonia.Media;
using CS2DemoKit.Parser;
using CS2DemoKit.Parser.GameEvents;

#endregion

namespace DemoViewer.NET.ViewModels;

/// <summary>
///     One message row in the Analysis Engine frame-message widget, including the full decoded
///     field list so the user can see exactly what data accompanied each potential graph trigger.
/// </summary>
public sealed class AnalysisMessageViewModel
{
    /// <summary>Initializes a new <see cref="AnalysisMessageViewModel" /> instance.</summary>
    public AnalysisMessageViewModel(NetMessage message, IReadOnlySet<Type> relevantTypes)
    {
        if (message is GameEventMessage gem)
        {
            Label = gem.DecodedEvent.Name;
            IsMatched = relevantTypes.Contains(gem.DecodedEvent.GetType());
            IsEvent = true;
            Fields = gem.DecodedEvent.GetDecodedFields()
                .Select(f => new AnalysisMessageFieldViewModel(f.Name, f.Value, f.WireType))
                .ToList();
        }
        else
        {
            Label = message.MessageTypeName;
            IsMatched = relevantTypes.Contains(message.GetType());
            IsEvent = false;
            Fields = [];
        }

        LabelForeground = IsMatched
            ? new SolidColorBrush(Color.Parse("#A0C8A0"))
            : new SolidColorBrush(Color.Parse("#404060"));

        IndicatorFill = IsMatched
            ? new SolidColorBrush(Color.Parse("#2E7D32"))
            : new SolidColorBrush(Color.Parse("#252545"));
    }

    /// <summary>
    ///     Decoded field list from <see cref="GameEvent.GetDecodedFields()" />.
    ///     Empty for non-event messages.
    /// </summary>
    public IReadOnlyList<AnalysisMessageFieldViewModel> Fields { get; }

    /// <summary>Has fields.</summary>
    public bool HasFields => Fields.Count > 0;

    /// <summary>Pre-computed indicator dot colour.</summary>
    public IBrush IndicatorFill { get; }

    /// <summary>True for <see cref="GameEventMessage" /> rows.</summary>
    public bool IsEvent { get; }

    /// <summary>
    ///     True when this message's dispatch key appears in the graph's edge table — i.e. it
    ///     could trigger a state transition.
    /// </summary>
    public bool IsMatched { get; }

    /// <summary>
    ///     Display label: the CS2 event name for game-event messages, otherwise the proto
    ///     type name (e.g. <c>"svc_PacketEntities"</c>).
    /// </summary>
    public string Label { get; }

    /// <summary>Pre-computed label colour — green for matched, dim for unmatched.</summary>
    public IBrush LabelForeground { get; }
}
