namespace CloudDrive.Core.Tests.Infrastructure;

/// <summary>
/// Loads key=value pairs from a .env file into <see cref="Environment.SetEnvironmentVariable"/>.
/// Walks up from the test assembly directory to find the .env file at the repository root.
/// Already-set environment variables take precedence (CI can override via real env vars).
/// </summary>
public static class DotEnvLoader
{
    /// <summary>
    /// Loads the .env file closest to the repository root.
    /// Call once at the start of the test session.
    /// </summary>
    public static void Load()
    {
        var envPath = FindEnvFile();
        if (envPath == null) return;

        foreach (var line in File.ReadAllLines(envPath))
        {
            var trimmed = line.Trim();

            // Skip empty lines and comments
            if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith('#'))
                continue;

            var eqIndex = trimmed.IndexOf('=');
            if (eqIndex < 0) continue;

            var key = trimmed[..eqIndex].Trim();
            var value = trimmed[(eqIndex + 1)..].Trim();

            // Strip optional surrounding quotes
            if (value.Length >= 2 &&
                ((value.StartsWith('"') && value.EndsWith('"')) ||
                 (value.StartsWith('\'') && value.EndsWith('\''))))
            {
                value = value[1..^1];
            }

            // Don't overwrite existing env vars (CI takes precedence)
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(key)))
            {
                Environment.SetEnvironmentVariable(key, value);
            }
        }
    }

    private static string? FindEnvFile()
    {
        // Start from the test assembly's directory and walk up
        var dir = AppContext.BaseDirectory;
        while (dir != null)
        {
            var candidate = Path.Combine(dir, ".env");
            if (File.Exists(candidate))
                return candidate;

            // Also check for .sln file as a marker for the repo root
            if (Directory.GetFiles(dir, "*.sln").Length > 0)
            {
                // We're at the repo root — if no .env here, stop looking
                return File.Exists(candidate) ? candidate : null;
            }

            dir = Path.GetDirectoryName(dir);
        }

        return null;
    }
}
