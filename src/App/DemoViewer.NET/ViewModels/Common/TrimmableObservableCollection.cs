#region

using System.Collections.ObjectModel;

#endregion

namespace DemoViewer.NET.ViewModels.Common;

/// <summary>
///     An <see cref="ObservableCollection{T}" /> that can hand its backing array's capacity back.
///     <para>
///         <c>Clear()</c> nulls the elements — which is what releases the items — but it does NOT shrink
///         the backing array, and <see cref="Collection{T}.Items" /> is protected, so a caller cannot trim
///         it from outside. That is invisible until a collection has grown large: a demo with ~131k frames
///         leaves <c>Frames</c>, <c>FrameRows</c> and <c>TickGroups</c> each holding a 131,072-slot array
///         of nulls — 1 MB apiece, still there long after the demo is closed. A heap dump names them as
///         surviving <c>DemoFrame[]</c> / <c>TickGroup[]</c> / <c>HarvestFrameRowViewModel[]</c>, which
///         reads alarmingly like a retained demo when it is really just spare capacity.
///     </para>
///     <para>
///         Deriving keeps the public surface an <see cref="ObservableCollection{T}" />, so every existing
///         XAML binding and consumer is unaffected.
///     </para>
/// </summary>
public sealed class TrimmableObservableCollection<T> : ObservableCollection<T>
{
    /// <summary>
    ///     Clears the collection AND releases the backing array's spare capacity. Use on demo unload;
    ///     ordinary <c>Clear()</c> remains correct everywhere the collection is about to be refilled
    ///     (trimming before a refill would just force the array to grow again).
    /// </summary>
    public void ClearAndTrim()
    {
        Clear();
        if (Items is List<T> list)
        {
            list.Capacity = 0;
        }
    }
}
