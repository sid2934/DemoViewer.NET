namespace DemoViewer.NET.ViewModels;

/// <summary>One row in the Analysis Engine chain-summary panel.</summary>
/// <remarks>Initializes a new <see cref="AnalysisChainSummaryViewModel" /> instance.</remarks>
public sealed class AnalysisChainSummaryViewModel(string chainName, int count)
{
    /// <summary>Chain name.</summary>
    public string ChainName { get; } = chainName;

    /// <summary>Count.</summary>
    public int Count { get; } = count;
}
