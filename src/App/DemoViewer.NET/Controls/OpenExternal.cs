#region

using System.Diagnostics;

#endregion

namespace DemoViewer.NET.Controls;

/// <summary>
///     Helpers for opening source links — VS Code (preferred for local paths with line numbers)
///     and the OS default browser/handler (fallback). Used by <see cref="ParseLinkChip" /> and
///     migrated from <c>ParseChainEntry</c> for reuse by future link-rendering surfaces
///     (entity class links, field decode errors).
/// </summary>
public static class OpenExternal
{
    /// <summary>
    ///     Open <paramref name="localPath" /> with VS Code first; if that fails, hand off to the
    ///     OS default file handler. <paramref name="column" /> is honored only alongside
    ///     <paramref name="line" /> (<c>code --goto</c> takes <c>file:line:col</c>).
    ///     Returns false when neither launch could even be attempted (v0.6.0 — callers with a
    ///     status surface say so instead of a click silently doing nothing).
    /// </summary>
    public static bool OpenLocalFile(string localPath, int? line = null, int? column = null)
    {
        if (TryOpenInVsCode(localPath, line, column))
        {
            return true;
        }

        try
        {
            Process.Start(new ProcessStartInfo(localPath)
            {
                UseShellExecute = true
            });
            return true;
        }
        catch
        {
            // Missing handler / revoked file association — reported via the return value; a link
            // click must never crash the app.
            return false;
        }
    }

    /// <summary>
    ///     Open <paramref name="uri" /> via the OS default handler. Never throws; returns false when
    ///     the handler launch failed so callers can surface "couldn't open" instead of silence.
    /// </summary>
    public static bool OpenUri(string uri)
    {
        try
        {
            Process.Start(new ProcessStartInfo(uri)
            {
                UseShellExecute = true
            });
            return true;
        }
        catch
        {
            // No registered handler for the scheme — reported via the return value.
            return false;
        }
    }

    /// <summary>
    ///     Try to open <paramref name="localPath" /> in VS Code via the <c>code --goto</c> CLI.
    ///     Returns <see langword="true" /> if the launch was attempted without throwing.
    ///     Falls back silently on systems where the <c>code</c> binary is unavailable.
    /// </summary>
    public static bool TryOpenInVsCode(string localPath, int? line = null, int? column = null)
    {
        try
        {
            string gotoArg = (line, column) switch
            {
                (null, _) => localPath,
                (not null, null) => $"{localPath}:{line}",
                _ => $"{localPath}:{line}:{column}"
            };
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
