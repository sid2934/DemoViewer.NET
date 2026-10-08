#region

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

#endregion

namespace DemoViewer.NET.Extensions.StratBook.Services.Strats;

/// <summary>
///     The one reading of a location field (<see cref="PlaceRef" />, a landing, a watched entry): a place, a point
///     picked outside every place, or both. Every surface that prints one goes through <see cref="Text(PlaceRef?, CalloutResolver?)" />:
///     the place by the owner's word when there is one, else the point as its rounded world coordinate,
///     <c>(1234, -560)</c>. The coordinate needs no zones, so an export prints the same before and after they load.
/// </summary>
public static class StratLocations
{
    /// <summary>Whether the location names a place.</summary>
    /// <param name="location">A location, or null.</param>
    public static bool HasPlace(PlaceRef? location) => !string.IsNullOrEmpty(location?.Place);

    /// <summary>Whether the location holds a finite world point.</summary>
    /// <param name="location">A location, or null.</param>
    public static bool HasPoint(PlaceRef? location) =>
        location is { X: { } x, Y: { } y } && double.IsFinite(x) && double.IsFinite(y);

    /// <summary>Whether the location says anything: a place or a point.</summary>
    /// <param name="location">A location, or null.</param>
    public static bool IsSet(PlaceRef? location) => HasPlace(location) || HasPoint(location);

    /// <summary>A point as people read it: whole world units, <c>(1234, -560)</c>.</summary>
    /// <param name="x">World X.</param>
    /// <param name="y">World Y.</param>
    public static string PointText(double x, double y) =>
        string.Create(CultureInfo.InvariantCulture, $"({Whole(x)}, {Whole(y)})");

    /// <summary>What a location prints as: the place's display name, else the point, else null.</summary>
    /// <param name="place">The canonical place, or null.</param>
    /// <param name="x">World X, or null.</param>
    /// <param name="y">World Y, or null.</param>
    /// <param name="callouts">The owner's words; null prints canonical names split into words.</param>
    public static string? Text(string? place, double? x, double? y, CalloutResolver? callouts)
    {
        if (!string.IsNullOrEmpty(place))
        {
            return StratDiffPhrasing.Place(place, callouts);
        }

        return x is { } px && y is { } py && double.IsFinite(px) && double.IsFinite(py) ? PointText(px, py) : null;
    }

    /// <summary>What a location prints as, or null when it says nothing.</summary>
    /// <param name="location">A location, or null.</param>
    /// <param name="callouts">The owner's words; null prints canonical names split into words.</param>
    public static string? Text(PlaceRef? location, CalloutResolver? callouts) =>
        location is null ? null : Text(location.Place, location.X, location.Y, callouts);

    /// <summary>What a landing prints as, or null when it says nothing.</summary>
    /// <param name="landing">A landing, or null.</param>
    /// <param name="callouts">The owner's words.</param>
    public static string? Text(UtilityLanding? landing, CalloutResolver? callouts) =>
        landing is null ? null : Text(landing.Place, landing.X, landing.Y, callouts);

    /// <summary>What a location's JSON prints as, for the history: an object with a place or a point, else null.</summary>
    /// <param name="node">A location object from a patch or a snapshot.</param>
    /// <param name="callouts">The owner's words.</param>
    public static string? NodeText(JsonNode? node, CalloutResolver? callouts) =>
        node is JsonObject obj ? Text(TextOf(obj["place"]), NumberOf(obj["x"]), NumberOf(obj["y"]), callouts) : null;

    /// <summary>Whether a JSON object has only location members, so the history may print it as one.</summary>
    /// <param name="obj">An object from a patch.</param>
    public static bool IsLocationNode(JsonObject obj) =>
        obj.Count > 0 && obj.All(p => p.Key is "place" or "x" or "y" or "levelMinZ");

    /// <summary>A watch's entries in reading order: its places, then its points. The first is what the token faces.</summary>
    /// <param name="watch">A watch, or null.</param>
    public static IReadOnlyList<PlaceRef> Watched(StepWatch? watch)
    {
        if (watch is null)
        {
            return [];
        }

        List<PlaceRef> entries = [.. watch.Places.Where(p => !string.IsNullOrEmpty(p)).Select(p => new PlaceRef { Place = p })];
        entries.AddRange(watch.Points?.Where(HasPoint) ?? []);
        return entries;
    }

    /// <summary>What a travel goes through, in reading order: the places, then the points.</summary>
    /// <param name="places">The <c>via</c> places, or null.</param>
    /// <param name="points">The <c>viaPoints</c>, or null.</param>
    public static IReadOnlyList<PlaceRef> Via(IReadOnlyList<string>? places, IReadOnlyList<PlaceRef>? points)
    {
        List<PlaceRef> entries = [.. (places ?? []).Where(p => !string.IsNullOrEmpty(p)).Select(p => new PlaceRef { Place = p })];
        entries.AddRange(points?.Where(HasPoint) ?? []);
        return entries;
    }

