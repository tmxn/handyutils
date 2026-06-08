using MsgReader.Outlook;
using ReverseMarkdown;
using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using static System.Runtime.InteropServices.JavaScript.JSType;

namespace ObsidianEmailProcessor
{
    // --- 1. The Entry Point ---
    static class Program
    {
        [STAThread]
        static void Main()
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new ProcessorForm());
        }
    }
    // --- 2. The Main Form Interface ---
    public class ProcessorForm : Form
    {
        private NotifyIcon _trayIcon;
        private TextBox _logConsole;
        private FileSystemWatcher _watcher;

        // Configure your paths here
        private readonly string _downloadsPath = @"D:\Downloads";
        private readonly string _obsidianVaultPath = @"\\192.168.18.127\obsidian\Emails";

        public ProcessorForm()
        {
            InitializeUI();
            PrintAsciiArt();
            InitializeWatcher();
        }

        private void InitializeUI()
        {
            this.Text = "MSGToMD Terminal";
            this.Size = new Size(720, 500);

            // Create the text area for logs
            _logConsole = new TextBox
            {
                Multiline = true,
                Dock = DockStyle.Fill,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                BackColor = Color.FromArgb(26, 27, 38), // Deep Stormy Blue/Black
                ForeColor = Color.FromArgb(122, 162, 247), // Soft Neon Blue
                Font = new Font("Cascadia Code", 10, FontStyle.Regular), // Modern terminal font
            };
            this.Controls.Add(_logConsole);

            // Create the System Tray Icon
            _trayIcon = new NotifyIcon
            {
                Icon = SystemIcons.Information, // You can load a custom .ico file here
                Text = "Email Processor",
                Visible = true
            };
            _trayIcon.DoubleClick += TrayIcon_DoubleClick;

            // Create Context Menu for Tray (Right-Click -> Exit)
            var contextMenu = new ContextMenuStrip();
            contextMenu.Items.Add("Exit", null, MenuExit_Click);
            _trayIcon.ContextMenuStrip = contextMenu;

            // Override the default close behavior
            this.FormClosing += ProcessorForm_FormClosing;
        }

        private void PrintAsciiArt()
        {
            // The "Filled" Block Shadow Style
            string mainArt =
                @"                                                                         " + Environment.NewLine +
                @"                                                                         " + Environment.NewLine +
                @"                                                                         " + Environment.NewLine +
                @"     ███╗   ███╗ ███████╗  ██████╗  ████████╗        ███╗   ███╗ ██████╗ " + Environment.NewLine +
                @"     ████╗ ████║ ██╔════╝ ██╔════╝  ╚══██╔══╝  ████╗ ████╗ ████║ ██╔══██╗" + Environment.NewLine +
                @"     ██╔████╔██║ ███████╗ ██║  ███╗    ██║    ██╔═██╗██╔████╔██║ ██║  ██║" + Environment.NewLine +
                @"     ██║╚██╔╝██║ ╚════██║ ██║   ██║    ██║    ██║ ██║██║╚██╔╝██║ ██║  ██║" + Environment.NewLine +
                @"     ██║ ╚═╝ ██║ ███████║ ╚██████╔╝    ██║    ╚████╔╝██║ ╚═╝ ██║ ██████╔╝" + Environment.NewLine +
                @"     ╚═╝     ╚═╝ ╚══════╝  ╚═════╝     ╚═╝     ╚═══╝ ╚═╝     ╚═╝ ╚═════╝ " + Environment.NewLine +
                @"                                                                         " + Environment.NewLine +
                @"                                                                         " + Environment.NewLine;

            string subArt =
                @"                   --- Developed by Gemini ---" + Environment.NewLine;

            
            _logConsole.AppendText(mainArt);
            _logConsole.AppendText(subArt);
            _logConsole.AppendText(new string('-', 60) + Environment.NewLine);
        }

        private void InitializeWatcher()
        {
            if (!Directory.Exists(_obsidianVaultPath))
                Directory.CreateDirectory(_obsidianVaultPath);

            _watcher = new FileSystemWatcher(_downloadsPath);
            _watcher.Filter = "*.msg";
            _watcher.NotifyFilter = NotifyFilters.FileName | NotifyFilters.CreationTime;
            _watcher.Created += (s, e) => ProcessNewFile(e.FullPath);
            _watcher.Renamed += (s, e) => ProcessNewFile(e.FullPath);
            _watcher.EnableRaisingEvents = true;

            Log($"Watching for .msg files in: {_downloadsPath}");
        }

        // --- UI Event Handlers ---

        private void ProcessorForm_FormClosing(object sender, FormClosingEventArgs e)
        {
            // If the user clicks the 'X', cancel the close and hide the form instead
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                this.Hide();
            }
        }

        private void TrayIcon_DoubleClick(object sender, EventArgs e)
        {
            this.Show();
            this.WindowState = FormWindowState.Normal;
        }

        private void MenuExit_Click(object sender, EventArgs e)
        {
            _trayIcon.Visible = false; // Hide icon before exiting
            Application.Exit();
        }

        // Helper to safely write to the TextBox from background threads
        private void Log(string message)
        {
            if (this.InvokeRequired)
            {
                this.Invoke(new Action(() => Log(message)));
                return;
            }
            _logConsole.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}\r\n");
        }

        // --- File Processing Logic ---

        private void ProcessNewFile(string filePath)
        {
            Log($"New file detected: {Path.GetFileName(filePath)}");

            if (!WaitForFile(filePath))
            {
                Log("Failed to read file. Locked by another process.");
                return;
            }

            try
            {
                using var msg = new Storage.Message(filePath);

                string subject = msg.Subject ?? "No Subject";
                string sender = msg.Sender?.DisplayName ?? msg.Sender?.Email ?? "Unknown Sender";
                string date = msg.SentOn?.ToString("yyyy-MM-dd HH:mm") ?? DateTime.Now.ToString("yyyy-MM-dd HH:mm");
                string bodyText = string.IsNullOrWhiteSpace(msg.BodyHtml) ? msg.BodyText : new Converter().Convert(msg.BodyHtml);

                // Dispose the message object to release file locks
                msg.Dispose();

                string markdownContent = $@"---
tags: [email]
sender: {sender}
date: {date}
---
# {subject}

**From:** {sender}
**Date:** {date}

---

{bodyText}";

                string safeFilename = GetSafeFilename($"{date.Substring(0, 10)} - {subject}.md");
                string destinationPath = Path.Combine(_obsidianVaultPath, safeFilename);

                File.WriteAllText(destinationPath, markdownContent);
                Log($"Saved Markdown: {safeFilename}");

                if (DeleteFileWithRetries(filePath))
                    Log("Deleted original .msg file.");
                else
                    Log("Failed to delete original .msg file after multiple attempts.");
            }
            catch (Exception ex)
            {
                Log($"Error processing file: {ex.Message}");
            }
        }

        private bool WaitForFile(string fullPath)
        {
            int numTries = 0;
            while (numTries < 10)
            {
                try
                {
                    using (FileStream fs = new FileStream(fullPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None, 100))
                    {
                        fs.ReadByte();
                        return true;
                    }
                }
                catch (Exception)
                {
                    numTries++;
                    Thread.Sleep(500);
                }
            }
            return false;
        }

        // Helper method to delete a file with retries in case it's locked
        private static bool DeleteFileWithRetries(string fullPath)
        {
            int numTries = 0;
            while (true)
            {
                ++numTries;
                try
                {
                    bool success = false;
                    // Attempt to open the file exclusively
                    using (FileStream fs = new FileStream(fullPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None, 100))
                    {
                        fs.ReadByte();
                        success = true; // If we get here, the file is ready to be deleted
                    }

                    if (success)
                    {
                        File.Delete(fullPath);
                        break; // If we get here, the file was successfully deleted
                    }
                }
                catch (Exception)
                {
                    if (numTries > 10)
                    {
                        return false;
                    }
                    Thread.Sleep(500); // Wait 500ms before retrying
                }
            }
            return true;
        }

        private string GetSafeFilename(string filename)
        {
            string invalidChars = Regex.Escape(new string(Path.GetInvalidFileNameChars()));
            string invalidRegStr = string.Format(@"([{0}]*\.+$)|([{0}]+)", invalidChars);
            return Regex.Replace(filename, invalidRegStr, "_");
        }
    }
}