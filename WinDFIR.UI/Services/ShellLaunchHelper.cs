using System;
using System.Diagnostics;
using System.IO;
using System.Windows;

namespace WinDFIR.UI.Services;

/// <summary>
/// Single entry for user-triggered shell opens (<c>Process.Start</c> with <c>UseShellExecute</c>).
/// Allows http(s), opening an existing file under a caller-verified base directory, or Explorer reveal for existing local file/directory paths.
/// </summary>
public static class ShellLaunchHelper
{
    private static string ExplorerExecutablePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");

    public static bool IsAllowedHttpOrHttps(Uri uri) =>
        uri.IsAbsoluteUri &&
        (string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
         string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase));

    /// <summary>Settings help / registry doc link: file under application base, else http(s) only.</summary>
    public static void TryOpenRegistryHelpLink(Uri? uri, string applicationBaseDirectory, Window? owner, string logContext = "Settings.RegistryHelpLink")
    {
        if (uri == null)
            return;

        var pathPart = uri.IsAbsoluteUri ? uri.AbsolutePath : uri.ToString();
        if (string.IsNullOrWhiteSpace(pathPart))
            return;

        pathPart = pathPart.TrimStart('/', '\\').Replace('/', Path.DirectorySeparatorChar);
        if (pathPart.Contains("..", StringComparison.Ordinal))
        {
            LogFailure(logContext, "path contains '..'");
            return;
        }

        string baseDir;
        string fullPath;
        try
        {
            baseDir = Path.GetFullPath(applicationBaseDirectory);
            fullPath = Path.GetFullPath(Path.Combine(baseDir, pathPart));
        }
        catch (Exception ex)
        {
            LogFailure(logContext, $"path normalization: {ex.Message}");
            NotifyFailure(owner, "Invalid path.", "Open link");
            return;
        }

        if (!fullPath.StartsWith(baseDir, StringComparison.OrdinalIgnoreCase))
        {
            LogFailure(logContext, "path escapes application base");
            return;
        }

        if (File.Exists(fullPath))
        {
            TryOpenExistingFileWithShell(fullPath, owner, logContext);
            return;
        }

        if (uri.IsAbsoluteUri && IsAllowedHttpOrHttps(uri))
        {
            TryOpenHttpOrHttps(uri, owner, logContext);
            return;
        }

        LogFailure(logContext, "no file under base and URI is not http(s)");
    }

    /// <summary>Explorer /select for an existing file, or open an existing directory.</summary>
    public static void TryRevealPathInExplorer(string? path, Window? owner, string logContext = "ProcessView.OpenDirectory")
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        path = path.Trim();

        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception ex)
        {
            LogFailure(logContext, $"normalization: {ex.Message}");
            NotifyFailure(owner, "Invalid path.", "Open in Explorer");
            return;
        }

        if (full.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
        {
            LogFailure(logContext, "invalid path characters");
            NotifyFailure(owner, "The path contains invalid characters.", "Open in Explorer");
            return;
        }

        if (!IsSafeShellLocalPath(full))
        {
            LogFailure(logContext, "disallowed characters in path");
            NotifyFailure(owner, "The path cannot be opened (invalid characters).", "Open in Explorer");
            return;
        }

        if (!File.Exists(ExplorerExecutablePath))
        {
            LogFailure(logContext, "explorer.exe missing");
            NotifyFailure(owner, "Could not find Windows Explorer.", "Open in Explorer");
            return;
        }

        try
        {
            if (File.Exists(full))
            {
                var args = "/select,\"" + full + "\"";
                Process.Start(new ProcessStartInfo(ExplorerExecutablePath) { UseShellExecute = true, Arguments = args });
                return;
            }

            if (Directory.Exists(full))
            {
                Process.Start(new ProcessStartInfo(ExplorerExecutablePath) { UseShellExecute = true, Arguments = "\"" + full + "\"" });
                return;
            }

            LogFailure(logContext, "path is not an existing file or directory");
            NotifyFailure(owner, "File or folder does not exist.", "Open in Explorer");
        }
        catch (Exception ex)
        {
            LogFailure(logContext, ex.Message);
            NotifyFailure(owner, "Could not open Explorer:\n" + ex.Message, "Open in Explorer");
        }
    }

    private static void TryOpenExistingFileWithShell(string fullPath, Window? owner, string logContext)
    {
        if (!IsSafeShellLocalPath(fullPath))
        {
            LogFailure(logContext, "disallowed characters in local path");
            NotifyFailure(owner, "The path cannot be opened (invalid characters).", "Open file");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(fullPath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            LogFailure(logContext, ex.Message);
            NotifyFailure(owner, "Could not open file:\n" + ex.Message, "Open file");
        }
    }

    private static void TryOpenHttpOrHttps(Uri uri, Window? owner, string logContext)
    {
        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            LogFailure(logContext, ex.Message);
            NotifyFailure(owner, "Could not open link:\n" + ex.Message, "Open link");
        }
    }

    private static bool IsSafeShellLocalPath(string fullPath)
    {
        if (string.IsNullOrEmpty(fullPath))
            return false;

        foreach (var c in fullPath)
        {
            if (c < 32 || c == '"' || c == '<' || c == '>' || c == '|')
                return false;
        }

        return true;
    }

    private static void LogFailure(string context, string detail) =>
        Debug.WriteLine("[" + context + "] Shell launch blocked or failed: " + detail);

    private static void NotifyFailure(Window? owner, string message, string caption)
    {
        if (owner != null)
            MessageBox.Show(owner, message, caption, MessageBoxButton.OK, MessageBoxImage.Warning);
        else
            MessageBox.Show(message, caption, MessageBoxButton.OK, MessageBoxImage.Warning);
    }
}
