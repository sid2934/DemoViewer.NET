#region

using SkiaSharp;

#endregion

namespace DemoViewer.NET.Playback2D.Cli;

/// <summary>
///     Raised for anything the caller typed wrong. Always maps to <see cref="ExitCode.Usage" />.
/// </summary>
internal sealed class CliUsageException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="message">The message printed after <c>error:</c>.</param>
    public CliUsageException(string message) : base(message)
    {
    }

    /// <summary>Parameterless overload required by CA1032.</summary>
    public CliUsageException()
    {
    }

    /// <summary>Wrapping overload required by CA1032.</summary>
    /// <param name="message">The message printed after <c>error:</c>.</param>
    /// <param name="innerException">The cause.</param>
    public CliUsageException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

/// <summary>
///     The hand-rolled argument parser (C1 decision 2: no new dependency). It understands both repo
///     styles, <c>--name value</c> (DemoTrimmer) and <c>--name=value</c> (AnalysisBench), plus bare
///     flags, leading positional verbs and a <c>--</c> terminator.
///     <para>
///         <b>Unknown options are an error, not a no-op.</b> Every accessor marks the option consumed;
///         <see cref="ThrowIfUnconsumed" /> then fails on anything left over. A typo in a CI golden
///         invocation must fail loudly rather than silently render the wrong thing.
///     </para>
/// </summary>
internal sealed class CliArgs
{
    private readonly HashSet<string> _consumed = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string?> _options = new(StringComparer.Ordinal);
    private readonly List<string> _order = [];
    private readonly List<string> _positional = [];

    private CliArgs()
    {
    }

    /// <summary>The leading non-option tokens, plus anything after <c>--</c>.</summary>
    public IReadOnlyList<string> Positional => _positional;

    /// <summary>The command verb, or null when none was given.</summary>
    public string? Verb => _positional.Count > 0 ? _positional[0] : null;

    /// <summary>The sub-verb (<c>golden verify</c>, <c>fixture capture</c>), or null.</summary>
    public string? SubVerb => _positional.Count > 1 ? _positional[1] : null;

    /// <summary>True when <c>--help</c>, <c>-h</c> or <c>-?</c> was given anywhere.</summary>
    public bool WantsHelp => _options.ContainsKey("help");

    /// <summary>Parses a raw argument vector.</summary>
    /// <param name="args">The process arguments.</param>
    public static CliArgs Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        CliArgs parsed = new();
        bool terminated = false;

        for (int i = 0; i < args.Length; i++)
        {
            string token = args[i];

            if (terminated)
            {
                parsed._positional.Add(token);
                continue;
            }

            if (string.Equals(token, "--", StringComparison.Ordinal))
            {
                terminated = true;
                continue;
            }

            if (string.Equals(token, "-h", StringComparison.Ordinal) ||
                string.Equals(token, "-?", StringComparison.Ordinal))
            {
                parsed.AddOption("help", null);
                continue;
            }

            if (!IsOptionToken(token))
            {
                parsed._positional.Add(token);
                continue;
            }

            string body = token[2..];
            int eq = body.IndexOf('=', StringComparison.Ordinal);
            if (eq >= 0)
            {
                parsed.AddOption(body[..eq], body[(eq + 1)..]);
                continue;
            }

            // "--name value" only when the next token is not itself an option. That keeps "--speed -1"
            // working (a lone '-' prefix is a value, not an option) while "--cpu --json" stays two flags.
            string? value = i + 1 < args.Length && !IsOptionToken(args[i + 1]) &&
                            !string.Equals(args[i + 1], "--", StringComparison.Ordinal)
                ? args[++i]
                : null;
            parsed.AddOption(body, value);
        }

