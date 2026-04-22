namespace CloudDrive.Core.SyncRoot;

public record ShellNamespaceRegistrationInfo(
    string Clsid,
    string? RegistrationValue,
    string? DisplayName,
    string? TargetFolderPath)
{
    public string Identifier => DisplayName ?? RegistrationValue ?? Clsid;
}
