#region

using System.Diagnostics;
using Avalonia;
using CommunityToolkit.Mvvm.Input;

#endregion

namespace DemoViewer.NET.ViewModels;

/// <summary>
///     One row in the Parse Chain panel.  Carries display text, optional source-link metadata,
///     and an open command that navigates to VS Code (local) or a browser (web) on click.
/// </summary>
public sealed class ParseChainEntry
{
    /// <summary>Secondary detail shown in a lighter style after the label, e.g. "(cmd=4, tick=1234)".</summary>
    public string? Detail { get; init; }

    /// <summary>Left padding derived from indent level.</summary>
    public Thickness Indent => new(IndentLevel * 14.0, 1, 4, 1);

    /// <summary>Nesting depth — each level adds left margin.</summary>
    public int IndentLevel { get; init; }

    /// <summary>Is clickable.</summary>
    public bool IsClickable => OpenCommand != null;

    /// <summary>Primary label shown in the monospace column.</summary>
    public string Label { get; init; } = "";

    // Bound in AXAML
    /// <summary>Open command.</summary>
    public RelayCommand? OpenCommand { get; private init; }

    /// <summary>Source badge shown right-aligned, e.g. "netmessages.proto:201".</summary>
    public string? SourceBadge { get; init; }

    // ── Factories ─────────────────────────────────────────────────────────────

    /// <summary>Creates a non-clickable informational row.</summary>
    public static ParseChainEntry Info(string label, string? detail = null, int indent = 0) =>
        new()
        {
            Label = label,
            Detail = detail,
            IndentLevel = indent
        };

    /// <summary>Creates a row linked to a local file (opens in VS Code) with a web URL fallback.</summary>
    public static ParseChainEntry Linked(
        string label,
        string? detail = null,
        string? localPath = null,
        int? localLine = null,
        string? webUrl = null,
        int indent = 0)
    {
        string? badge = localPath != null
            ? $"{Path.GetFileName(localPath)}{(localLine.HasValue ? $":{localLine}" : "")}"
            : webUrl != null
                ? TryGetHostBadge(webUrl)
                : null;

        RelayCommand? cmd = localPath != null || webUrl != null
            ? new RelayCommand(() => OpenSource(localPath, localLine, webUrl))
            : null;

        return new ParseChainEntry
        {
            Label = label,
            Detail = detail,
            SourceBadge = badge,
            IndentLevel = indent,
            OpenCommand = cmd
        };
    }

    // ── Source-opening ────────────────────────────────────────────────────────

    private static void OpenSource(string? localPath, int? line, string? webUrl)
    {
        if (localPath != null && File.Exists(localPath))
        {
            // Try VS Code with --goto for line-accurate navigation
            if (TryOpenVsCode(localPath, line))
            {
                return;
            }

            // Fallback: open the file with the OS default handler
            try
            {
                Process.Start(new ProcessStartInfo(localPath)
                {
                    UseShellExecute = true
                });
                return;
            }
            catch
            {
            }
        }

        if (webUrl != null)
        {
            try
            {
                Process.Start(new ProcessStartInfo(webUrl)
                {
                    UseShellExecute = true
                });
            }
            catch
            {
            }
        }
    }

    private static string? TryGetHostBadge(string url)
    {
        try
        {
            return new Uri(url).Host;
        }
        catch
        {
            return null;
        }
    }

    private static bool TryOpenVsCode(string path, int? line)
    {
        try
        {
            string gotoArg = line.HasValue ? $"{path}:{line}" : path;
            ProcessStartInfo psi = new()
            {
                FileName = "code",
                Arguments = $"--goto \"{gotoArg}\"",
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                CreateNoWindow = true
            };
            Process.Start(psi);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
