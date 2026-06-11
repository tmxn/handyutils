using Renci.SshNet;
using System;
using System.Threading;
using System.Windows.Forms;
using static System.Net.Mime.MediaTypeNames;

namespace LinuxBatteryMonitor
{
    static class Program
    {
        // --- CONFIGURATION ---
        private const string LinuxUser = "user";
        private const string LinuxPassword = "password";
        private const string LinuxIP = "192.168.0.1";
        private const int CheckIntervalMinutes = 30;
        // ---------------------

        [STAThread]
        static void Main()
        {
            ApplicationConfiguration.Initialize();

            System.Diagnostics.Debug.WriteLine($"[{DateTime.Now:HH:mm:ss}] [Main] Initializing background monitor thread...");

            // Run the monitoring loop on a background thread so the OS doesn't think the app is frozen
            Thread monitorThread = new Thread(BatteryMonitorLoop)
            {
                IsBackground = true
            };
            monitorThread.Start();

            System.Diagnostics.Debug.WriteLine($"[{DateTime.Now:HH:mm:ss}] [Main] Background thread started. Running application messaging loop...");

            // Keeps the background process alive without showing an empty application window
            System.Windows.Forms.Application.Run();
        }

        private static void BatteryMonitorLoop()
        {
            // Command to fetch capacity and status separated by a newline
            string commandText = "cat /sys/class/power_supply/BAT0/capacity && cat /sys/class/power_supply/BAT0/status";

            System.Diagnostics.Debug.WriteLine($"[{DateTime.Now:HH:mm:ss}] [Worker] Worker loop active. Interval set to {CheckIntervalMinutes} minutes.");

            while (true)
            {
                try
                {
                    System.Diagnostics.Debug.WriteLine($"[{DateTime.Now:HH:mm:ss}] [Worker] Initiating SSH connection connection sequence to {LinuxIP}...");

                    using (var client = new SshClient(LinuxIP, LinuxUser, LinuxPassword))
                    {
                        // Bypass host key validation strictly for local network convenience
                        client.HostKeyReceived += (sender, e) =>
                        {
                            System.Diagnostics.Debug.WriteLine($"[{DateTime.Now:HH:mm:ss}] [Worker] Host key received. Trust verified automatically.");
                            e.CanTrust = true;
                        };

                        client.Connect();
                        System.Diagnostics.Debug.WriteLine($"[{DateTime.Now:HH:mm:ss}] [Worker] SSH Session established successfully.");

                        System.Diagnostics.Debug.WriteLine($"[{DateTime.Now:HH:mm:ss}] [Worker] Creating remote bash command: \"{commandText}\"");
                        using (var command = client.CreateCommand(commandText))
                        {
                            System.Diagnostics.Debug.WriteLine($"[{DateTime.Now:HH:mm:ss}] [Worker] Dispatching command execution payload...");
                            string result = command.Execute();

                            System.Diagnostics.Debug.WriteLine($"[{DateTime.Now:HH:mm:ss}] [Worker] Raw response received. Parsing token strings...");

                            // Split the output lines (Line 1: Capacity, Line 2: Status)
                            string[] lines = result.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);

                            if (lines.Length >= 2)
                            {
                                int.TryParse(lines[0].Trim(), out int batteryLevel);
                                string batteryStatus = lines[1].Trim();

                                System.Diagnostics.Debug.WriteLine($"[{DateTime.Now:HH:mm:ss}] [Worker] Diagnostics parsed successfully -> Charge Level: {batteryLevel}%, Status: {batteryStatus}");

                                // Condition Check: Trigger alert if below 100% OR if it is discharging
                                if (batteryLevel < 100 || !batteryStatus.Equals("Charging", StringComparison.OrdinalIgnoreCase))
                                {
                                    System.Diagnostics.Debug.WriteLine($"[{DateTime.Now:HH:mm:ss}] [Worker] Alert condition triggered! Compiling desktop alert notification window...");

                                    string alertMessage = $"Your Linux Laptop needs attention!\n\n" +
                                                          $"Charge Level: {batteryLevel}%\n" +
                                                          $"Status: {batteryStatus}";

                                    // Display a native Windows MessageBox that forces focus to the front
                                    MessageBox.Show(
                                        alertMessage,
                                        "Laptop Battery Alert",
                                        MessageBoxButtons.OK,
                                        MessageBoxIcon.Warning,
                                        MessageBoxDefaultButton.Button1,
                                        MessageBoxOptions.DefaultDesktopOnly // Forces it to pop over all open apps
                                    );

                                    System.Diagnostics.Debug.WriteLine($"[{DateTime.Now:HH:mm:ss}] [Worker] Desktop alert window dismissed by user.");
                                }
                                else
                                {
                                    System.Diagnostics.Debug.WriteLine($"[{DateTime.Now:HH:mm:ss}] [Worker] Laptop battery is fully charged and connected to power supply. No alert required.");
                                }
                            }
                            else
                            {
                                System.Diagnostics.Debug.WriteLine($"[{DateTime.Now:HH:mm:ss}] [Worker] Malformed execution response. Expected 2 data streams, but received {lines.Length}. Payload data: \"{result}\"");
                            }
                        }

                        client.Disconnect();
                        System.Diagnostics.Debug.WriteLine($"[{DateTime.Now:HH:mm:ss}] [Worker] SSH session closed cleanly.");
                    }
                }
                catch (Exception ex)
                {
                    // If it fails to connect (e.g. laptop closed), it will silently log and try again next interval
                    System.Diagnostics.Debug.WriteLine($"[{DateTime.Now:HH:mm:ss}] [Worker-Exception] Connection or execution pipeline broke: {ex.Message}");
                }

                System.Diagnostics.Debug.WriteLine($"[{DateTime.Now:HH:mm:ss}] [Worker] Sleep loop initiated. Thread resting for next {CheckIntervalMinutes} minutes.");

                // Sleep for 30 minutes before running the next check
                Thread.Sleep(TimeSpan.FromMinutes(CheckIntervalMinutes));
            }
        }
    }
}