    /// <summary>A lurk's areas in reading order: its places, then its points.</summary>
    /// <param name="lurk">A lurk, or null.</param>
    public static IReadOnlyList<PlaceRef> LurkAreas(StepLurk? lurk)
    {
        if (lurk is null)
        {
            return [];
        }

        List<PlaceRef> entries = [.. lurk.Areas.Where(p => !string.IsNullOrEmpty(p)).Select(p => new PlaceRef { Place = p })];
        entries.AddRange(lurk.AreaPoints?.Where(HasPoint) ?? []);
        return entries;
    }

    /// <summary>
    ///     The location a map click sets: the place under the point and the point with its level. With no answer
    ///     about places (the map's zones are missing), the stored place stays, as a landing always did. From the
    ///     stored reference, so a field a newer build wrote survives.
    /// </summary>
    /// <param name="stored">The location as it stands, or null.</param>
    /// <param name="place">The place under the click, or null when it is in none.</param>
    /// <param name="x">World X.</param>
    /// <param name="y">World Y.</param>
    /// <param name="levelMinZ">The clicked floor's level key.</param>
    /// <param name="placesKnown">False when the map's places could not be read.</param>
    public static PlaceRef Picked(PlaceRef? stored, string? place, double x, double y, double levelMinZ, bool placesKnown)
    {
        PlaceRef next = stored is null ? new PlaceRef() : Clone(stored);
        next.Place = placesKnown ? place : stored?.Place;
        next.X = Round(x);
        next.Y = Round(y);
        next.LevelMinZ = levelMinZ;
        return next;
    }

    /// <summary>
    ///     The location a typed place sets: the stored one when it already names that place (its point kept), else
    ///     the place alone, so a token never faces a point under another place's name. Null for no place.
    /// </summary>
    /// <param name="stored">The location as it stands, or null.</param>
    /// <param name="place">The canonical place, or the text as typed, or null.</param>
    public static PlaceRef? Typed(PlaceRef? stored, string? place)
    {
        if (string.IsNullOrEmpty(place))
        {
            return null;
        }

        if (stored is not null && string.Equals(stored.Place, place, StringComparison.Ordinal))
        {
            return Clone(stored);
        }

        PlaceRef next = stored is null ? new PlaceRef() : Clone(stored);
        next.Place = place;
        next.X = null;
        next.Y = null;
        next.LevelMinZ = null;
        return next;
    }

    /// <summary>Whether two locations write the same JSON, unknown fields included.</summary>
    /// <param name="a">A location, or null.</param>
    /// <param name="b">A location, or null.</param>
    public static bool Same(PlaceRef? a, PlaceRef? b) =>
        (a is null && b is null) || (a is not null && b is not null && string.Equals(Json(a), Json(b), StringComparison.Ordinal));

    /// <summary>A deep copy, unknown fields included.</summary>
    /// <param name="location">The location.</param>
    public static PlaceRef Clone(PlaceRef location) =>
        JsonSerializer.Deserialize(Json(location), StratJsonContext.Default.PlaceRef)!;

    /// <summary>A landing read as a location: the two write the same members.</summary>
    /// <param name="landing">The landing.</param>
    public static PlaceRef FromLanding(UtilityLanding landing) =>
        JsonSerializer.Deserialize(JsonSerializer.Serialize(landing, StratJsonContext.Default.UtilityLanding), StratJsonContext.Default.PlaceRef)!;

    /// <summary>A location written as a landing.</summary>
    /// <param name="location">The location.</param>
    public static UtilityLanding ToLanding(PlaceRef location) =>
        JsonSerializer.Deserialize(Json(location), StratJsonContext.Default.UtilityLanding)!;

    // Two decimals in the file, as a token position: finer is pointer noise.
    private static double Round(double value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);

    // + 0.0 turns a rounded -0 into 0, so a point never prints as "-0".
    private static double Whole(double value) => Math.Round(value, MidpointRounding.AwayFromZero) + 0.0;

    private static string Json(PlaceRef location) => JsonSerializer.Serialize(location, StratJsonContext.Default.PlaceRef);

    private static string? TextOf(JsonNode? node) =>
        node is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : null;

    // Through the JSON text: a value built in code may hold an int that GetValue<double> refuses.
    private static double? NumberOf(JsonNode? node) =>
        node is JsonValue value && value.GetValueKind() == JsonValueKind.Number
            ? double.Parse(value.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture)
            : null;
}