        return parsed;
    }

    /// <summary>True when the option was given. Consumes it.</summary>
    /// <param name="name">The option name without dashes.</param>
    public bool Flag(string name)
    {
        _consumed.Add(name);
        return _options.ContainsKey(name);
    }

    /// <summary>The option's value, or null when absent or given bare. Consumes it.</summary>
    /// <param name="name">The option name without dashes.</param>
    public string? String(string name)
    {
        _consumed.Add(name);
        return _options.GetValueOrDefault(name);
    }

    /// <summary>The option's value; throws when absent or valueless.</summary>
    /// <param name="name">The option name without dashes.</param>
    /// <exception cref="CliUsageException">The option is missing or carries no value.</exception>
    public string Require(string name) =>
        String(name) ?? throw new CliUsageException($"--{name} <value> is required.");

    /// <summary>The option parsed as an integer, or <paramref name="fallback" /> when absent.</summary>
    /// <param name="name">The option name without dashes.</param>
    /// <param name="fallback">Returned when the option is absent.</param>
    /// <exception cref="CliUsageException">The value is present but not an integer.</exception>
    public int Int(string name, int fallback)
    {
        string? raw = String(name);
        if (raw is null)
        {
            return fallback;
        }

        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
            ? value
            : throw new CliUsageException($"--{name} expects an integer, got '{raw}'.");
    }

    /// <summary>The option parsed as a double, or <paramref name="fallback" /> when absent.</summary>
    /// <param name="name">The option name without dashes.</param>
    /// <param name="fallback">Returned when the option is absent.</param>
    /// <exception cref="CliUsageException">The value is present but not a number.</exception>
    public double Double(string name, double fallback)
    {
        string? raw = String(name);
        if (raw is null)
        {
            return fallback;
        }

        return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
            ? value
            : throw new CliUsageException($"--{name} expects a number, got '{raw}'.");
    }

    /// <summary>The option parsed as <c>WxH</c>, or <paramref name="fallback" /> when absent.</summary>
    /// <param name="name">The option name without dashes.</param>
    /// <param name="fallback">Returned when the option is absent.</param>
    /// <exception cref="CliUsageException">The value is not two positive integers separated by 'x'.</exception>
    public SKSizeI Size(string name, SKSizeI fallback)
    {
        string? raw = String(name);
        if (raw is null)
        {
            return fallback;
        }

        string[] parts = raw.Split('x', 'X');
        if (parts.Length == 2 &&
            int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int w) &&
            int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int h) &&
            w > 0 && h > 0)
        {
            return new SKSizeI(w, h);
        }

        throw new CliUsageException($"--{name} expects WxH (e.g. 1920x1080), got '{raw}'.");
    }

    /// <summary>The option split on commas, or null when absent. Empty entries are dropped.</summary>
    /// <param name="name">The option name without dashes.</param>
    public IReadOnlyList<string>? List(string name)
    {
        string? raw = String(name);
        return raw is null
            ? null
            : raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    /// <summary>Marks a positional token consumed for the purpose of unknown-option detection.</summary>
    /// <remarks>Positionals are never reported as unknown; this exists only for symmetry in commands.</remarks>
    public void ConsumeVerbs()
    {
        _consumed.Add("help");
    }

    /// <summary>Fails when any option was never read by the dispatched command.</summary>
    /// <exception cref="CliUsageException">One or more options are unknown to this command.</exception>
    public void ThrowIfUnconsumed()
    {
        List<string> unknown = [];
        foreach (string name in _order)
        {
            if (!_consumed.Contains(name))
            {
                unknown.Add("--" + name);
            }
        }

        if (unknown.Count > 0)
        {
            throw new CliUsageException(
                $"unknown option{(unknown.Count > 1 ? "s" : "")}: {string.Join(", ", unknown)}");
        }
    }

    private static bool IsOptionToken(string token) =>
        token.Length > 2 && token.StartsWith("--", StringComparison.Ordinal);

    private void AddOption(string name, string? value)
    {
        if (!_options.ContainsKey(name))
        {
            _order.Add(name);
        }

        _options[name] = value;
    }
}
