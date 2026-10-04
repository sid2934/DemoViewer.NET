#region

using CS2DemoKit.Parser;
using DemoViewer.NET.Services.DemoProcessing;

#endregion

namespace DemoViewer.NET.Extensions;

/// <summary>An SDK evaluator on the app's demo fan-out.</summary>
internal sealed class ExtensionEvaluatorAdapter(IExtensionEvaluator inner) : IDemoEvaluator
{
    public IExtensionEvaluator Inner { get; } = inner ?? throw new ArgumentNullException(nameof(inner));

    public string Id => Inner.Id;

    public bool ReadsUserCommands => Inner.ReadsUserCommands;

    public bool Wants(string path) => Inner.Wants(path);

    public void Evaluate(string path, ParsedDemo parsed) => Inner.Evaluate(path, parsed);

    public void OnFailed(string path) => Inner.OnFailed(path);

    public DemoJobPriority PriorityFor(string path) =>
        Inner.PriorityFor(path) == JobPriority.UserRequested ? DemoJobPriority.UserRequested : DemoJobPriority.Background;

    public long OrderHint(string path) => Inner.OrderHint(path);

    public IReadOnlyList<string> PendingPaths() => Inner.PendingPaths();
}
