namespace DemoViewer.NET.Services.Players;

/// <summary>Turns a Steam vanity name into an account. Nothing in the app implements it: it needs the network.</summary>
public interface IVanityResolver
{
    /// <summary>The account the vanity name belongs to, or null when it has none or cannot be asked.</summary>
    /// <param name="vanity">The name from a <c>/id/</c> URL.</param>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    Task<ulong?> ResolveAsync(string vanity, CancellationToken cancellationToken);
}
