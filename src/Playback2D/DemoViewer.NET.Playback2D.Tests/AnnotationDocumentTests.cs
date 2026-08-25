#region

using System.Reflection;
using DemoViewer.NET.Playback2D.Core.Annotations;

#endregion

namespace DemoViewer.NET.Playback2DTests;

/// <summary>
///     The document, its delta stack and the gesture-squashing rule that makes one user action cost one
///     Ctrl+Z. Also the structural half of design risk 13: the history holds annotations and nothing else.
/// </summary>
public class AnnotationDocumentTests
{
    [Test]
    public async Task Apply_Add_BumpsVersion_RaisesChanged()
    {
        AnnotationDocument doc = new();
        int raised = 0;
        doc.Changed += () => raised++;
        int before = doc.Version;

        doc.Apply(new DocDelta.Add(AnnotationFakes.Stroke(), 0));

        await Assert.That(doc.Elements.Count).IsEqualTo(1);
        await Assert.That(doc.Version).IsGreaterThan(before);
        await Assert.That(raised).IsEqualTo(1);
        await Assert.That(doc.UndoDepth).IsEqualTo(1);
    }

    /// <summary>
    ///     Ctrl+Z is reachable from the keyboard while the pointer is captured mid-stroke. Undoing there
    ///     used to pop the PREVIOUS entry, and then the stroke's own <see cref="AnnotationDocument.Apply" />
    ///     cleared the redo stack — so the earlier stroke was gone with no way back. The open gesture is
    ///     the user's current intent; history editing waits for it to finish.
    /// </summary>
    [Test]
    public async Task Undo_DuringAnOpenGesture_IsRefused_AndLosesNothing()
    {
        AnnotationDocument doc = new();
        AnnotationElement first = AnnotationFakes.Stroke();
        doc.Apply(new DocDelta.Add(first, 0));

        using (doc.BeginGesture("draw"))
        {
            await Assert.That(doc.Undo()).IsFalse();
            await Assert.That(doc.Redo()).IsFalse();
            doc.Apply(new DocDelta.Add(AnnotationFakes.Stroke(x: 500), 1));
        }

        await Assert.That(doc.Elements.Count).IsEqualTo(2);
        await Assert.That(doc.UndoDepth).IsEqualTo(2);

        await Assert.That(doc.Undo()).IsTrue();
        await Assert.That(doc.Undo()).IsTrue();
        await Assert.That(doc.Elements).IsEmpty();

        await Assert.That(doc.Redo()).IsTrue();
        await Assert.That(doc.Elements[0]).IsEqualTo(first)
            .Because("nothing the open gesture did may destroy an earlier stroke's history");
    }

    [Test]
    public async Task Gesture_ManyDeltas_SquashToOneUndoEntry()
    {
        AnnotationDocument doc = new();

        using (doc.BeginGesture("erase"))
        {
            for (int i = 0; i < 12; i++)
            {
                doc.Apply(new DocDelta.Add(AnnotationFakes.Stroke(), i));
            }
        }

        await Assert.That(doc.Elements.Count).IsEqualTo(12);
        await Assert.That(doc.UndoDepth).IsEqualTo(1);

        doc.Undo();
        await Assert.That(doc.Elements).IsEmpty();
        await Assert.That(doc.RedoDepth).IsEqualTo(1);
    }

    /// <summary>
    ///     Closing a gesture is the moment its deltas become an undo entry — until then they sit in the
    ///     open batch and <c>UndoDepth</c> still reads zero. Without a notification there, every consumer
    ///     tracking undo depth (the toolbar's undo button first among them) stays stale until some
    ///     unrelated mutation happens to wake it. Found by the headless exit-criterion suite.
    /// </summary>
    [Test]
    public async Task Gesture_Close_AnnouncesTheNewUndoEntry()
    {
        AnnotationDocument doc = new();

        int depthWhenLastNotified = -1;
        doc.Changed += () => depthWhenLastNotified = doc.UndoDepth;

        using (doc.BeginGesture("draw"))
        {
            doc.Apply(new DocDelta.Add(AnnotationFakes.Stroke(), 0));
            await Assert.That(depthWhenLastNotified).IsEqualTo(0)
                .Because("mid-gesture the delta is not an undo entry yet");
        }

        await Assert.That(depthWhenLastNotified).IsEqualTo(1);
    }

    /// <summary>
    ///     ...and the close notification must NOT bump <c>Version</c>: nothing about the CONTENT changed,
    ///     and the ink layer re-records every level's dry picture on a version change.
    /// </summary>
    [Test]
    public async Task Gesture_Close_DoesNotBumpVersion()
    {
        AnnotationDocument doc = new();

        int versionInsideGesture;
        using (doc.BeginGesture("draw"))
        {
            doc.Apply(new DocDelta.Add(AnnotationFakes.Stroke(), 0));
            versionInsideGesture = doc.Version;
        }

        await Assert.That(doc.Version).IsEqualTo(versionInsideGesture);
    }

