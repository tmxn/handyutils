using System.Diagnostics;
using System.Text;

namespace HeadlessGpuKeeper;

/// <summary>
/// Registers the keeper to start with the user's session.
///
/// Why a task and not an HKCU\...\Run entry: Run entries and MSIX StartupTasks are
/// launched by the shell once Explorer is up, whereas a logon-triggered task is started
/// by the Task Scheduler service off the logon event itself, so it generally wins the
/// race against the apps we are pinning. Windows makes no ordering guarantee between
/// the two, which is why the keeper also sweeps immediately on startup — losing the
/// race costs milliseconds, not a stale entry.
///
/// Two triggers, because a session can come up without anyone sitting at the machine.
/// After a Windows Update restart, ARSO signs the user back in and then locks: apps
/// autostart against a locked screen, long before the user unlocks. That is a real
/// logon so LogonTrigger covers it, but SessionUnlock is cheap insurance for the paths
/// it does not (and for a session resumed after a long lock). MultipleInstancesPolicy
/// is IgnoreNew and the app holds a single-instance mutex, so a redundant fire is a
/// no-op rather than a second keeper.
/// </summary>
public static class AutoStart
{
    public const string TaskName = "HeadlessGPUKeeper";

    public static bool IsInstalled()
        => RunSchTasks($"/Query /TN \"{TaskName}\"").ExitCode == 0;

    public static (bool Ok, string Message) Install()
    {
        string exePath = Environment.ProcessPath ?? "";
        if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
        {
            return (false, "Could not determine the keeper's own executable path.");
        }

        string xmlPath = Path.Combine(Path.GetTempPath(), $"{TaskName}.task.xml");

        try
        {
            // schtasks /XML expects UTF-16; writing UTF-8 fails with a bare "invalid XML".
            File.WriteAllText(xmlPath, BuildTaskXml(exePath), Encoding.Unicode);

            var result = RunSchTasks($"/Create /TN \"{TaskName}\" /XML \"{xmlPath}\" /F");
            return result.ExitCode == 0
                ? (true, "Registered to start at logon.")
                : (false, string.IsNullOrWhiteSpace(result.Output) ? "schtasks refused the task." : result.Output.Trim());
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
        finally
        {
            try { if (File.Exists(xmlPath)) File.Delete(xmlPath); } catch { }
        }
    }

    public static (bool Ok, string Message) Uninstall()
    {
        var result = RunSchTasks($"/Delete /TN \"{TaskName}\" /F");
        return result.ExitCode == 0
            ? (true, "Removed from startup.")
            : (false, string.IsNullOrWhiteSpace(result.Output) ? "schtasks refused the delete." : result.Output.Trim());
    }

    static (int ExitCode, string Output) RunSchTasks(string arguments)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });

            if (process is null) return (-1, "Could not start schtasks.exe.");

            string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            process.WaitForExit(15000);
            return (process.HasExited ? process.ExitCode : -1, output);
        }
        catch (Exception ex)
        {
            return (-1, ex.Message);
        }
    }

    static string BuildTaskXml(string exePath)
    {
        string user = $"{Environment.UserDomainName}\\{Environment.UserName}";
        string workingDirectory = Path.GetDirectoryName(exePath) ?? "";

        return $"""
        <?xml version="1.0" encoding="UTF-16"?>
        <Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
          <RegistrationInfo>
            <Description>Keeps GPU preferences pinned for apps that install to version-stamped paths, and keeps the dGPU awake for local inference.</Description>
            <URI>\{TaskName}</URI>
          </RegistrationInfo>
          <Triggers>
            <LogonTrigger>
              <Enabled>true</Enabled>
              <UserId>{Escape(user)}</UserId>
              <Delay>PT0S</Delay>
            </LogonTrigger>
            <SessionStateChangeTrigger>
              <Enabled>true</Enabled>
              <UserId>{Escape(user)}</UserId>
              <StateChange>SessionUnlock</StateChange>
            </SessionStateChangeTrigger>
          </Triggers>
          <Principals>
            <Principal id="Author">
              <UserId>{Escape(user)}</UserId>
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
            <Hidden>false</Hidden>
            <RunOnlyIfIdle>false</RunOnlyIfIdle>
            <DisallowStartOnRemoteAppSession>false</DisallowStartOnRemoteAppSession>
            <UseUnifiedSchedulingEngine>true</UseUnifiedSchedulingEngine>
            <WakeToRun>false</WakeToRun>
            <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
            <Priority>4</Priority>
            <RestartOnFailure>
              <Interval>PT1M</Interval>
              <Count>3</Count>
            </RestartOnFailure>
          </Settings>
          <Actions Context="Author">
            <Exec>
              <Command>{Escape(exePath)}</Command>
              <WorkingDirectory>{Escape(workingDirectory)}</WorkingDirectory>
            </Exec>
          </Actions>
        </Task>
        """;
    }

    static string Escape(string value)
        => value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}
