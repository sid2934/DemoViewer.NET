#region

using System.Globalization;

#endregion

namespace DemoViewer.NET.AssetBaker;

/// <summary>
///     Parses a CS2 radar overview <c>.txt</c> (a KeyValues-1 text file) into the world→radar transform and
///     the <c>verticalsections</c> radar-layer bands. Hand-rolled (tolerant of <c>//</c> comments + nesting) so
///     the baker owns exactly the fields it needs; the file is small and regular.
/// </summary>
public static class OverviewTxt
{
    public static Parsed Parse(string path)
    {
        string text = File.ReadAllText(path);
        KvNode root = ParseKv(text);

        // Root wraps one map block, e.g. { "de_nuke": { ...fields... } }. Take the first (only) block.
        KvNode map = root.Children.Values.OfType<KvNode>().FirstOrDefault()
                     ?? throw new InvalidDataException($"{path}: no map block found");

        double posX = map.GetDouble("pos_x");
        double posY = map.GetDouble("pos_y");
        double scale = map.GetDouble("scale");
        double rotate = map.GetDoubleOrDefault("rotate", 0); // absent ⇒ 0 (no rotation). [UNCERTAIN] — stored raw.
        double zoom = map.GetDoubleOrDefault("zoom", 1); // absent ⇒ 1. [UNCERTAIN] — stored raw.

        List<(string, double, double)> sections = new();
        if (map.Children.TryGetValue("verticalsections", out object? vsObj) && vsObj is KvNode vs)
        {
            foreach ((string name, object child) in vs.Ordered)
            {
                if (child is KvNode sec)
                {
                    double altMin = sec.GetDoubleOrDefault("AltitudeMin", double.NegativeInfinity);
                    double altMax = sec.GetDoubleOrDefault("AltitudeMax", double.PositiveInfinity);
                    sections.Add((name, altMin, altMax));
                }
            }
        }

        RadarTransform transform = new(posX, posY, scale, rotate, zoom, 1024);
        return new Parsed(transform, sections);
    }

    private static KvNode ParseKv(string text)
    {
        int i = 0;
        KvNode root = new();
        ParseBlockBody(text, ref i, root, true);
        return root;
    }

    // Parses key/value pairs until '}' (or EOF at top level). A value is either a quoted string or a nested block.
    private static void ParseBlockBody(string text, ref int i, KvNode node, bool topLevel)
    {
        while (true)
        {
            string? key = NextToken(text, ref i, out bool isBrace);
            if (key is null)
            {
                if (topLevel)
                {
                    return;
                }

                throw new InvalidDataException("unexpected EOF inside block");
            }

            if (isBrace)
            {
                if (key == "}")
                {
                    return; // end of this block
                }

                throw new InvalidDataException("unexpected '{' where a key was expected");
            }

            // Read the value: either a nested block '{' or a scalar string.
            string? next = NextToken(text, ref i, out bool valIsBrace);
            if (next is null)
            {
                throw new InvalidDataException($"unexpected EOF after key '{key}'");
            }

            if (valIsBrace && next == "{")
            {
                KvNode child = new();
                ParseBlockBody(text, ref i, child, false);
                node.Add(key, child);
            }
            else if (valIsBrace)
            {
                throw new InvalidDataException($"unexpected '}}' after key '{key}'");
            }
            else
            {
                node.Add(key, next); // scalar
            }
        }
    }

    // Returns the next token: a quoted/bare string (isBrace=false) or a single "{"/"}" (isBrace=true). Skips
    // whitespace and // line comments. Null at EOF.
    private static string? NextToken(string text, ref int i, out bool isBrace)
    {
        isBrace = false;
        while (i < text.Length)
        {
            char c = text[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }

            switch (c)
            {
                case '/' when i + 1 < text.Length && text[i + 1] == '/':
                {
                    while (i < text.Length && text[i] != '\n')
                    {
                        i++;
                    }

                    continue;
                }
                case '{':
                case '}':
                    i++;
                    isBrace = true;
                    return c.ToString();
                case '"':
                {
                    i++;
                    int start = i;
                    while (i < text.Length && text[i] != '"')
                    {
                        i++;
                    }

                    string s = text.Substring(start, i - start);
                    if (i < text.Length)
                    {
                        i++; // closing quote
                    }

                    return s;
                }
            }

            // bare token (rare in these files) — read until whitespace/brace/quote
            int bstart = i;
            while (i < text.Length && !char.IsWhiteSpace(text[i]) && text[i] != '{' && text[i] != '}' && text[i] != '"')
            {
                i++;
            }

            return text.Substring(bstart, i - bstart);
        }

        return null;
    }

    /// <summary>The parsed result: the transform + the (possibly empty) verticalsection list.</summary>
    public sealed record Parsed(
        RadarTransform Transform,
        IReadOnlyList<(string Name, double AltMin, double AltMax)> VerticalSections);

    // ── A minimal KeyValues-1 text tree ──

    private sealed class KvNode
    {
        public readonly Dictionary<string, object> Children = new(StringComparer.OrdinalIgnoreCase);

        // Preserves insertion order (verticalsections order matters) AND allows keyed lookup.
        public readonly List<(string Key, object Value)> Ordered = new();

        public void Add(string key, object value)
        {
            Ordered.Add((key, value));
            Children[key] = value; // last-wins on dup keys (fine for our fields)
        }

        public string GetString(string key) =>
            Children.TryGetValue(key, out object? v) && v is string s
                ? s
                : throw new InvalidDataException($"missing key '{key}'");

        public double GetDouble(string key) =>
            double.Parse(GetString(key), CultureInfo.InvariantCulture);

        public double GetDoubleOrDefault(string key, double fallback) =>
            Children.TryGetValue(key, out object? v) && v is string s
                                                     && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double d)
                ? d
                : fallback;
    }
}
