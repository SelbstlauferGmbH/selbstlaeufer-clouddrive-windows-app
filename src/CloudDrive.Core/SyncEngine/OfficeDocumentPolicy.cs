namespace CloudDrive.Core.SyncEngine;

public static class OfficeDocumentPolicy
{
    private static readonly HashSet<string> OfficeExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".doc",
        ".docx",
        ".docm",
        ".dot",
        ".dotx",
        ".dotm",
        ".xls",
        ".xlsx",
        ".xlsm",
        ".xlt",
        ".xltx",
        ".xltm",
        ".xlsb",
        ".ppt",
        ".pptx",
        ".pptm",
        ".pot",
        ".potx",
        ".potm",
        ".pps",
        ".ppsx",
        ".ppsm",
        ".vsd",
        ".vsdx",
        ".rtf"
    };

    public static bool IsOfficeDocumentPath(string path) =>
        OfficeExtensions.Contains(Path.GetExtension(path));
}
