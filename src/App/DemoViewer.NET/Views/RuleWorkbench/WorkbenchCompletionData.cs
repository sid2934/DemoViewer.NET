#region

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using AvaloniaEdit.CodeCompletion;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using DemoViewer.NET.Modules.RuleWorkbench;
using DemoViewer.NET.Theming;

#endregion

namespace DemoViewer.NET.Views.RuleWorkbench;

/// <summary>
///     Adapts a UI-free <see cref="WorkbenchCompletion" /> to AvaloniaEdit's
///     <see cref="ICompletionData" /> for the editor's completion window. The row shows the term
///     with a dim, colour-coded <em>type</em> badge on the right so the kind of each
///     suggestion — event, facet, context, kind, … — reads at a glance, matching the editor's role colours.
/// </summary>
public sealed class WorkbenchCompletionData : ICompletionData
{
    private readonly WorkbenchCompletion _completion;

    public WorkbenchCompletionData(WorkbenchCompletion completion) => _completion = completion;

    public IImage? Image => null;
    public string Text => _completion.Text;

    /// <summary>The row: the term on the left, a dim colour-coded category badge on the right.</summary>
    public object Content => BuildRow();

    public object Description => $"{_completion.Category} — {_completion.Detail}";
    public double Priority => _completion.Priority;

    public void Complete(TextArea textArea, ISegment completionSegment, EventArgs insertionRequestEventArgs) =>
        textArea.Document.Replace(completionSegment, Text);

    private Grid BuildRow()
    {
        Grid grid = new()
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            MinWidth = 220
        };

        TextBlock term = new()
        {
            Text = _completion.Text,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(term, 0);

        TextBlock badge = new()
        {
            Text = _completion.Category,
            FontSize = 10,
            Margin = new Thickness(12, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(CategoryColour(_completion.Category)),
            Opacity = 0.85
        };
        Grid.SetColumn(badge, 1);

        grid.Children.Add(term);
        grid.Children.Add(badge);
        return grid;
    }

    /// <summary>
    ///     The badge colour per category — resolved from the SAME <c>Syntax*</c> token namespace the
    ///     editor's highlighter uses (v0.6.0 code-color promotion; the old values were byte-copies of
    ///     the tokens' Dark values, so a light editor got dark-tuned badges). Resolved per popup open
    ///     — the popup is transient, so no live-switch hook is needed. Fallbacks = the VS "Dark+"
    ///     design-time values, mirroring WorkbenchYamlHighlighting's role table.
    /// </summary>
    private static Color CategoryColour(string category)
    {
        (string token, string fallback) = category switch
        {
            "section" => ("SyntaxSection", "#C586C0"),
            "show" => ("SyntaxSection", "#C586C0"),
            "view" => ("SyntaxEvent", "#DCDCAA"),
            "facet" => ("SyntaxFacet", "#9CDCFE"),
            "context" => ("SyntaxPath", "#9CDCFE"),
            "entity" => ("SyntaxPath", "#9CDCFE"),
            "kind" => ("SyntaxKind", "#4FC1FF"),
            "modifier" => ("SyntaxModifier", "#569CD6"),
            "literal" => ("SyntaxLiteral", "#4EC9B0"),
            "stat" => ("SyntaxIdentifier", "#D7BA7D"),
            _ => ("SyntaxPlain", "#9A9A9A") // function / other
        };

        return ThemeColors.Get(token, Application.Current?.ActualThemeVariant, fallback);
    }
}
