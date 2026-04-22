using System.Security.Cryptography;
using System.Text;

namespace CloudDrive.Core.Configuration;

public static class CredentialManager
{
    private static readonly string CredentialPath = Path.Combine(
        AppSettings.GetDataDirectory(), "credentials.dat");

    public static bool HasPassword() => File.Exists(CredentialPath);

    public static void SavePassword(string password)
    {
        Directory.CreateDirectory(AppSettings.GetDataDirectory());
        var plainBytes = Encoding.UTF8.GetBytes(password);
        var encryptedBytes = ProtectedData.Protect(plainBytes, null, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(CredentialPath, encryptedBytes);
    }

    public static string? LoadPassword()
    {
        if (!HasPassword())
            return null;

        var encryptedBytes = File.ReadAllBytes(CredentialPath);
        var plainBytes = ProtectedData.Unprotect(encryptedBytes, null, DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(plainBytes);
    }

    public static void DeletePassword()
    {
        if (HasPassword())
            File.Delete(CredentialPath);
    }
}
