#region

using CS2DemoKit.Analysis.Diagnostics;
using CS2DemoKit.Parser;
using DemoViewer.NET.Modules.Highlights;
using DemoViewer.NET.ViewModels.Diagnostics;
using Microsoft.Extensions.Logging;

#endregion

namespace DemoViewer.NET.Services.DemoProcessing;

/// <summary>The queue's forward read: opens the file the way the retained parse would, and runs the merged rules.</summary>
public sealed class ForwardPassRunner
{
    private static ILogger? _diagLog;

    private readonly MergedRulesBuild _rules;
    private readonly TimeProvider _time;

    /// <param name="rules">The build highlights and round facts are stamped from.</param>
    /// <param name="timeProvider">The clock for the settled-file check.</param>
    public ForwardPassRunner(MergedRulesBuild rules, TimeProvider? timeProvider = null)
    {
        _rules = rules;
        _time = timeProvider ?? TimeProvider.System;
    }

    private static ILogger Log => _diagLog ??= DiagnosticsLog.CreateLogger("App.Highlights");

    /// <summary>One forward pass over <paramref name="path" />.</summary>
    public ForwardDemoResult Run(string path, ForwardNeeds needs, Action<double> progress, CancellationToken cancellationToken)
    {
        ParseOptions options = ForwardDemoPass.ReaderOptions(cancellationToken);

        // Same rule as the retained parse: a file that may still be written is read into memory, never mapped.
        using DemoReader reader = MappedParsePolicy.IsSettled(path, _time, MappedParsePolicy.StatFile)
            ? DemoReader.OpenFile(path, options)
            : DemoReader.Open(File.ReadAllBytes(path), options);
        ForwardDemoResult pass = ForwardDemoPass.Run(reader, needs,
            (needs & ForwardNeeds.Rules) != 0 ? _rules.Docs : null, progress, cancellationToken);
        if (pass.Run is { } run)
        {
            RulesetExclusionReport.Report(Log, run.Build);
        }

        return pass;
    }
}
