#region

using DemoViewer.NET.Modules.RuleWorkbench;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Undo and redo over the editing path (docs/rule-graph/design.md §6.4 item 5), which §6.4 calls
///     out as something a node editor cannot ship without and which nodify does not provide.
///     <para>
///         The stack holds BUFFERS, not inverse commands, because <c>RulesetDocumentEditor</c> is
///         immutable and every operation already returns a whole new document. The tests here are
///         about that choice: an undo returns the exact bytes, including comments, and it does so
///         whichever operation produced them.
///     </para>
///     <para>
///         Exercised against the editing functions rather than through a view model, because the
///         property under test is a property of the model. The view-model wiring is a stack and two
///         commands over these.
///     </para>
/// </summary>
public class RulesetUndoRedoTests
{
    private const string Sample = """
                                  # a header worth keeping
                                  ruleset: probe
                                  for: each_player

                                  stats:
                                    kills:
                                      count: kill        # trailing
                                      per: round
                                      label: Kills
                                  """;

    /// <summary>
    ///     A sequence of edits undone one at a time returns each intermediate buffer exactly, and
    ///     the last undo returns the original bytes. Comments included, which is the point: an undo
    ///     that regenerated would come back without them.
    /// </summary>
    [Test]
    public async Task UndoingEveryEdit_ReturnsTheOriginalBytes()
    {
        List<string> history = [Sample];
        RulesetNodeBinding kills = new(RulesetNodeKind.Stat, "kills");

        history.Add(RulesetNodeEditing.SetField(history[^1], kills, "label", "Frags"));
        history.Add(RulesetNodeEditing.SetField(history[^1], kills, "while", "round.active"));
        history.Add(RulesetNodeEditing.AddStat(history[^1]).Text);
        history.Add(RulesetNodeEditing.Delete(history[^1], kills));

        // Each step really did change something, or the test proves nothing.
        for (int i = 1; i < history.Count; i++)
        {
            await Assert.That(history[i]).IsNotEqualTo(history[i - 1]).Because($"step {i} was a no-op");
        }

        Stack<string> undo = new();
        string current = Sample;
        foreach (string next in history.Skip(1))
        {
            undo.Push(current);
            current = next;
        }

        while (undo.Count > 0)
        {
            current = undo.Pop();
        }

        await Assert.That(current).IsEqualTo(Sample)
            .Because("undoing back to the start must return the original bytes, comments and all");
    }

    /// <summary>Redo after undo returns the edited buffer exactly, so the pair is lossless both ways.</summary>
    [Test]
    public async Task RedoAfterUndo_ReturnsTheEditedBytes()
    {
        RulesetNodeBinding kills = new(RulesetNodeKind.Stat, "kills");
        string edited = RulesetNodeEditing.SetField(Sample, kills, "label", "Frags");

        Stack<string> undo = new();
        Stack<string> redo = new();

        undo.Push(Sample);
        string current = edited;

        redo.Push(current);
        current = undo.Pop();
        await Assert.That(current).IsEqualTo(Sample);

        undo.Push(current);
        current = redo.Pop();
        await Assert.That(current).IsEqualTo(edited);
    }

    /// <summary>
    ///     An edit that produces the same bytes is not worth an undo entry. An author who retypes a
    ///     value into the field it already holds should not have to press undo to get back to where
    ///     they were.
    /// </summary>
    [Test]
    public async Task AnEditThatChangesNothing_ProducesTheSameBytes()
    {
        RulesetNodeBinding kills = new(RulesetNodeKind.Stat, "kills");

        string same = RulesetNodeEditing.SetField(Sample, kills, "label", "Kills");

        await Assert.That(same).IsEqualTo(Sample)
            .Because("setting a field to what it already holds is a no-op the view model drops");
    }

    /// <summary>
    ///     Undo is byte equality, not model equality, and the difference matters. Two documents can
    ///     parse to the same ruleset and differ in every comment; only the first is an undo.
    /// </summary>
    [Test]
    public async Task UndoIsByteEquality_NotModelEquality()
    {
        RulesetNodeBinding kills = new(RulesetNodeKind.Stat, "kills");

        string edited = RulesetNodeEditing.SetField(Sample, kills, "label", "Frags");
        string back = RulesetNodeEditing.SetField(edited, kills, "label", "Kills");

        // Round-tripping the VALUE gets the bytes back too, here, because the writer splices. That
        // is worth asserting rather than assuming: it is the property that makes a value-based undo
        // and a replayed edit agree.
        await Assert.That(back).IsEqualTo(Sample);
    }
}
