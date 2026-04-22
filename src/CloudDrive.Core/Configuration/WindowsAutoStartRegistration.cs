using Microsoft.Win32;

namespace CloudDrive.Core.Configuration;

public sealed class WindowsAutoStartRegistration : IAutoStartRegistration
{
    internal const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    internal const string ValueName = "CloudDrive";
    public const string AutoStartArgument = "--autostart";

    private readonly IRunKeyStore _runKeyStore;
    private readonly Func<string?> _executablePathProvider;

    public WindowsAutoStartRegistration()
        : this(new CurrentUserRunKeyStore(), () => Environment.ProcessPath)
    {
    }

    internal WindowsAutoStartRegistration(IRunKeyStore runKeyStore, Func<string?> executablePathProvider)
    {
        _runKeyStore = runKeyStore;
        _executablePathProvider = executablePathProvider;
    }

    public bool IsRegistered()
    {
        var expectedCommand = TryBuildCommandLine();
        if (expectedCommand == null)
            return false;

        var registeredCommand = _runKeyStore.GetValue(ValueName);
        return string.Equals(registeredCommand, expectedCommand, StringComparison.OrdinalIgnoreCase);
    }

    public void Apply(bool enabled)
    {
        if (!enabled)
        {
            _runKeyStore.DeleteValue(ValueName);
            return;
        }

        var expectedCommand = TryBuildCommandLine();
        if (expectedCommand == null)
            return;

        var currentCommand = _runKeyStore.GetValue(ValueName);
        if (string.Equals(currentCommand, expectedCommand, StringComparison.OrdinalIgnoreCase))
            return;

        _runKeyStore.SetValue(ValueName, expectedCommand);
    }

    internal string? TryBuildCommandLine()
    {
        var executablePath = _executablePathProvider();
        if (string.IsNullOrWhiteSpace(executablePath))
            return null;

        var normalizedPath = Path.GetFullPath(executablePath);
        return $"\"{normalizedPath}\" {AutoStartArgument}";
    }

    internal interface IRunKeyStore
    {
        string? GetValue(string valueName);
        void SetValue(string valueName, string value);
        void DeleteValue(string valueName);
    }

    private sealed class CurrentUserRunKeyStore : IRunKeyStore
    {
        public string? GetValue(string valueName)
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(valueName) as string;
        }

        public void SetValue(string valueName, string value)
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            key.SetValue(valueName, value, RegistryValueKind.String);
        }

        public void DeleteValue(string valueName)
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            key?.DeleteValue(valueName, throwOnMissingValue: false);
        }
    }
}
