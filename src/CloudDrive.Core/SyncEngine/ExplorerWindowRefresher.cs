using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace CloudDrive.Core.SyncEngine;

internal sealed class ExplorerWindowRefresher
{
    private readonly ILogger _logger;

    public ExplorerWindowRefresher(ILogger logger)
    {
        _logger = logger;
    }

    public void RefreshDirectory(string localDirectoryPath)
    {
        if (string.IsNullOrWhiteSpace(localDirectoryPath))
            return;

        var normalizedPath = NormalizePath(localDirectoryPath);
        _logger.LogInformation("Explorer refresh requested for {Path}", normalizedPath);
        NotifyShellDirectoryChanged(normalizedPath);

        object? shellApplication = null;
        object? shellWindows = null;
        var refreshedWindowCount = 0;

        try
        {
            var shellType = Type.GetTypeFromProgID("Shell.Application");
            if (shellType == null)
                return;

            shellApplication = Activator.CreateInstance(shellType);
            if (shellApplication == null)
                return;

            shellWindows = InvokeMember(shellApplication, "Windows", BindingFlags.InvokeMethod);
            if (shellWindows == null)
                return;

            var count = Convert.ToInt32(
                InvokeMember(shellWindows, "Count", BindingFlags.GetProperty) ?? 0,
                CultureInfo.InvariantCulture);

            for (int i = 0; i < count; i++)
            {
                object? window = null;
                try
                {
                    window = InvokeMember(shellWindows, "Item", BindingFlags.InvokeMethod, [i]);
                    if (window == null)
                        continue;

                    var locationUrl = InvokeMember(window, "LocationURL", BindingFlags.GetProperty) as string;
                    if (!TryGetLocalPath(locationUrl, out var windowPath))
                        continue;

                    if (!NormalizePath(windowPath).Equals(normalizedPath, StringComparison.OrdinalIgnoreCase))
                        continue;

                    InvokeMember(window, "Refresh", BindingFlags.InvokeMethod);
                    refreshedWindowCount++;
                    _logger.LogInformation("Explorer window refreshed for {Path}", normalizedPath);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to refresh Explorer window {Index} for {Path}", i, normalizedPath);
                }
                finally
                {
                    ReleaseComObject(window);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to refresh Explorer for {Path}", normalizedPath);
        }
        finally
        {
            ReleaseComObject(shellWindows);
            ReleaseComObject(shellApplication);
        }

        _logger.LogInformation("Explorer refresh completed for {Path} (windowsRefreshed={Count})",
            normalizedPath, refreshedWindowCount);
    }

    public void NotifyItemChanged(string localPath)
    {
        if (string.IsNullOrWhiteSpace(localPath))
            return;

        var normalizedPath = NormalizePath(localPath);
        _logger.LogInformation("Explorer item update notified for {Path}", normalizedPath);
        SHChangeNotify(SHCNE_UPDATEITEM, SHCNF_PATHW | SHCNF_FLUSHNOWAIT, normalizedPath, IntPtr.Zero);
    }

    public void NotifyDirectoryChanged(string localDirectoryPath)
    {
        if (string.IsNullOrWhiteSpace(localDirectoryPath))
            return;

        var normalizedPath = NormalizePath(localDirectoryPath);
        _logger.LogInformation("Explorer directory update notified for {Path}", normalizedPath);
        NotifyShellDirectoryChanged(normalizedPath);
    }

    private static bool TryGetLocalPath(string? locationUrl, out string localPath)
    {
        localPath = string.Empty;

        if (string.IsNullOrWhiteSpace(locationUrl))
            return false;

        if (Uri.TryCreate(locationUrl, UriKind.Absolute, out var uri) &&
            uri.IsFile &&
            !string.IsNullOrWhiteSpace(uri.LocalPath))
        {
            localPath = uri.LocalPath;
            return true;
        }

        if (Path.IsPathRooted(locationUrl))
        {
            localPath = locationUrl;
            return true;
        }

        return false;
    }

    private static string NormalizePath(string path)
    {
        return Path.GetFullPath(path)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static object? InvokeMember(object target, string memberName, BindingFlags bindingFlags, object?[]? args = null)
    {
        return target.GetType().InvokeMember(memberName, bindingFlags, null, target, args);
    }

    private static void ReleaseComObject(object? comObject)
    {
        if (comObject != null && Marshal.IsComObject(comObject))
            Marshal.ReleaseComObject(comObject);
    }

    private static void NotifyShellDirectoryChanged(string localDirectoryPath)
    {
        SHChangeNotify(SHCNE_UPDATEDIR, SHCNF_PATHW | SHCNF_FLUSHNOWAIT, localDirectoryPath, IntPtr.Zero);
    }

    private const uint SHCNE_UPDATEITEM = 0x00002000;
    private const uint SHCNE_UPDATEDIR = 0x00001000;
    private const uint SHCNF_PATHW = 0x0005;
    private const uint SHCNF_FLUSHNOWAIT = 0x2000;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern void SHChangeNotify(uint wEventId, uint uFlags, string dwItem1, IntPtr dwItem2);
}
