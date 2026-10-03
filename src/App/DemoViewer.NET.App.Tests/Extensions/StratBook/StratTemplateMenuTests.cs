#region

using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DemoViewer.NET.Services.Strats;
using DemoViewer.NET.ViewModels.StratBook;
using DemoViewer.NET.Views.StratBook;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>New Strat's menu offers Blank and every template; Apply Template shows only while the strat is bare.</summary>
[NotInParallel]
[Category("Integration")]
public class StratTemplateMenuTests
{
    [Test]
    public async Task TheMenus_OfferEveryTemplate_AndApplyHidesOnceTheStratHasSteps() =>
        await HeadlessSession.RunOnUi(async () =>
        {
            using StratBookTabViewModel vm = new(new StratStore(null), null, null, false);
            vm.Session.AutoSaveDelay = TimeSpan.FromHours(1);
            vm.Session.IdleCommitDelay = TimeSpan.FromHours(1);
            vm.SelectedMap = "de_mirage";
            vm.NewStratCommand.Execute(null);

            Window window = new() { Width = 1280, Height = 800, Content = new StratBookTabView { DataContext = vm } };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            SplitButton newStrat = window.GetVisualDescendants().OfType<SplitButton>().Single(b => b.Name == "NewStratButton");
            DropDownButton apply = window.GetVisualDescendants().OfType<DropDownButton>().Single(b => b.Name == "ApplyTemplateButton");
            MenuItem[] items = [.. ((MenuFlyout)newStrat.Flyout!).Items.OfType<MenuItem>()];
            string[] ids =
            [
                .. items.SelectMany(i => i.Items.Count > 0 ? i.Items.OfType<MenuItem>() : [i])
                    .Select(i => i.CommandParameter as string).OfType<string>()
            ];

            using (Assert.Multiple())
            {
                await Assert.That(items[0].Header).IsEqualTo("Blank");
                await Assert.That(ids).IsEquivalentTo(StratTemplates.Templates.Select(t => t.Id));
                await Assert.That(apply.IsEffectivelyVisible).IsTrue().Because("a blank strat can take a template");
            }

            await Assert.That(items.Single(i => Equals(i.Header, "Default (T)")).Command).IsSameReferenceAs(vm.NewStratCommand);
            vm.ApplyTemplateCommand.Execute("default");
            Dispatcher.UIThread.RunJobs();

            await Assert.That(apply.IsEffectivelyVisible).IsFalse().Because("the strat has steps now");
            window.Close();
        });
}
