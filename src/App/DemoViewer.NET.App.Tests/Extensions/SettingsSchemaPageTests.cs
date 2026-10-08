#region

using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DemoViewer.NET.Extensions;
using DemoViewer.NET.ViewModels.Settings;
using DemoViewer.NET.Views.Settings;
using Microsoft.Extensions.DependencyInjection;

#endregion

namespace DemoViewer.NET.AppTests.Extensions;

/// <summary>
///     A settings page the host renders from an extension's schema: each row reads and writes the extension's
///     own settings, follows a change made elsewhere, and the page renders a control per kind.
/// </summary>
[Category("Render")]
public class SettingsSchemaPageTests
{
    private static readonly SettingsSchema Schema = new("dev.example.page", "EXAMPLE",
    [
        SettingDescriptor.Toggle("walk", "Walk in the background", false, "About two seconds per demo."),
        SettingDescriptor.Number("stride", "Stride", 4, 1, 16),
        SettingDescriptor.Choice("source", "Names from", "pawn", [new SettingChoice("pawn", "Pawn"), new SettingChoice("zones", "Zones")]),
        SettingDescriptor.Text("note", "Note"),
        SettingDescriptor.Folder("out", "Output folder")
    ]);

    [Test]
    public async Task Rows_ShowTheDefaults_WriteThroughOnAChange_AndFollowAChangeMadeElsewhere()
    {
        ExtensionSettingsStore settings = new("dev.example.page", null, a => a());
        using SchemaSettingsPageViewModel page = new(Schema, settings);
        ToggleSettingRow walk = (ToggleSettingRow)page.Rows[0];
        NumberSettingRow stride = (NumberSettingRow)page.Rows[1];
        ChoiceSettingRow source = (ChoiceSettingRow)page.Rows[2];
        FolderSettingRow folder = (FolderSettingRow)page.Rows[4];

        using (Assert.Multiple())
        {
            await Assert.That(walk.Value).IsFalse();
            await Assert.That(stride.Value).IsEqualTo(4m);
            await Assert.That(source.Selected!.Value).IsEqualTo("pawn");
            await Assert.That(folder.HasPath).IsFalse();
        }

        walk.Value = true;
        stride.Value = 40.4m;
        source.Selected = source.Choices[1];
        folder.Path = "/tmp/out";
        settings.Set("stride", 2);

        using (Assert.Multiple())
        {
            await Assert.That(settings.Get("walk", false)).IsTrue();
            await Assert.That(settings.Get("source", "")).IsEqualTo("zones");
            await Assert.That(settings.Get<string?>("out", null)).IsEqualTo("/tmp/out");
            await Assert.That(stride.Value).IsEqualTo(2m).Because("the row follows a write made elsewhere");
        }

        stride.Value = 40.4m;
        await Assert.That(settings.Get("stride", 0)).IsEqualTo(16).Because("a whole step stores the clamped whole number");

        folder.ClearCommand.Execute(null);
        await Assert.That(settings.Get<string?>("out", "none")).IsEqualTo("none").Because("clearing forgets the key");
    }

    [Test]
    public async Task TheContributedSchema_IsASettingsPage_ThatRendersAControlPerKind()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            Pack pack = new("dev.example.page." + Guid.NewGuid().ToString("N")[..8]);
            ServiceCollection services = new();
            services.AddSingleton<ExtensionShellHub>();
            using ServiceProvider sp = services.BuildServiceProvider();
            ExtensionContext context = new(pack, sp);
            PackContributions contributions = new(pack, () => context);
            contributions.SettingsSchema(Schema);

            SettingsPageContribution page = contributions.SettingsPages.Single();
            object viewModel = page.ViewModelFactory();
            Control view = page.ViewFactory();
            view.DataContext = viewModel;
            Window window = new() { Width = 600, Height = 600, Content = view };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();

            try
            {
                using (Assert.Multiple())
                {
                    await Assert.That(page.Header).IsEqualTo("EXAMPLE");
                    await Assert.That(page.FeatureId).IsEqualTo(pack.FeatureId);
                    await Assert.That(page.Keywords).Contains("Walk in the background");
                    await Assert.That(viewModel).IsTypeOf<SchemaSettingsPageViewModel>();
                    await Assert.That(view).IsTypeOf<SchemaSettingsPageView>();
                    await Assert.That(view.GetVisualDescendants().OfType<ToggleSwitch>().Count()).IsEqualTo(1);
                    await Assert.That(view.GetVisualDescendants().OfType<NumericUpDown>().Count()).IsEqualTo(1);
                    await Assert.That(view.GetVisualDescendants().OfType<ComboBox>().Count()).IsEqualTo(1);
                }

                view.GetVisualDescendants().OfType<ToggleSwitch>().Single().IsChecked = true;
                await Assert.That(context.Settings.Get("walk", false)).IsTrue();
            }
            finally
            {
                window.Close();
                ((IDisposable)viewModel).Dispose();
            }
        });
    }

    private sealed class Pack(string id) : IExtension
    {
        public string Id => id;
        public string FeatureId => "pack." + id;
        public IEnumerable<ExtensionFeature> Features => [];

        public void Register(IServiceCollection services)
        {
        }

        public void Contribute(IExtensionContributions contributions, IServiceProvider services)
        {
        }
    }
}
