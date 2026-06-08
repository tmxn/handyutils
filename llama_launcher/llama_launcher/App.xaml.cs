using System;
using System.IO;
using System.Diagnostics;
using System.Windows;
using System.Windows.Shell;
using System.Collections.Generic;

namespace llama_launcher
{
    public partial class App : Application
    {
        // Configuration: Change this to your target directory
        private string ScriptDirectory = @"C:\Users\tmxn\llamalaunch";


        protected override void OnStartup(StartupEventArgs e)
        {
            // If arguments are passed, it means an item was clicked in the Jump List
            if (e.Args.Length > 0)
            {
                LaunchScript(e.Args[0]);
                Shutdown();
                return;
            }

            // Otherwise, refresh the Jump List and stay open (or close)
            RefreshJumpList();

            // Keep the app alive if you want it pinned, or shutdown if just updating
            Shutdown(); 
        }

        private void RefreshJumpList()
        {
            JumpList myJumpList = new JumpList();
            JumpList.SetJumpList(Application.Current, myJumpList);

            if (!Directory.Exists(ScriptDirectory)) return;

            string[] scripts = Directory.GetFiles(ScriptDirectory, "*.ps1");

            foreach (string scriptPath in scripts)
            {
                JumpTask task = new JumpTask
                {
                    Title = Path.GetFileNameWithoutExtension(scriptPath),
                    Arguments = $"\"{scriptPath}\"",
                    Description = $"Launch {Path.GetFileName(scriptPath)}",
                    // Points back to this EXE
                    ApplicationPath = Process.GetCurrentProcess().MainModule.FileName,
                    IconResourcePath = "powershell.exe",
                    WorkingDirectory = ScriptDirectory
                };

                myJumpList.JumpItems.Add(task);
            }

            myJumpList.Apply();
        }

        private void LaunchScript(string scriptPath)
        {
            ProcessStartInfo psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                // -NoExit keeps the window open so logs can be viewed
                Arguments = $"-NoExit -ExecutionPolicy Bypass -File \"{scriptPath}\"",
                UseShellExecute = true,
                CreateNoWindow = false
            };

            Process.Start(psi);
        }
    }
}