using System.Security;
using System.Security.Principal;

namespace CloudDrive.Core.Watchdog;

public sealed class WatchdogScheduledTaskXmlBuilder
{
    public string Build(string executablePath, DateTimeOffset startBoundary)
    {
        var normalizedExecutablePath = Path.GetFullPath(executablePath);
        var workingDirectory = Path.GetDirectoryName(normalizedExecutablePath) ?? AppContext.BaseDirectory;
        var userId = WindowsIdentity.GetCurrent().Name;
        var escapedExecutable = Escape(normalizedExecutablePath);
        var escapedWorkingDirectory = Escape(workingDirectory);
        var escapedUserId = Escape(userId);
        var escapedAuthor = Escape("Selbstläufer CloudDrive");
        var escapedDescription = Escape("Runs the Selbstläufer CloudDrive watchdog silently and keeps Explorer status up to date.");
        var boundary = Escape(startBoundary.ToString("s"));

        return $$"""
<?xml version="1.0" encoding="UTF-16"?>
<Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <RegistrationInfo>
    <Author>{{escapedAuthor}}</Author>
    <Description>{{escapedDescription}}</Description>
  </RegistrationInfo>
  <Triggers>
    <LogonTrigger>
      <Enabled>true</Enabled>
      <UserId>{{escapedUserId}}</UserId>
    </LogonTrigger>
    <CalendarTrigger>
      <StartBoundary>{{boundary}}</StartBoundary>
      <Enabled>true</Enabled>
      <ScheduleByDay>
        <DaysInterval>1</DaysInterval>
      </ScheduleByDay>
      <Repetition>
        <Interval>PT1M</Interval>
        <Duration>P1D</Duration>
        <StopAtDurationEnd>false</StopAtDurationEnd>
      </Repetition>
    </CalendarTrigger>
  </Triggers>
  <Principals>
    <Principal id="Author">
      <UserId>{{escapedUserId}}</UserId>
      <LogonType>InteractiveToken</LogonType>
      <RunLevel>LeastPrivilege</RunLevel>
    </Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <AllowHardTerminate>false</AllowHardTerminate>
    <StartWhenAvailable>true</StartWhenAvailable>
    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
    <IdleSettings>
      <StopOnIdleEnd>false</StopOnIdleEnd>
      <RestartOnIdle>false</RestartOnIdle>
    </IdleSettings>
    <AllowStartOnDemand>true</AllowStartOnDemand>
    <Enabled>true</Enabled>
    <Hidden>true</Hidden>
    <RunOnlyIfIdle>false</RunOnlyIfIdle>
    <DisallowStartOnRemoteAppSession>false</DisallowStartOnRemoteAppSession>
    <UseUnifiedSchedulingEngine>true</UseUnifiedSchedulingEngine>
    <WakeToRun>false</WakeToRun>
    <ExecutionTimeLimit>PT5M</ExecutionTimeLimit>
    <Priority>7</Priority>
  </Settings>
  <Actions Context="Author">
    <Exec>
      <Command>{{escapedExecutable}}</Command>
      <WorkingDirectory>{{escapedWorkingDirectory}}</WorkingDirectory>
    </Exec>
  </Actions>
</Task>
""";
    }

    private static string Escape(string value) => SecurityElement.Escape(value) ?? string.Empty;
}
