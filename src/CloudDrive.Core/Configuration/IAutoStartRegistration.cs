namespace CloudDrive.Core.Configuration;

public interface IAutoStartRegistration
{
    bool IsRegistered();
    void Apply(bool enabled);
}