    [Test]
    public async Task Gesture_ZeroDeltas_PushesNoUndoEntry()
    {
        AnnotationDocument doc = new();

        using (doc.BeginGesture("erase"))
        {
            // A drag-erase that touched nothing: every Remove targets an absent id.
            doc.Apply(new DocDelta.Remove(Guid.NewGuid()));
            doc.Apply(new DocDelta.Remove(Guid.NewGuid()));
        }

        await Assert.That(doc.UndoDepth).IsEqualTo(0);
        await Assert.That(doc.Version).IsEqualTo(0);
    }

    [Test]
    public async Task BailToMark_RollsBack_NoUndoEntry()
    {
        AnnotationDocument doc = new();
        AnnotationElement kept = AnnotationFakes.Stroke();
        doc.Apply(new DocDelta.Add(kept, 0));
        int undoAfterCommit = doc.UndoDepth;

        using (doc.BeginGesture("draw"))
        {
            doc.Apply(new DocDelta.Add(AnnotationFakes.Stroke(), 1));
            doc.Apply(new DocDelta.Remove(kept.Id));
            await Assert.That(doc.Elements.Count).IsEqualTo(1);

            await Assert.That(doc.BailToMark()).IsTrue();
        }

        await Assert.That(doc.Elements.Count).IsEqualTo(1);
        await Assert.That(doc.Elements[0].Id).IsEqualTo(kept.Id);
        await Assert.That(doc.UndoDepth).IsEqualTo(undoAfterCommit);
        await Assert.That(doc.IsGestureOpen).IsFalse();
    }

    [Test]
    public async Task Undo_Redo_RoundTripsElements()
    {
        AnnotationDocument doc = new();
        AnnotationElement a = AnnotationFakes.Stroke();
        AnnotationElement b = AnnotationFakes.Stroke();
        doc.Apply(new DocDelta.Add(a, 0));
        doc.Apply(new DocDelta.Add(b, 1));

        doc.Undo();
        await Assert.That(doc.Elements.Count).IsEqualTo(1);
        doc.Undo();
        await Assert.That(doc.Elements).IsEmpty();

        doc.Redo();
        doc.Redo();
        await Assert.That(doc.Elements.Count).IsEqualTo(2);
        await Assert.That(doc.Elements[0]).IsEqualTo(a);
        await Assert.That(doc.Elements[1]).IsEqualTo(b);
        await Assert.That(doc.Undo()).IsTrue();
        await Assert.That(doc.Elements[0]).IsEqualTo(a);
    }

    [Test]
    public async Task Remove_Undo_RestoresAtTheSamePosition()
    {
        AnnotationDocument doc = new();
        AnnotationElement a = AnnotationFakes.Stroke();
        AnnotationElement b = AnnotationFakes.Stroke();
        AnnotationElement c = AnnotationFakes.Stroke();
        doc.Apply(new DocDelta.Add(a, 0));
        doc.Apply(new DocDelta.Add(b, 1));
        doc.Apply(new DocDelta.Add(c, 2));

        doc.Apply(new DocDelta.Remove(b.Id));
        await Assert.That(doc.Elements.Count).IsEqualTo(2);

        doc.Undo();
        await Assert.That(doc.Elements[1]).IsEqualTo(b).Because("z-order is list order; a restored " +
            "stroke that jumps to the top would repaint over strokes it used to sit under");
    }

    [Test]
    public async Task Apply_AfterUndo_ClearsRedo()
    {
        AnnotationDocument doc = new();
        doc.Apply(new DocDelta.Add(AnnotationFakes.Stroke(), 0));
        doc.Undo();
        await Assert.That(doc.RedoDepth).IsEqualTo(1);

        doc.Apply(new DocDelta.Add(AnnotationFakes.Stroke(), 0));
        await Assert.That(doc.RedoDepth).IsEqualTo(0);
    }

    [Test]
    public async Task NestedGesture_Throws()
    {
        AnnotationDocument doc = new();
        using IDisposable outer = doc.BeginGesture("draw");

        InvalidOperationException? thrown = null;
        try
        {
            doc.BeginGesture("erase").Dispose();
        }
        catch (InvalidOperationException e)
        {
            thrown = e;
        }

        await Assert.That(thrown).IsNotNull()
            .Because("the router guarantees one active tool, so a nested gesture is a bug — and a " +
                     "silently nested one merges two intents into a single undo entry");
    }

    [Test]
    public async Task History_BoundedTo200Entries()
    {
        AnnotationDocument doc = new();
        for (int i = 0; i < AnnotationDocument.MaxHistoryEntries + 50; i++)
        {
            doc.Apply(new DocDelta.Add(AnnotationFakes.Stroke(), i));
        }

        await Assert.That(doc.UndoDepth).IsEqualTo(AnnotationDocument.MaxHistoryEntries);
        await Assert.That(doc.Elements.Count).IsEqualTo(AnnotationDocument.MaxHistoryEntries + 50);
    }

