#region

using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.Json.Serialization;

#endregion

namespace DemoViewer.NET.Services.Startup;

/// <summary>Why this launch runs without extensions.</summary>
public enum SafeModeReason
{
    /// <summary>The app was started with <see cref="LaunchGuard.SafeModeArgument" />.</summary>
    Requested,

    /// <summary>The previous launch never finished starting: it crashed or was closed while starting.</summary>
    StartupFailed,

    /// <summary>The previous session crashed inside an extension.</summary>
    ExtensionCrashed,

    /// <summary>The previous session stopped responding and never recovered.</summary>
    StoppedResponding
}

/// <summary>Whether this launch is in safe mode, and why.</summary>
/// <param name="IsActive">True when no extension loads, the shipped ones included.</param>
/// <param name="Reason">Why, when active.</param>
/// <param name="ExtensionName">The extension the previous crash was in, when one was found on its stack.</param>
/// <param name="ExtensionFeatureId">That extension's master switch, so the banner can offer to turn it off.</param>
public sealed record SafeModeState(bool IsActive, SafeModeReason? Reason, string? ExtensionName = null, string? ExtensionFeatureId = null)
{
    /// <summary>A normal launch.</summary>
    public static SafeModeState Off { get; } = new(false, null);

    /// <summary>The banner's sentence.</summary>
    public string Message => Reason switch
    {
        SafeModeReason.Requested => "Safe mode: extensions are off because the app was started with --safe-mode.",
        SafeModeReason.StartupFailed when ExtensionName is not null =>
            $"Safe mode: extensions are off because the last launch did not finish starting. It failed in {ExtensionName}.",
        SafeModeReason.StartupFailed => "Safe mode: extensions are off because the last launch did not finish starting.",
        SafeModeReason.ExtensionCrashed => $"Safe mode: extensions are off because the last session crashed in {ExtensionName}.",
        SafeModeReason.StoppedResponding => "Safe mode: extensions are off because the last session stopped responding.",
        _ => ""
    };
}

/// <summary>An extension as crash attribution sees it.</summary>
/// <param name="Assembly">Its entry assembly.</param>
/// <param name="Name">Its display name.</param>
/// <param name="FeatureId">Its master switch.</param>
/// <param name="LoadContext">
///     Its own load context, whose every assembly (its private dependencies too) is its code; null when it
///     shares the app's default context and only its entry assembly is.
/// </param>
public sealed record ExtensionIdentity(Assembly Assembly, string Name, string FeatureId, AssemblyLoadContext? LoadContext = null)
{
    /// <summary>True when <paramref name="assembly" /> is this extension's code.</summary>
    public bool Owns(Assembly assembly) =>
        ReferenceEquals(assembly, Assembly)
        || (LoadContext is not null && !ReferenceEquals(LoadContext, AssemblyLoadContext.Default)
                                    && ReferenceEquals(AssemblyLoadContext.GetLoadContext(assembly), LoadContext));
}

/// <summary>
///     Tracks a desktop launch in <c>&lt;config root&gt;/launch-state.json</c> so the next launch can tell whether
///     this one started, crashed or froze, and go into safe mode when an extension may be why. Every write is
///     best effort: losing one never stops the app.
/// </summary>
public sealed class LaunchGuard
{
    /// <summary>The state file's name under the config root.</summary>
    public const string FileName = "launch-state.json";

    /// <summary>The command-line switch that starts the app with every extension off.</summary>
    public const string SafeModeArgument = "--safe-mode";

    private static readonly JsonSerializerOptions _json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly Lock _gate = new();
    private readonly string? _path;
    private readonly TimeProvider _clock;
    private LaunchState _state;

    private LaunchGuard(string? path, TimeProvider clock, SafeModeState decision, LaunchState state)
    {
        _path = path;
        _clock = clock;
        Decision = decision;
        _state = state;
    }

    /// <summary>The guard of this process, once the desktop head began one.</summary>
    public static LaunchGuard? Current { get; private set; }

    /// <summary>Whether this launch runs in safe mode, and why.</summary>
    public SafeModeState Decision { get; }

    /// <summary>
    ///     Reads what the previous launch left, decides whether this one is safe mode, and records this one as
    ///     starting. <see cref="Install" /> makes it the process's.
    /// </summary>
    /// <param name="configRoot">The config root; null (no filesystem) decides from the arguments alone.</param>
    /// <param name="args">The command line.</param>
    /// <param name="clock">The clock; null for the system's.</param>
    public static LaunchGuard Begin(string? configRoot, IReadOnlyList<string> args, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(args);
        clock ??= TimeProvider.System;
        string? path = configRoot is null ? null : Path.Combine(configRoot, FileName);
        SafeModeState decision = Decide(Read(path), args.Any(IsSafeModeArgument));
        LaunchState state = new(LaunchPhase.Starting, clock.GetUtcNow(), decision.IsActive, Environment.ProcessId);
        LaunchGuard guard = new(path, clock, decision, state);
        guard.Write();
        return guard;
    }

    /// <summary>Makes this the process's <see cref="Current" />, which the shell reads for the safe-mode banner.</summary>
    public LaunchGuard Install()
    {
        Current = this;
        return this;
    }

