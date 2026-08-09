namespace Subspace;

public static class Program
{
    private static bool _exiting;
    private static bool _escPrimed;
    private static DateTime _lastEsc = DateTime.MinValue;
    private static VhdHandler? _vhd;
    private static readonly HashSet<string> MediaExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".flv", ".webm", ".m4v", ".mpg", ".mpeg", ".ts", ".3gp",
        ".mp3", ".wav", ".flac", ".aac", ".ogg", ".m4a", ".opus", ".wma", ".ac3", ".aiff"
    };

    [STAThread]
    public static int Main()
    {
        try
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            Console.CursorVisible = false;
            ConsoleEvents.EnableQuickEdit(false);
        }
        catch { }

        Config config;
        try { config = Config.Load(); }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"Error: {ex.Message}");
            Console.ResetColor();
            Pause();
            return 1;
        }

        if (!PromptPin(config.Pin))
        {
            Console.Clear();
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("Incorrect PIN. Exiting.");
            Console.ResetColor();
            Pause();
            return 1;
        }

        using var hook = new KeyboardHook();
        hook.DoubleEscPressed += OnDoubleEsc;
        hook.Install();

        try { _vhd = new VhdHandler(config.VhdxPath); }
        catch (Exception ex)
        {
            Console.Clear();
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"Failed to open VHDX:\n{ex.Message}");
            Console.ResetColor();
            Pause();
            return 1;
        }

        using (_vhd)
        {
            BrowserLoop();
        }

        Console.Clear();
        Console.CursorVisible = true;
        return 0;
    }

    private static void OnDoubleEsc()
    {
        _exiting = true;
        Console.Clear();
        Console.CursorVisible = true;
        Environment.Exit(0);
    }

    private static bool PromptPin(string correctPin)
    {
        Console.Clear();
        DrawTitle();
        Console.WriteLine();
        Console.WriteLine("  Enter PIN: ");
        var entered = "";
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) break;
            if (key.Key == ConsoleKey.Backspace && entered.Length > 0) entered = entered[..^1];
            else if (char.IsDigit(key.KeyChar) && entered.Length < 12) entered += key.KeyChar;

            var y = Console.CursorTop;
            Console.SetCursorPosition(13, y);
            Console.Write(new string('•', entered.Length) + new string(' ', 12 - entered.Length));

            if (entered.Length == correctPin.Length && entered == correctPin)
                return true;
        }
        return entered == correctPin;
    }

    private static void DrawTitle()
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine(
            " _                         \n" +
            " ____  _| |__ ____ __  __ _ __ ___ \n" +
            "(_-< || | '_ (_-< '_ \\/ _` / _/ -_)\n" +
            "/__/\\_,_|_.__/__/ .__/\\__,_\\__\\___|\n" +
            "                |_|                ");
        Console.ResetColor();
    }

    private static void BrowserLoop()
    {
        var selected = 0;

        while (!_exiting)
        {
            var entries = _vhd!.ListEntries();
            DrawBrowser(entries, ref selected);
            if (entries.Count == 0)
            {
                Console.SetCursorPosition(0, Console.WindowHeight - 1);
                Console.ReadKey(true);
                continue;
            }

            var key = Console.ReadKey(true).Key;
            switch (key)
            {
                case ConsoleKey.UpArrow: selected = (selected - 1 + entries.Count) % entries.Count; break;
                case ConsoleKey.DownArrow: selected = (selected + 1) % entries.Count; break;
                case ConsoleKey.Enter:
                    var e = entries[selected];
                    if (e.IsDirectory) { _vhd.TryEnter(e.Name); selected = 0; }
                    else if (IsMedia(e.Name)) { MediaMenu(e); }
                    break;
                case ConsoleKey.Backspace:
                    _vhd.GoUp(); selected = 0; break;
                case ConsoleKey.Escape:
                    if (HandleEsc()) return; break;
            }
        }
    }

    private static bool HandleEsc()
    {
        var now = DateTime.UtcNow;
        if (_escPrimed && (now - _lastEsc).TotalMilliseconds < 1500)
        {
            _exiting = true;
            return true;
        }
        _lastEsc = now;
        _escPrimed = true;
        return false;
    }

    private static bool IsMedia(string name)
    {
        var ext = Path.GetExtension(name);
        return MediaExts.Contains(ext);
    }

    private static void DrawBrowser(List<FileEntry> entries, ref int selected)
    {
        if (selected >= entries.Count) selected = entries.Count - 1;
        if (selected < 0) selected = 0;

        Console.CursorVisible = false;
        Console.Clear();
        Console.SetCursorPosition(0, 0);
        var (w, h) = (Console.WindowWidth, Console.WindowHeight);
        if (w <= 0 || h <= 0) return;

        DrawTitle();

        var pathStr = $"  {_vhd!.CurrentPath}";
        Console.ForegroundColor = ConsoleColor.DarkYellow;
        Console.WriteLine(pathStr.PadRight(w - 1, ' '));
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine(new string('─', Math.Max(w - 1, 0)));
        Console.ResetColor();

        var rows = Math.Max(h - 8, 3);
        var page = selected / rows;
        var start = page * rows;
        var shown = entries.Skip(start).Take(rows).ToList();

        for (int i = 0; i < rows; i++)
        {
            Console.SetCursorPosition(0, 4 + i);
            if (i < shown.Count)
            {
                var entry = shown[i];
                var idx = start + i;
                var isSel = idx == selected;
                var isDir = entry.IsDirectory;
                var icon = isDir ? "  ▸" : (IsMedia(entry.Name) ? "  ♫" : "  ·");
                var line = icon + " " + Truncate(entry.Name, w - 6);
                if (isSel)
                {
                    Console.BackgroundColor = ConsoleColor.DarkCyan;
                    Console.ForegroundColor = ConsoleColor.White;
                    Console.Write(line.PadRight(w - 1, ' '));
                    Console.ResetColor();
                }
                else
                {
                    Console.ForegroundColor = isDir ? ConsoleColor.Cyan : ConsoleColor.Gray;
                    Console.Write(line.PadRight(w - 1, ' '));
                    Console.ResetColor();
                }
            }
            else
            {
                Console.Write(new string(' ', Math.Max(w - 1, 0)));
            }
        }

        Console.SetCursorPosition(0, 4 + rows);
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine(new string('─', Math.Max(w - 1, 0)));
        Console.Write("  ↑/↓ navigate   Enter open/play   Backspace up   Esc Esc exit   ".PadRight(w - 1, ' '));
        Console.ResetColor();
    }

    private static string Truncate(string s, int max)
    {
        if (max <= 0) return "";
        if (s.Length <= max) return s;
        return s[..(max - 1)] + "…";
    }

    private static void MediaMenu(FileEntry file)
    {
        string[] options = { "▶ Play Video", "♪ Play Audio Only", "← Cancel" };
        var selected = 0;
        RenderMenu(file, options, selected);

        while (!_exiting)
        {
            var key = Console.ReadKey(true).Key;
            switch (key)
            {
                case ConsoleKey.UpArrow: selected = (selected - 1 + options.Length) % options.Length; RenderMenu(file, options, selected); break;
                case ConsoleKey.DownArrow: selected = (selected + 1) % options.Length; RenderMenu(file, options, selected); break;
                case ConsoleKey.Enter:
                    if (selected == 0) PlayVideo(file);
                    else if (selected == 1) PlayAudio(file);
                    return;
                case ConsoleKey.Escape:
                    if (HandleEsc()) return;
                    return;
                case ConsoleKey.Backspace: return;
            }
        }
    }

    private static void RenderMenu(FileEntry file, string[] options, int selected)
    {
        Console.CursorVisible = false;
        Console.SetCursorPosition(0, 0);
        var (w, h) = (Console.WindowWidth, Console.WindowHeight);
        var boxTop = h / 2 - 2;
        var width = 34;
        var left = Math.Max((w - width) / 2, 0);

        // overlay
        Console.BackgroundColor = ConsoleColor.DarkMagenta;
        Console.ForegroundColor = ConsoleColor.White;
        for (int r = 0; r < 6; r++)
        {
            Console.SetCursorPosition(left, boxTop + r);
            Console.Write(new string(' ', width));
        }

        Console.SetCursorPosition(left + 1, boxTop);
        Console.BackgroundColor = ConsoleColor.Black;
        Console.ForegroundColor = ConsoleColor.Magenta;
        Console.Write(" " + Truncate(file.Name, width - 3));
        for (int i = 0; i < options.Length; i++)
        {
            Console.SetCursorPosition(left + 2, boxTop + 2 + i);
            if (i == selected)
            {
                Console.BackgroundColor = ConsoleColor.DarkCyan;
                Console.ForegroundColor = ConsoleColor.White;
            }
            else
            {
                Console.BackgroundColor = ConsoleColor.DarkMagenta;
                Console.ForegroundColor = ConsoleColor.White;
            }
            Console.Write(" " + options[i].PadRight(width - 5));
        }
        Console.ResetColor();
    }

    private static void PlayVideo(FileEntry file)
    {
        try
        {
            var stream = _vhd!.OpenFile(file.Name);
            ViewStatus($"Opening video: {file.Name}");
            VideoPlayerWindow.Run(file.Name, stream);
        }
        catch (Exception ex)
        {
            ViewStatus($"Error opening video: {ex.Message}");
        }
    }

    private static void PlayAudio(FileEntry file)
    {
        try
        {
            var stream = _vhd!.OpenFile(file.Name);
            using var player = new AudioPlayer();
            player.Load(stream);
            AudioLoop(player, file.Name);
        }
        catch (Exception ex)
        {
            ViewStatus($"Error opening audio: {ex.Message}");
        }
    }

    private static void AudioLoop(AudioPlayer player, string title)
    {
        var exit = false;
        var lastRender = DateTime.MinValue;
        Console.Clear();
        ConsoleEvents.ClearInput();

        while (!exit && !_exiting)
        {
            DrawAudioUI(player, title);
            Thread.Sleep(10);

            while (ConsoleEvents.TryReadEvent(out var ev))
            {
                if (ev.IsMouse)
                {
                    var m = ev.Mouse;
                    if ((m.dwButtonState & ConsoleEvents.FROM_LEFT_1ST_BUTTON_PRESSED) != 0)
                    {
                        HandleSeekMouse(player, m);
                    }
                }
                else if (ev.IsKey && ev.KeyDown)
                {
                    switch (ev.Key)
                    {
                        case ConsoleKey.Spacebar: player.TogglePlayPause(); break;
                        case ConsoleKey.UpArrow: player.Volume = Math.Clamp(player.Volume + 5, 0, 100); break;
                        case ConsoleKey.DownArrow: player.Volume = Math.Clamp(player.Volume - 5, 0, 100); break;
                        case ConsoleKey.LeftArrow: player.Seek(Math.Max(0, (double)(player.Time - 5000) / Math.Max(1, player.Length))); break;
                        case ConsoleKey.RightArrow: player.Seek(Math.Min(1, (double)(player.Time + 5000) / Math.Max(1, player.Length))); break;
                        case ConsoleKey.Q: player.Volume = Math.Clamp(player.Volume + 5, 0, 100); break;
                        case ConsoleKey.A: player.Volume = Math.Clamp(player.Volume - 5, 0, 100); break;
                        case ConsoleKey.Escape:
                            if (HandleEsc()) { exit = true; _exiting = true; }
                            else exit = true;
                            break;
                        case ConsoleKey.Backspace:
                        case ConsoleKey.Enter:
                            exit = true; break;
                    }
                }
            }
        }
    }

    private static void HandleSeekMouse(AudioPlayer player, ConsoleEvents.MOUSE_EVENT_RECORD mouse)
    {
        var row = Console.WindowHeight - 3;
        if (mouse.dwMousePosition.Y == row)
        {
            var barStart = 10;
            var barW = Math.Max(Console.WindowWidth - barStart - 12, 10);
            var x = mouse.dwMousePosition.X - barStart;
            if (x < 0) x = 0;
            var frac = Math.Clamp((double)x / barW, 0, 1);
            player.Seek(frac);
        }
    }

    private static void DrawAudioUI(AudioPlayer player, string title)
    {
        var (w, h) = (Console.WindowWidth, Console.WindowHeight);
        if (w <= 6 || h <= 4) return;

        Console.CursorVisible = false;
        var len = player.Length;
        var time = player.Time;
        var frac = len > 0 ? Math.Clamp((double)time / len, 0, 1) : 0;

        Console.SetCursorPosition(0, 0);
        var top = $"  ♪ Now Playing: {Truncate(title, w - 14)}".PadRight(w - 1, ' ');
        Console.BackgroundColor = ConsoleColor.DarkGreen;
        Console.ForegroundColor = ConsoleColor.White;
        Console.Write(top);
        Console.ResetColor();

        var volumeStr = $"  Volume: {player.Volume}%".PadRight(Math.Min(w / 2, 24), ' ');
        var stateStr = player.Playing ? "⏸ Playing   Press SPACE to pause" : "▶ Paused    Press SPACE to play";
        var info = (volumeStr + "  " + stateStr).PadRight(w - 1, ' ');
        Console.SetCursorPosition(0, h - 4);
        Console.BackgroundColor = ConsoleColor.DarkGray;
        Console.ForegroundColor = ConsoleColor.White;
        Console.Write(info);
        Console.ResetColor();

        // seek bar
        Console.SetCursorPosition(0, h - 3);
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.Write(Format(time).PadRight(9));
        Console.ResetColor();
        var barStart = 10;
        var barW = Math.Max(w - barStart - 12, 10);
        Console.SetCursorPosition(barStart, h - 3);
        Console.BackgroundColor = ConsoleColor.DarkGray;
        Console.ForegroundColor = ConsoleColor.White;
        var filled = (int)(barW * frac);
        var bar = new string('█', filled) + new string('░', Math.Max(barW - filled, 0));
        Console.Write(bar);
        // thumb
        Console.SetCursorPosition(barStart + filled - 1, h - 3);
        Console.BackgroundColor = ConsoleColor.Cyan;
        Console.Write("│");
        Console.ResetColor();
        Console.SetCursorPosition(barStart + barW + 1, h - 3);
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.Write($"{Format(len)}".PadRight(9));
        Console.ResetColor();

        Console.SetCursorPosition(0, h - 1);
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.Write("  ←/→ seek   Q/A volume   Space play/pause   Esc stop".PadRight(w - 1, ' '));
        Console.ResetColor();
    }

    private static string Format(long ms)
    {
        var t = TimeSpan.FromMilliseconds(ms);
        return t.TotalHours >= 1 ? t.ToString(@"hh\:mm\:ss") : t.ToString(@"mm\:ss");
    }

    private static void ViewStatus(string msg)
    {
        Console.SetCursorPosition(0, Console.WindowHeight - 1);
        Console.ForegroundColor = ConsoleColor.DarkYellow;
        Console.Write(Truncate(msg, Math.Max(Console.WindowWidth - 1, 0)).PadRight(Console.WindowWidth - 1, ' '));
        Console.ResetColor();
        Console.SetCursorPosition(0, Console.WindowHeight - 1);
    }

    private static void Pause()
    {
        Console.WriteLine();
        Console.WriteLine("Press any key to exit...");
        Console.ReadKey(true);
    }
}