    /// <summary>
    ///     Plan decision D6. A level rebuild is a SYSTEM event: it rewrites live anchors and every anchor
    ///     captured in the history, without consuming an undo slot — otherwise a later Ctrl+Z would
    ///     restore an anchor pointing at a level that no longer exists.
    /// </summary>
    [Test]
    public async Task RemapWorldLevels_RewritesLiveAndHistory_NoUndoEntry()
    {
        AnnotationDocument doc = new();
        AnnotationElement lower = AnnotationFakes.Stroke(space: new SpaceRef.World(-448));
        AnnotationElement upper = AnnotationFakes.Stroke(space: new SpaceRef.World(-128));
        doc.Apply(new DocDelta.Add(lower, 0));
        doc.Apply(new DocDelta.Add(upper, 1));
        doc.Apply(new DocDelta.Remove(upper.Id));

        int undoBefore = doc.UndoDepth;
        int redoBefore = doc.RedoDepth;

        doc.RemapWorldLevels(new Dictionary<double, double>
        {
            [-448] = -384,
            [-128] = -64
        });

        await Assert.That(((SpaceRef.World)doc.Elements[0].Space).LevelMinZ).IsEqualTo(-384);
        await Assert.That(doc.UndoDepth).IsEqualTo(undoBefore);
        await Assert.That(doc.RedoDepth).IsEqualTo(redoBefore);

        // The removed element comes back on the NEW level, not the vanished one.
        doc.Undo();
        AnnotationElement restored = doc.Elements[1];
        await Assert.That(((SpaceRef.World)restored.Space).LevelMinZ).IsEqualTo(-64);
    }

    /// <summary>Plan correction 9: B3's non-undoable mutation entry point.</summary>
    [Test]
    public async Task ApplyMigration_MutatesWithoutTouchingEitherStack()
    {
        AnnotationDocument doc = new();
        AnnotationElement element = AnnotationFakes.Stroke();
        doc.Apply(new DocDelta.Add(element, 0));
        doc.Undo();
        int undoBefore = doc.UndoDepth;
        int redoBefore = doc.RedoDepth;
        int raised = 0;
        doc.Changed += () => raised++;

        doc.ApplyMigration(new DocDelta.Add(element with
        {
            Time = new TimeEnvelope(10, 20, 0, 0)
        }, 0));

        await Assert.That(doc.Elements.Count).IsEqualTo(1);
        await Assert.That(doc.UndoDepth).IsEqualTo(undoBefore);
        await Assert.That(doc.RedoDepth).IsEqualTo(redoBefore);
        await Assert.That(raised).IsEqualTo(1);
    }

    [Test]
    public async Task Reset_ClearsHistory_AndRaisesOnce()
    {
        AnnotationDocument doc = new();
        doc.Apply(new DocDelta.Add(AnnotationFakes.Stroke(), 0));
        doc.Undo();

        int raised = 0;
        doc.Changed += () => raised++;
        doc.Reset([AnnotationFakes.Stroke(), AnnotationFakes.Stroke()]);

        await Assert.That(doc.Elements.Count).IsEqualTo(2);
        await Assert.That(doc.UndoDepth).IsEqualTo(0);
        await Assert.That(doc.RedoDepth).IsEqualTo(0);
        await Assert.That(raised).IsEqualTo(1);
    }

    [Test]
    public async Task Replace_Undo_RestoresThePreviousValue()
    {
        AnnotationDocument doc = new();
        AnnotationElement original = AnnotationFakes.Stroke();
        doc.Apply(new DocDelta.Add(original, 0));

        AnnotationElement edited = original with
        {
            Time = new TimeEnvelope(500, 800, 4, 8)
        };
        doc.Apply(new DocDelta.Replace(original.Id, edited));
        await Assert.That(doc.Elements[0].Time.FromTick).IsEqualTo(500);

        doc.Undo();
        await Assert.That(doc.Elements[0].Time.FromTick).IsNull();
        await Assert.That(doc.Elements[0]).IsEqualTo(original);
    }

    /// <summary>
    ///     Design risk 13 ("history lives only in <c>AnnotationDocument</c>"), asserted structurally: no
    ///     member of the document's public surface names a camera, a playhead or a selection type, so
    ///     undo after a seek can only ever undo the stroke.
    /// </summary>
    [Test]
    public async Task Undo_DoesNotTouchCameraOrPlayback()
    {
        string[] forbidden =
        [
            "Camera", "Viewport", "Transform", "Playback", "Playhead", "Seek", "Selection", "Pane"
        ];

        foreach (MemberInfo member in typeof(AnnotationDocument).GetMembers(
                     BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
        {
            foreach (string word in forbidden)
            {
                await Assert.That(member.Name.Contains(word, StringComparison.Ordinal)).IsFalse()
                    .Because($"AnnotationDocument.{member.Name} names '{word}'; the undo history must " +
                             "hold annotations and nothing else (design risk 13)");
            }
        }
    }
}
