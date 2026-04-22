using CloudDrive.Core.Configuration;
using Shouldly;

namespace CloudDrive.Core.Tests.Configuration;

public class WindowsAutoStartRegistrationTests
{
    [Fact]
    public void Apply_WhenEnabled_WritesQuotedExecutableWithAutostartArgument()
    {
        var store = new FakeRunKeyStore();
        var registration = CreateRegistration(store, @"C:\Program Files\CloudDrive\CloudDrive.App.exe");

        registration.Apply(enabled: true);

        store.Values[WindowsAutoStartRegistration.ValueName]
            .ShouldBe(@"""C:\Program Files\CloudDrive\CloudDrive.App.exe"" --autostart");
    }

    [Fact]
    public void Apply_WhenDisabled_RemovesRegisteredValue()
    {
        var store = new FakeRunKeyStore();
        store.Values[WindowsAutoStartRegistration.ValueName] = "existing";
        var registration = CreateRegistration(store, @"C:\CloudDrive.App.exe");

        registration.Apply(enabled: false);

        store.Values.ContainsKey(WindowsAutoStartRegistration.ValueName).ShouldBeFalse();
    }

    [Fact]
    public void Apply_WhenCommandAlreadyMatches_DoesNotRewriteRunValue()
    {
        var store = new FakeRunKeyStore();
        var registration = CreateRegistration(store, @"C:\CloudDrive.App.exe");
        var expectedCommand = registration.TryBuildCommandLine();
        expectedCommand.ShouldNotBeNull();
        store.Values[WindowsAutoStartRegistration.ValueName] = expectedCommand;

        registration.Apply(enabled: true);

        store.SetValueCalls.ShouldBe(0);
    }

    [Fact]
    public void IsRegistered_ReturnsTrueOnlyForExactCurrentCommand()
    {
        var store = new FakeRunKeyStore();
        var registration = CreateRegistration(store, @"C:\CloudDrive.App.exe");

        registration.IsRegistered().ShouldBeFalse();

        store.Values[WindowsAutoStartRegistration.ValueName] = @"""C:\CloudDrive.App.exe""";
        registration.IsRegistered().ShouldBeFalse();

        store.Values[WindowsAutoStartRegistration.ValueName] = @"""C:\CloudDrive.App.exe"" --autostart";
        registration.IsRegistered().ShouldBeTrue();
    }

    [Fact]
    public void Apply_WhenOnlyCommandCasingDiffers_DoesNotRewriteRunValue()
    {
        var store = new FakeRunKeyStore();
        store.Values[WindowsAutoStartRegistration.ValueName] = @"""c:\clouddrive.app.exe"" --AUTOSTART";
        var registration = CreateRegistration(store, @"C:\CloudDrive.App.exe");

        registration.Apply(enabled: true);

        store.SetValueCalls.ShouldBe(0);
    }

    private static WindowsAutoStartRegistration CreateRegistration(
        WindowsAutoStartRegistration.IRunKeyStore store,
        string executablePath)
    {
        return new WindowsAutoStartRegistration(store, () => executablePath);
    }

    private sealed class FakeRunKeyStore : WindowsAutoStartRegistration.IRunKeyStore
    {
        public Dictionary<string, string> Values { get; } = [];
        public int SetValueCalls { get; private set; }

        public string? GetValue(string valueName)
        {
            return Values.TryGetValue(valueName, out var value) ? value : null;
        }

        public void SetValue(string valueName, string value)
        {
            SetValueCalls++;
            Values[valueName] = value;
        }

        public void DeleteValue(string valueName)
        {
            Values.Remove(valueName);
        }
    }
}
