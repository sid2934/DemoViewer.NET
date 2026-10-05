namespace DemoViewer.NET.Extensions.StratBook.Modules.UtilityBook;

/// <summary>
///     Renders a demo's missing lineup clips on the visit that walked its grenades, from that visit's parse.
///     Runs after the grenade pass: a demo is wanted on the visit only once its walk has landed in the index.
/// </summary>
public sealed class LineupClipPass : IExtensionPass, IDisposable
{
    /// <summary>The pass id.</summary>
    public const string PassId = "lineupclips";

    private readonly LineupClipService _clips;
    private readonly GrenadeIndexEvaluator _grenades;
    private readonly HashSet<string> _walked = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="grenades">The grenade pass this one follows.</param>
    /// <param name="clips">Plans and renders the clips.</param>
    public LineupClipPass(GrenadeIndexEvaluator grenades, LineupClipService clips)
    {
        ArgumentNullException.ThrowIfNull(grenades);
        ArgumentNullException.ThrowIfNull(clips);
        _grenades = grenades;
        _clips = clips;
        _grenades.Walked += OnWalked;
    }

    /// <inheritdoc />
    public string Id => PassId;

    /// <inheritdoc />
    public bool ReadsUserCommands => false;

    /// <inheritdoc />
    public DemoInterest Interest(string demoPath)
    {
        if (!_clips.Renders)
        {
            return DemoInterest.No;
        }

        lock (_walked)
        {
            if (_walked.Contains(demoPath))
            {
                return DemoInterest.Yes;
            }
        }

        return _grenades.Interest(demoPath) == DemoInterest.No ? DemoInterest.No : DemoInterest.AfterUpstream;
    }

    /// <inheritdoc />
    public void Run(IPassContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        lock (_walked)
        {
            _walked.Remove(context.DemoPath);
        }

        _clips.RenderOn(context.DemoPath, context.Parsed, context.CancellationToken);
    }

    /// <inheritdoc />
    public void OnFailed(string demoPath)
    {
        lock (_walked)
        {
            _walked.Remove(demoPath);
        }
    }

    /// <inheritdoc />
    public void Dispose() => _grenades.Walked -= OnWalked;

    // Only while clips render: a walk with them off must not leave the demo wanted once they come on.
    private void OnWalked(string path)
    {
        if (!_clips.Renders)
        {
            return;
        }

        lock (_walked)
        {
            _walked.Add(path);
        }
    }
}
