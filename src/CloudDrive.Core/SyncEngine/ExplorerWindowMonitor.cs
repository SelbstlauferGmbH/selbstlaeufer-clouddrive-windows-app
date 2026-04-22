using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace CloudDrive.Core.SyncEngine;

internal sealed class ExplorerWindowMonitor
{
    private readonly string _syncRootPath;
    private readonly ILogger<ExplorerWindowMonitor> _logger;

    public ExplorerWindowMonitor(string syncRootPath, ILogger<ExplorerWindowMonitor> logger)
    {
        _syncRootPath = NormalizePath(syncRootPath);
        _logger = logger;
    }

    public bool HasOpenWindowForSyncRoot()
    {
        object? shellApplication = null;
        object? shellWindows = null;

        try
        {
            var shellType = Type.GetTypeFromProgID("Shell.Application");
            if (shellType == null)
                return false;

            shellApplication = Activator.CreateInstance(shellType);
            if (shellApplication == null)
                return false;

            shellWindows = InvokeMember(shellApplication, "Windows", BindingFlags.InvokeMethod);
            if (shellWindows == null)
                return false;

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
                    if (TryGetLocalPath(locationUrl, out var localPath) && IsInSyncRoot(localPath))
                        return true;
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to inspect Explorer window {Index}", i);
                }
                finally
                {
                    ReleaseComObject(window);
                }
            }

            return false;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to inspect Explorer windows");
            return false;
        }
        finally
        {
            ReleaseComObject(shellWindows);
            ReleaseComObject(shellApplication);
        }
    }

    private bool IsInSyncRoot(string localPath)
    {
        var normalized = NormalizePath(localPath);
        return normalized.Equals(_syncRootPath, StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith(_syncRootPath + "\\", StringComparison.OrdinalIgnoreCase);
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
        var fullPath = Path.GetFullPath(path);
        return fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
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
}
