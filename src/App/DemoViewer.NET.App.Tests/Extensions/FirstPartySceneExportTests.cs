#region

using DemoViewer.NET.Configuration;
using DemoViewer.NET.Extensions;
using DemoViewer.NET.Playback2D.Core.Export;
using DemoViewer.NET.Services;
using DemoViewer.NET.Services.Export;
using DemoViewer.NET.ViewModels.Playback2D;

#endregion

namespace DemoViewer.NET.AppTests.Extensions;

/// <summary>
///     The dialog a first-party extension opens over its own scene: its own choices, the 2D export's saved folder
///     and quality, and only the folder written back when an export starts.
/// </summary>
public class FirstPartySceneExportTests
{
    private static readonly SceneExportDefaults Gif = new(ExportFormats.Gif, 20, 640, 640, true, true, true, false);

    [Test]
    public async Task TheDialog_TakesTheExtensionsChoices_AndTheSavedFolder_AndSavesOnlyTheFolderOnStart()
    {
        string root = Path.Combine(Path.GetTempPath(), "dv-scene-export-" + Guid.NewGuid().ToString("N"));
        string saved = Path.Combine(root, "saved");
        string picked = Path.Combine(root, "picked");
        Directory.CreateDirectory(saved);
        Directory.CreateDirectory(picked);
        try
        {
            SettingsService settings = new(root);
            settings.Write(s =>
            {
                s.Playback2D.ExportOutputDirectory = saved;
                s.Playback2D.ExportFps = 60;
                s.Playback2D.ExportFormatId = ExportFormats.WebM;
            });

            using HeavyJobGate gate = new();
            FirstPartyExports exports = new(gate, null!, settings);
            RecordingJob job = new();
            Playback2DExportDialogViewModel dialog = FirstPartySceneExport.NewDialog(exports, Gif,
                [new ExportRangeOption("all", 0, 99)], job, static (start, end, _, _) => end - start + 1, null,
                static () => null, new ExportDialogScene("Export scene", "scene", FirstPartySceneExport.SizePresets, null));

            using (Assert.Multiple())
            {
                await Assert.That(dialog.SelectedFormat).IsEqualTo(ExportFormats.Gif);
                await Assert.That(Path.GetDirectoryName(dialog.OutputPath)).IsEqualTo(saved);
            }

            dialog.OutputPath = Path.Combine(picked, "scene.gif");
            dialog.StartCommand.Execute(null);

            using (Assert.Multiple())
            {
                await Assert.That(job.Started).IsEqualTo(1);
                await Assert.That(settings.Current.Playback2D.ExportOutputDirectory).IsEqualTo(picked);
                await Assert.That(settings.Current.Playback2D.ExportFps).IsEqualTo(60);
                await Assert.That(settings.Current.Playback2D.ExportFormatId).IsEqualTo(ExportFormats.WebM);
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private sealed class RecordingJob : IExportJobService
    {
        public int Started { get; private set; }

        public ExportJobStatus Status => ExportJobStatus.Idle;

        public event EventHandler<ExportJobStatus>? StatusChanged
        {
            add { }
            remove { }
        }

        public void Start(Scene2DExportRequest request) => Started++;

        public Task CancelAsync() => Task.CompletedTask;
    }
}
