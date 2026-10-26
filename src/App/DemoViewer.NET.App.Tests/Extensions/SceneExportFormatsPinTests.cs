#region

using System.Reflection;
using DemoViewer.NET.Extensions.Sdk.Export;
using PipelineFormats = DemoViewer.NET.Playback2D.Pipeline.Export.ExportFormats;

#endregion

namespace DemoViewer.NET.AppTests.Extensions;

/// <summary>
///     The format ids are persisted keys, and the SDK carries a second copy of them because Pipeline cannot
///     reference the SDK. The encoder reads Pipeline's and an extension writes the SDK's into the same saved
///     default, so the two must not drift. Compared as name-to-value sets: an id added, renamed or revalued
///     on either side fails here.
/// </summary>
public class SceneExportFormatsPinTests
{
    [Test]
    public async Task ThePublishedFormatIds_AreExactlyThePipelinesOwn()
    {
        string published = Literals(typeof(SceneExportFormats));

        using (Assert.Multiple())
        {
            await Assert.That(published).IsNotEmpty().Because("reading no literal at all would pin nothing");
            await Assert.That(published).IsEqualTo(Literals(typeof(PipelineFormats)));
        }
    }

    private static string Literals(Type type) => string.Join("\n", type
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(f => f.IsLiteral)
        .Select(f => f.Name + "=" + f.GetRawConstantValue())
        .Order(StringComparer.Ordinal));
}
