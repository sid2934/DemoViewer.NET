namespace DemoViewer.NET.Modules.Playback2D;

/// <summary>
///     One entry in the Follow-Player picker (#2): a player the camera can be told to track, identified by
///     stable <see cref="Slot" /> with a display <see cref="Name" /> and a team tag for the menu. Built from
///     the roster on demand when the mode menu opens, so it lists the current match players by name.
/// </summary>
public readonly record struct FollowablePlayer(int Slot, string Name, int Team)
{
    /// <summary>Menu label, e.g. "CT  s1mple" / "T  ZywOo".</summary>
    public string Display => $"{TeamTag}  {Name}";

    private string TeamTag => Team switch
    {
        2 => "T",
        3 => "CT",
        _ => "—"
    };
}
