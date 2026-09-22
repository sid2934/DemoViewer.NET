#region

using System.Globalization;
using Avalonia.Data.Converters;

#endregion

namespace DemoViewer.NET.Views.RuleWorkbench;

/// <summary>
///     Splits a node's display value into the wire events the canvas draws as header chips.
///     <para>
///         <b>A chip per event, never a wire.</b> design.md §6.7 measures the alternative: promoting
///         <c>on:</c> and the built-in gates to connectors is what turns a gate every stat in a file
///         shares into a 21-way hub, and deletes 137 of the corpus's 240 connections' worth of
///         meaning while adding nothing. The events reach here space-joined because a canvas node is
///         a <c>RulesetGraphNode</c>, which carries one display-value slot and is shared with the
///         MSAGL projection.
///     </para>
/// </summary>
public sealed class EventChipsConverter : IValueConverter
{
    /// <summary>The one instance, referenced from XAML as <c>{x:Static}</c>.</summary>
    public static EventChipsConverter Instance { get; } = new();

    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string text
            ? text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : Array.Empty<string>();

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException("The canvas does not write an event list back through the chips.");
}
