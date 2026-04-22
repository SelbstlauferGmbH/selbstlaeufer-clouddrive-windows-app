using CloudDrive.Core.Localization;

namespace CloudDrive.Core.SyncRoot;

/// <summary>
/// Represents the four visual states shown to the user in Windows Explorer.
/// Each state maps to a specific combination of cfapi calls.
/// </summary>
public enum ExplorerVisualState
{
    Connected,
    Disconnected
}

public static class ExplorerVisualStateInfo
{
    /// <summary>
    /// Returns the human-readable status message for a given state, or null for Connected (cleared).
    /// </summary>
    public static string? GetStatusMessage(ExplorerVisualState state) => state switch
    {
        ExplorerVisualState.Connected => null,
        ExplorerVisualState.Disconnected => AppLocalizer.Instance.GetString("ExplorerStatus_Disconnected"),
        _ => null
    };

    /// <summary>
    /// Returns the icon resource string for a given state (Layer 3 — experimental).
    /// </summary>
    public static string GetIconResource(ExplorerVisualState state) => state switch
    {
        ExplorerVisualState.Connected => @"%SystemRoot%\system32\imageres.dll,-1043",
        ExplorerVisualState.Disconnected => @"%SystemRoot%\system32\imageres.dll,-1405",
        _ => @"%SystemRoot%\system32\imageres.dll,-1043"
    };
}
