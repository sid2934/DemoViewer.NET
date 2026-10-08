#region

using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.DemoProcessing;

#endregion

namespace DemoViewer.NET.Extensions;

/// <summary>
///     An extension's record pass as one of the host's. The pass sees the SDK's rows, never the cache's types;
///     a throw from <see cref="IExtensionRecordPass.Wants" /> or <see cref="IExtensionRecordPass.Run" />
///     reaches the runner, which skips the pass for that demo and reports it against the extension.
/// </summary>
internal sealed class ExtensionRecordPassHost(IExtensionRecordPass inner, ExtensionGuard guard, HostLibrary library)
    : IRecordPass
{
    /// <summary>The extension's pass.</summary>
    public IExtensionRecordPass Inner { get; } = inner ?? throw new ArgumentNullException(nameof(inner));

    /// <summary>The extension's guard, which faults are reported to.</summary>
    public ExtensionGuard Guard { get; } = guard ?? throw new ArgumentNullException(nameof(guard));

    public string Id { get; } = inner.Id;

    public string Owner => Guard.Scope.Id;

    public bool Wants(DemoCacheIndexEntry entry) => Inner.Wants(library.Project(entry));

    public void Run(DemoCacheRecord record, CancellationToken cancellationToken) =>
        Inner.Run(library.DetailOf(record), cancellationToken);

    /// <summary>
    ///     A factory handing out one host per pass instance, so the runner's per-row memory follows the pass;
    ///     a factory that builds a new pass gets a new host.
    /// </summary>
    public static Func<IRecordPass> Cached(RecordPassContribution contribution, Func<HostLibrary> library)
    {
        ArgumentNullException.ThrowIfNull(contribution);
        ArgumentNullException.ThrowIfNull(library);
        object gate = new();
        ExtensionRecordPassHost? cached = null;
        return () =>
        {
            IExtensionRecordPass pass = contribution.Factory();
            if (!string.Equals(pass.Id, contribution.Id, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Record pass registered as '{contribution.Id}' built an instance whose Id is '{pass.Id}'.");
            }

            lock (gate)
            {
                if (cached is null || !ReferenceEquals(cached.Inner, pass))
                {
                    cached = new ExtensionRecordPassHost(pass, contribution.Guard, library());
                }

                return cached;
            }
        };
    }
}
