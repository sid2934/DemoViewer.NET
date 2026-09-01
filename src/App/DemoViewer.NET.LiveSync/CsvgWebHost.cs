#region

using System.Globalization;
using Cs2VideoGenerator.Core;
using Cs2VideoGenerator.Core.DependencyInjection;
using Cs2VideoGenerator.Core.Grpc;
using Cs2VideoGenerator.Core.ProcessManagement;
using Cs2VideoGenerator.Core.Services;
using DemoViewer.NET.Configuration;
using DemoViewer.NET.Services.Dependencies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

#endregion

namespace DemoViewer.NET.LiveSync;

/// <summary>
///     The private, in-process CSVG gRPC host:
///     a slim <see cref="WebApplication" /> whose only jobs are hosting CSVG's
///     <see cref="Cs2GameService" /> on localhost:50051/HTTP2 (the port the CS2 plugin and mock
///     server dial back to, which is fixed by the plugin and not configurable) and owning the CSVG service
///     container. Its DI world is fully isolated from the app's. CSVG never sees app services and
///     the app resolves only <see cref="Session" /> from it.
///     <para>
///         Started lazily on user enable, never at app start. The configuration is EXCLUSIVELY the
///         in-memory projection of <see cref="LiveSyncSettings" />: every ambient source
///         (appsettings.json next to the app binary, DOTNET_/ASPNETCORE_ environment variables)
///         is cleared so machine state cannot bleed into CSVG's options.
///     </para>
/// </summary>
public sealed class CsvgWebHost : IAsyncDisposable
{
    /// <summary>The fixed plugin dial-back port.</summary>
    public const int GrpcPort = 50051;

    private readonly WebApplication _app;

    private CsvgWebHost(WebApplication app) => _app = app;

    /// <summary>
    ///     The CSVG video session (2.0 object graph, replaces the deleted <c>ICsvgClient</c>),
    ///     owned by this host's container. Registered as a singleton by
    ///     <c>AddCs2VideoGeneratorCore</c>; demo control lives on <see cref="CsvgVideoSession.Engine" />.
    /// </summary>
    public CsvgVideoSession Session => _app.Services.GetRequiredService<CsvgVideoSession>();

    /// <summary>
    ///     The mock user-action injection channel: the mock manager doubles as the
    ///     injector in mock mode; a no-op stub otherwise. Test-facing: the integration suite
    ///     drives in-game user actions through it and asserts DV's mirroring.
    /// </summary>
    public IMockUserActionInjector MockInjector =>
        _app.Services.GetRequiredService<IMockUserActionInjector>();

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _app.StopAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
        catch
        {
            // Kestrel teardown is best-effort; container disposal below still runs.
        }

