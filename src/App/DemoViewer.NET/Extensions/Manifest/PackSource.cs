namespace DemoViewer.NET.Extensions.Manifest;

/// <summary>
///     Where a configured pack's assembly came from: the copy the installer shipped beside the app, or a
///     copy staged under <c>&lt;config root&gt;/extensions/&lt;id&gt;/&lt;version&gt;/</c> that the loader
///     chose over it. Settings shows it beside the version.
/// </summary>
public abstract record PackSource
{
    private PackSource()
    {
    }

    /// <summary>The copy compiled into this build and shipped with it.</summary>
    public static Shipped Bundled { get; } = new();

    /// <summary>True for <see cref="Staged" />.</summary>
    public bool IsStaged => this is Staged;

    /// <summary>The source in user terms: "bundled" or "installed update".</summary>
    public abstract string Label { get; }

    /// <summary>The copy shipped with the installer.</summary>
    public sealed record Shipped : PackSource
    {
        internal Shipped()
        {
        }

        /// <inheritdoc />
        public override string Label => "bundled";
    }

    /// <summary>A copy loaded from <paramref name="Directory" /> under the config root.</summary>
    /// <param name="Directory">The staged version directory the assembly was loaded from.</param>
    public sealed record Staged(string Directory) : PackSource
    {
        /// <inheritdoc />
        public override string Label => "installed update";
    }
}
