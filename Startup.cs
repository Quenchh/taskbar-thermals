using System.Diagnostics;
using System.Security;
using System.Security.Principal;
using System.Text;

namespace TaskbarThermals;

/// <summary>
/// Autostart through Task Scheduler rather than the Run key: a task with "highest privileges"
/// starts elevated at logon without a UAC prompt, which the CPU temperature sensor needs.
/// </summary>
internal static class Startup
{
    private const string TaskName = "TaskbarThermals";

    public static bool IsEnabled() => Schtasks("/Query", "/TN", TaskName) == 0;

    public static bool Disable() => Schtasks("/Delete", "/TN", TaskName, "/F") == 0;

    public static bool Enable()
    {
        string exe = Environment.ProcessPath!;
        string user = WindowsIdentity.GetCurrent().Name;
        string xml = $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo><Description>Taskbar Thermals - CPU/GPU temperature and usage on the taskbar</Description></RegistrationInfo>
              <Triggers>
                <LogonTrigger>
                  <Enabled>true</Enabled>
                  <UserId>{SecurityElement.Escape(user)}</UserId>
                  <Delay>PT5S</Delay>
                </LogonTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <UserId>{SecurityElement.Escape(user)}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>HighestAvailable</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <AllowHardTerminate>true</AllowHardTerminate>
                <StartWhenAvailable>false</StartWhenAvailable>
                <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
                <IdleSettings><StopOnIdleEnd>false</StopOnIdleEnd><RestartOnIdle>false</RestartOnIdle></IdleSettings>
                <AllowStartOnDemand>true</AllowStartOnDemand>
                <Enabled>true</Enabled>
                <Hidden>false</Hidden>
                <RunOnlyIfIdle>false</RunOnlyIfIdle>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>4</Priority>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>{SecurityElement.Escape(exe)}</Command>
                  <WorkingDirectory>{SecurityElement.Escape(Path.GetDirectoryName(exe))}</WorkingDirectory>
                </Exec>
              </Actions>
            </Task>
            """;

        string tmp = Path.Combine(Path.GetTempPath(), $"taskbar-thermals-task-{Guid.NewGuid():N}.xml");
        try
        {
            File.WriteAllText(tmp, xml, Encoding.Unicode);
            return Schtasks("/Create", "/TN", TaskName, "/XML", tmp, "/F") == 0;
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    private static int Schtasks(params string[] args)
    {
        var psi = new ProcessStartInfo("schtasks.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var p = Process.Start(psi)!;
        p.StandardOutput.ReadToEnd();
        p.StandardError.ReadToEnd();
        p.WaitForExit();
        return p.ExitCode;
    }
}
