#region

using System.Diagnostics.CodeAnalysis;

#endregion

namespace DemoViewer.NET.Playback2D.Core.Annotations;

/// <summary>
///     One invertible mutation of an <see cref="AnnotationDocument" />. Deliberately minimal and
///     serializable: the INVERSE is computed at apply time by the document (a <see cref="Remove" />'s
///     inverse needs the element that was removed, which the delta itself does not carry), so a delta
///     never has to describe both directions.
/// </summary>
public abstract record DocDelta
{
    /// <summary>Inserts an element at an index. An index past the end appends.</summary>
    /// <param name="Element">The element to insert.</param>
    /// <param name="Index">Insertion position; clamped into range on apply.</param>
    [SuppressMessage("Design", "CA1034:Nested types should not be visible",
        Justification = "Closed discriminated union; the nesting is the contract (design §5.4).")]
    public sealed record Add(AnnotationElement Element, int Index) : DocDelta;

    /// <summary>Removes the element with this id. A no-op when it is absent.</summary>
    /// <param name="Id">The element's identity.</param>
    [SuppressMessage("Design", "CA1034:Nested types should not be visible",
        Justification = "Closed discriminated union; the nesting is the contract (design §5.4).")]
    public sealed record Remove(Guid Id) : DocDelta;

    /// <summary>Swaps the element with this id for a new value, keeping its position.</summary>
    /// <param name="Id">The element being replaced.</param>
    /// <param name="Element">Its replacement. Its own <c>Id</c> is what the document keys on afterwards.</param>
    [SuppressMessage("Design", "CA1034:Nested types should not be visible",
        Justification = "Closed discriminated union; the nesting is the contract (design §5.4).")]
    public sealed record Replace(Guid Id, AnnotationElement Element) : DocDelta;

    /// <summary>Applies several deltas as one unit. Inverted in reverse order.</summary>
    /// <param name="Items">The deltas, in application order.</param>
    [SuppressMessage("Design", "CA1034:Nested types should not be visible",
        Justification = "Closed discriminated union; the nesting is the contract (design §5.4).")]
    public sealed record Batch(IReadOnlyList<DocDelta> Items) : DocDelta;
}