        await _app.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    ///     Builds and starts the host. Throws <see cref="LiveSyncPortInUseException" /> when
    ///     another process (typically a CSVG CLI) already owns port 50051; other startup failures
    ///     propagate as-is. On any failure the partially-built host is disposed before throwing.
    /// </summary>
    public static async Task<CsvgWebHost> StartAsync(LiveSyncSettings settings, ILoggerProvider? logBridge,
        CancellationToken cancellationToken, string? captureProvider = null)
    {
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();

        // Ambient-config guard: drop every pre-registered source (appsettings.json,
        // DOTNET_/ASPNETCORE_ env vars, command line) so the ONLY configuration CSVG binds is the
        // in-memory projection below.
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(ProjectSettings(settings, captureProvider));

        // CSVG logs are surfaced through DV's Output panel, not a console this GUI app lacks.
        builder.Logging.ClearProviders();
        if (logBridge is not null)
        {
            builder.Logging.AddProvider(logBridge);
        }

        // The log bridge (OutputLogBridge) is the SOLE provider and the SOLE gate: it decides per
        // record, reading the min-level + framework-capture toggles LIVE so the user can change
        // verbosity on this running host with no reconnect. Floor MEL at Trace (+ explicit Trace
        // filters for the framework prefixes, defeating any default cap CreateSlimBuilder may add)
        // so nothing is pre-dropped below the bridge; MEL then still consults the bridge's
        // IsEnabled, which short-circuits state allocation for records the bridge would drop.
        builder.Logging.SetMinimumLevel(LogLevel.Trace);
        builder.Logging.AddFilter("Microsoft", LogLevel.Trace);
        builder.Logging.AddFilter("Grpc", LogLevel.Trace);
        builder.Logging.AddFilter("System", LogLevel.Trace);

        builder.Services.AddCs2VideoGeneratorCore(builder.Configuration);
        builder.Services.AddGrpc();
        builder.WebHost.ConfigureKestrel(kestrel =>
            kestrel.ListenLocalhost(GrpcPort, listen => listen.Protocols = HttpProtocols.Http2));

        WebApplication app = builder.Build();
        app.MapGrpcService<Cs2GameService>();

        try
        {
            await app.StartAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsAddressInUse(ex))
        {
            await app.DisposeAsync().ConfigureAwait(false);
            throw new LiveSyncPortInUseException(ex);
        }
        catch
        {
            await app.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        return new CsvgWebHost(app);
    }

    /// <summary>
    ///     Projects DV's <see cref="LiveSyncSettings" /> into CSVG's <c>Cs2VideoGenerator</c>
    ///     configuration section. <paramref name="captureProvider" /> pins
    ///     <c>VideoCaptureProvider</c> when set: the reel host passes
    ///     <c>CaptureProviderNames.InEngineHooked</c> so capture is deterministic rather than
    ///     inheriting CSVG 2.0's new <c>InEngine</c> default; the playback-only sync host leaves it
    ///     null (its <c>watch</c> session runs <c>initializeCapture: false</c>, so the provider is
    ///     never brought up and the value is immaterial). Shared with <see cref="InstallRecovery" />'s
    ///     short-lived detection container.
    /// </summary>
    internal static Dictionary<string, string?> ProjectSettings(LiveSyncSettings settings,
        string? captureProvider = null)
    {
        const string Section = Cs2VideoGeneratorOptions.ConfigurationSectionName;
        Dictionary<string, string?> config = new(StringComparer.OrdinalIgnoreCase)
        {
            [$"{Section}:MockMode"] = settings.MockMode ? "true" : "false",
            [$"{Section}:ForceIncompatiblePlugin"] = settings.ForceIncompatiblePlugin ? "true" : "false",
            [$"{Section}:GrpcPort"] = GrpcPort.ToString(CultureInfo.InvariantCulture)
        };

        if (!string.IsNullOrWhiteSpace(captureProvider))
        {
            config[$"{Section}:VideoCaptureProvider"] = captureProvider;
        }

        if (!string.IsNullOrWhiteSpace(settings.ExternalMockServerPath))
        {
            config[$"{Section}:ExternalMockServerPath"] = settings.ExternalMockServerPath;
        }

        if (!string.IsNullOrWhiteSpace(settings.Cs2RootInstallationDirectory))
        {
            config[$"{Section}:Cs2RootInstallationDirectory"] = settings.Cs2RootInstallationDirectory;
        }

        // Guided ffmpeg (v0.6.0): CSVG resolves ffmpeg from PATH unless Ffmpeg:BinaryDirectory is
        // set. Since this projection is the ONLY config source (ambient sources are cleared
        // above), the user-populated drop-in folder (<config>/tools/ffmpeg, see FfmpegDependency)
        // would be unreachable without this line. Projected only when the drop-in copy is the
        // resolution; a PATH install keeps CSVG's default behavior.
        FfmpegStatus ffmpeg = FfmpegDependency.Locate();
        if (ffmpeg is { Found: true, Source: FfmpegSource.Managed, Directory: { } ffmpegDir })
        {
            config[$"{FfmpegOptions.SectionName}:BinaryDirectory"] = ffmpegDir;
        }

        return config;
    }

    private static bool IsAddressInUse(Exception ex) =>
        ex is AddressInUseException
        || ex.InnerException is AddressInUseException
        || ex is IOException io && io.InnerException is AddressInUseException;
}

/// <summary>
///     Port 50051 is owned by another process. The message is the user-facing copy
///     ; the flyout offers Retry / Disable.
/// </summary>
public sealed class LiveSyncPortInUseException(Exception inner) : InvalidOperationException(
    $"Another program is using the CS2 sync port ({CsvgWebHost.GrpcPort}). " +
    "Close other CSVG tools and retry.", inner);
