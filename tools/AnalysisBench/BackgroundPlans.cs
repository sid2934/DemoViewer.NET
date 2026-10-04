#region

using CS2DemoKit.Parser;

#endregion

namespace AnalysisBench;

/// <summary>
///     Narrower parses for the background-work memory measurements. Each name maps to a
///     <see cref="DecodePlan" /> passed through <see cref="ParseOptions.Plan" />; nothing here changes the parser.
/// </summary>
internal static class BackgroundPlans
{
    public static DecodePlan Resolve(string name) => name switch
    {
        "everything" => DecodePlan.Everything,
        "events" => DecodePlan.GameEventsOnly,
        "structure" => DecodePlan.StructureOnly,
        "replay" => DecodePlan.EntityReplay,
        // The shared background parse without svc_UserCmds, which only Grenade Walk reads.
        "no-usercmds" => DecodePlan.Everything with
        {
            Categories = MessageCategories.All & ~MessageCategories.UserCmds
        },
        _ => throw new ArgumentException($"unknown plan '{name}'")
    };

    public static ParsedDemo Parse(string path, string read, string name)
    {
        ParseOptions options = new()
        {
            Plan = Resolve(name)
        };
        return read == "bytes"
            ? DemoParser.Parse(File.ReadAllBytes(path), options)
            : MemoryMappedDemoSource.ParseFile(path, options);
    }
}
