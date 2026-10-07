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

    /// <summary>
    ///     The configured outputs a read of a demo records, by its path: the tables of the stamped rulesets whose
    ///     stored outputs are stale for it. Null records <c>round_facts</c> alone.
    /// </summary>
    public Func<string, IReadOnlySet<string>?>? OutputsFor { get; init; }

    /// <summary>How a demo is read, and where the content hash taken from that read goes. Null hashes nothing.</summary>
    public DemoFileRead? FileRead { get; init; }

    private static ILogger Log => _diagLog ??= DiagnosticsLog.CreateLogger("App.Highlights");

    /// <summary>One forward pass over <paramref name="path" />.</summary>
    public ForwardDemoResult Run(string path, ForwardNeeds needs, Action<double> progress, CancellationToken cancellationToken)
    {
        ParseOptions options = ForwardDemoPass.ReaderOptions(cancellationToken);

        // Same rule as the retained parse: only a settled local file is mapped.
        byte[]? bytes = (FileRead ?? new DemoFileRead(_time, null, null)).Prepare(path, cancellationToken);
        using DemoReader reader = bytes is null ? DemoReader.OpenFile(path, options) : DemoReader.Open(bytes, options);
        ForwardDemoResult pass = ForwardDemoPass.Run(reader, needs,
            (needs & ForwardNeeds.Rules) != 0 ? _rules.Docs : null, progress,
            (needs & ForwardNeeds.Rules) != 0 ? OutputsFor?.Invoke(path) : null, cancellationToken);
        if (pass.Run is { } run)
        {
            RulesetExclusionReport.Report(Log, run.Build);
        }

        return pass;
    }
}