    /// <summary>True for the safe-mode switch, in either dash style.</summary>
    public static bool IsSafeModeArgument(string arg) =>
        string.Equals(arg, SafeModeArgument, StringComparison.OrdinalIgnoreCase)
        || string.Equals(arg, "/safe-mode", StringComparison.OrdinalIgnoreCase);

    /// <summary>The decision rule, apart from the file: see the type's summary.</summary>
    public static SafeModeState Decide(LaunchState? previous, bool requested)
    {
        if (requested)
        {
            return new SafeModeState(true, SafeModeReason.Requested);
        }

        if (previous is null || previous.Phase == LaunchPhase.Exited)
        {
            return SafeModeState.Off;
        }

        if (previous.Phase == LaunchPhase.Starting)
        {
            return new SafeModeState(true, SafeModeReason.StartupFailed, previous.Crash?.ExtensionName,
                previous.Crash?.ExtensionFeatureId);
        }

        if (previous.Crash is { ExtensionName: { } name } crash)
        {
            return new SafeModeState(true, SafeModeReason.ExtensionCrashed, name, crash.ExtensionFeatureId);
        }

        return previous.Hung ? new SafeModeState(true, SafeModeReason.StoppedResponding) : SafeModeState.Off;
    }

    /// <summary>The app finished starting and its UI answered: a later failure is no longer a failed start.</summary>
    public void MarkRunning() => Update(s => s.Phase == LaunchPhase.Starting ? s with { Phase = LaunchPhase.Running } : s);

    /// <summary>The app is closing normally.</summary>
    public void MarkExited() => Update(s => s with { Phase = LaunchPhase.Exited, Hung = false });

    /// <summary>The UI thread stopped answering (true), or answered again (false).</summary>
    public void MarkHung(bool hung) => Update(s => s.Hung == hung ? s : s with { Hung = hung });

    /// <summary>
    ///     Records an unhandled exception, naming the first of <paramref name="extensions" /> whose code is on its
    ///     stack. Called from the process's last-chance handler, so it never throws.
    /// </summary>
    public void RecordCrash(Exception exception, IEnumerable<ExtensionIdentity> extensions)
    {
        try
        {
            ExtensionIdentity? culprit = Attribute(exception, extensions);
            Update(s => s with
            {
                Crash = new CrashRecord(_clock.GetUtcNow(), exception.GetType().FullName ?? exception.GetType().Name,
                    culprit?.Name, culprit?.FeatureId)
            });
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The last-chance handler must not throw over the crash it is recording.
        }
    }

    /// <summary>
    ///     The extension whose code is deepest-first on <paramref name="exception" />'s stack, inner exceptions
    ///     included: a frame in its entry assembly or in any assembly of its own load context.
    /// </summary>
    public static ExtensionIdentity? Attribute(Exception exception, IEnumerable<ExtensionIdentity> extensions)
    {
        ArgumentNullException.ThrowIfNull(exception);
        ArgumentNullException.ThrowIfNull(extensions);
        ExtensionIdentity[] known = [.. extensions];
        return known.Length == 0
            ? null
            : DemoViewer.NET.Extensions.StackAttribution.Find(exception, a => known.FirstOrDefault(k => k.Owns(a)));
    }

    /// <summary>Reads a state file; null when it is missing or unreadable.</summary>
    public static LaunchState? Read(string? path)
    {
        try
        {
            return path is not null && File.Exists(path)
                ? JsonSerializer.Deserialize<LaunchState>(File.ReadAllText(path), _json)
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private void Update(Func<LaunchState, LaunchState> change)
    {
        lock (_gate)
        {
            LaunchState next = change(_state);
            if (next == _state)
            {
                return;
            }

            _state = next;
            Write();
        }
    }

    // Written to a sibling file and moved over the old one, so a crash mid-write leaves the previous state.
    private void Write()
    {
        if (_path is null)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            string temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_state, _json));
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: the next launch reads whatever was last written.
        }
    }
}

/// <summary>Where a launch got to.</summary>
public enum LaunchPhase
{
    /// <summary>Started, not yet up.</summary>
    Starting,

    /// <summary>Up and answering.</summary>
    Running,

    /// <summary>Closed normally.</summary>
    Exited
}

/// <summary>The state file's contents.</summary>
/// <param name="Phase">Where the launch got to.</param>
/// <param name="StartedUtc">When it started.</param>
/// <param name="SafeMode">Whether it ran in safe mode.</param>
/// <param name="ProcessId">Its process id, for the log.</param>
/// <param name="Hung">True while its UI thread was not answering.</param>
/// <param name="Crash">Its unhandled exception, when it had one.</param>
public sealed record LaunchState(
    LaunchPhase Phase,
    DateTimeOffset StartedUtc,
    bool SafeMode,
    int ProcessId,
    bool Hung = false,
    CrashRecord? Crash = null);

/// <summary>An unhandled exception, as the next launch needs it.</summary>
/// <param name="AtUtc">When.</param>
/// <param name="ExceptionType">Its type.</param>
/// <param name="ExtensionName">The extension found on its stack, or null.</param>
/// <param name="ExtensionFeatureId">That extension's master switch.</param>
public sealed record CrashRecord(DateTimeOffset AtUtc, string ExceptionType, string? ExtensionName, string? ExtensionFeatureId);
