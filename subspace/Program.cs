using System.Text;

namespace Subspace;

public static class Program
{
    private static bool _exiting;
    private static bool _escPrimed;
    private static DateTime _lastEsc = DateTime.MinValue;
    private static VhdHandler? _vhd;
    private static DoubleBufferedTerminal? _term;
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

        // Initialize double-buffered terminal after PIN prompt
        var (w, h) = (Console.WindowWidth, Console.WindowHeight);
        _term = new DoubleBufferedTerminal(w, h);
        _term.Initialize();

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

        _term?.Close();
        Console.Clear();
        Console.CursorVisible = true;
        return 0;
    }

    private static void OnDoubleEsc()
    {
        _exiting = true;
        _term?.Close();
        Console.CursorVisible = true;
        Environment.Exit(0);
    }

    private static bool PromptPin(string correctPin)
    {
        Console.Clear();
        DrawTitleDirect();
        Console.WriteLine();
        Console.WriteLine("  Enter PIN: ");
        var entered = "";
        while (true)
        {
            ConsoleEvents.WaitForEvent(out var ev);
            if (!ev.IsKey || !ev.KeyDown) continue;

            if (ev.Key == ConsoleKey.Enter) break;
            if (ev.Key == ConsoleKey.Backspace && entered.Length > 0) entered = entered[..^1];
            else if (char.IsDigit(ev.Char) && entered.Length < 12) entered += ev.Char;

            var y = Console.CursorTop;
            Console.SetCursorPosition(13, y);
            Console.Write(new string('•', entered.Length) + new string(' ', 12 - entered.Length));

            if (entered.Length == correctPin.Length && entered == correctPin)
                return true;
        }
        return entered == correctPin;
    }

    private static void DrawTitleDirect()
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
        var entries = _vhd!.ListEntries();

        while (!_exiting)
        {
            // Render current state first (before blocking on input)
            if (entries.Count > 0)
            {
                DrawBrowser(entries, ref selected);
            }

            // Block until we get at least one event
            ConsoleEvents.WaitForEvent(out var ev);
            ProcessBrowserEvent(entries, ref selected, ev);

            // Drain any additional events that arrived
            while (ConsoleEvents.TryReadEvent(out ev))
            {
                ProcessBrowserEvent(entries, ref selected, ev);
            }

            // Refresh entries (may have changed from Enter/Backspace)
            entries = _vhd!.ListEntries();
            if (entries.Count == 0 && !selected.Equals(-1))
            {
                selected = 0;
            }
        }
    }

    private static void ProcessBrowserEvent(List<FileEntry> entries, ref int selected, ConsoleEventResult ev)
    {
        if (ev.IsMouse)
        {
            var m = ev.Mouse;
            // Left click: select item at clicked row
            if ((m.dwButtonState & ConsoleEvents.FROM_LEFT_1ST_BUTTON_PRESSED) != 0)
            {
                var clickRow = m.dwMousePosition.Y;
                var (w, h) = (Console.WindowWidth, Console.WindowHeight);
                var rows = Math.Max(h - 8, 3);
                if (clickRow >= 4 && clickRow < 4 + rows)
                {
                    var page = selected / rows;
                    var start = page * rows;
                    var idx = start + (clickRow - 4);
                    if (idx >= 0 && idx < entries.Count)
                    {
                        selected = idx;
                        var now = DateTime.UtcNow;
                        if (_lastClick > DateTime.MinValue && (now - _lastClick).TotalMilliseconds < 400)
                        {
                            var entry = entries[selected];
                            if (entry.IsDirectory) { _vhd!.TryEnter(entry.Name); selected = 0; }
                            else if (IsMedia(entry.Name)) { MediaMenu(entry); }
                        }
                        _lastClick = now;
                    }
                }
            }
            // Wheel: scroll up/down (delta is in HIGH word of dwButtonState)
            if ((m.dwEventFlags & ConsoleEvents.MOUSE_WHEELED) != 0)
            {
                var delta = (short)(m.dwButtonState >> 16);
                if (delta > 0) selected = (selected - 3 + entries.Count) % entries.Count;
                else if (delta < 0) selected = (selected + 3) % entries.Count;
            }
        }
        else if (ev.IsKey && ev.KeyDown)
        {
            switch (ev.Key)
            {
                case ConsoleKey.UpArrow: selected = (selected - 1 + entries.Count) % entries.Count; break;
                case ConsoleKey.DownArrow: selected = (selected + 1) % entries.Count; break;
                case ConsoleKey.Enter:
                    var entry = entries[selected];
                    if (entry.IsDirectory) { _vhd!.TryEnter(entry.Name); selected = 0; }
                    else if (IsMedia(entry.Name)) { MediaMenu(entry); }
                    break;
                case ConsoleKey.Backspace:
                    _vhd!.GoUp(); selected = 0; break;
                case ConsoleKey.Escape:
                    if (HandleEsc()) return;
                    break;
            }
        }
    }

    private static DateTime _lastClick = DateTime.MinValue;

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

        var term = _term!;
        var (w, h) = (term.Width, term.Height);
        if (w <= 0 || h <= 0) return;

        term.ClearBackBuffer();

        // Title
        DrawTitleToBuffer(term, 0, 0);

        // Path
        var pathStr = $"  {_vhd!.CurrentPath}";
        int pathRow = 5;
        for (int x = 0; x < w - 1; x++)
        {
            char ch = x < pathStr.Length ? pathStr[x] : ' ';
            term.SetCell(x, pathRow, ch, ConsoleColor.DarkYellow, ConsoleColor.Black);
        }

        // Separator
        int sepRow = pathRow + 1;
        for (int x = 0; x < w - 1; x++)
            term.SetCell(x, sepRow, '─', ConsoleColor.DarkGray, ConsoleColor.Black);

        // File list
        var rows = Math.Max(h - 8, 3);
        var page = selected / rows;
        var start = page * rows;
        var shown = entries.Skip(start).Take(rows).ToList();

        for (int i = 0; i < rows; i++)
        {
            int row = 4 + i;
            if (i < shown.Count)
            {
                var entry = shown[i];
                var idx = start + i;
                var isSel = idx == selected;
                var isDir = entry.IsDirectory;
                var icon = isDir ? "  ▸" : (IsMedia(entry.Name) ? "  ♫" : "  ·");
                var line = (icon + " " + Truncate(entry.Name, w - 6)).PadRight(w - 1, ' ');

                if (isSel)
                {
                    for (int x = 0; x < line.Length; x++)
                        term.SetCell(x, row, line[x], ConsoleColor.White, ConsoleColor.DarkCyan);
                }
                else
                {
                    ConsoleColor fg = isDir ? ConsoleColor.Cyan : ConsoleColor.Gray;
                    for (int x = 0; x < line.Length; x++)
                        term.SetCell(x, row, line[x], fg, ConsoleColor.Black);
                }
            }
            else
            {
                for (int x = 0; x < w - 1; x++)
                    term.SetCell(x, row, ' ', ConsoleColor.Gray, ConsoleColor.Black);
            }
        }

        // Bottom separator
        int bottomSepRow = 4 + rows;
        for (int x = 0; x < w - 1; x++)
            term.SetCell(x, bottomSepRow, '─', ConsoleColor.DarkGray, ConsoleColor.Black);

        // Help text
        int helpRow = bottomSepRow + 1;
        var helpText = "  ↑/↓ navigate   Click/wheel scroll   Enter open/play   Backspace up   Esc Esc exit   ".PadRight(w - 1, ' ');
        for (int x = 0; x < helpText.Length; x++)
            term.SetCell(x, helpRow, helpText[x], ConsoleColor.DarkGray, ConsoleColor.Black);

        term.Render();
    }

    private static void DrawTitleToBuffer(DoubleBufferedTerminal term, int x, int y)
    {
        string[] titleLines = new[]
        {
            " _                         ",
            " ____  _| |__ ____ __  __ _ __ ___ ",
            "(_-< || | '_ (_-< '_ \\/ _` / _/ -_)",
            "/__/\\_,_|_.__/__/ .__/\\__,_\\__\\___|",
            "                |_|                "
        };

        for (int i = 0; i < titleLines.Length; i++)
        {
            for (int c = 0; c < titleLines[i].Length && (x + c) < term.Width; c++)
                term.SetCell(x + c, y + i, titleLines[i][c], ConsoleColor.Cyan, ConsoleColor.Black);
        }
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
        // Clear alternate screen to remove wide characters (CJK) that cell-by-cell clear misses
        Console.Write("\x1b[2J\x1b[H");
        _term!.ResetFrontBuffer();
        RenderMenu(file, options, selected);

        while (!_exiting)
        {
            ConsoleEvents.WaitForEvent(out var ev);
            if (ProcessMenuEvent(file, options, ref selected, ev)) return;
            while (ConsoleEvents.TryReadEvent(out ev))
            {
                if (ProcessMenuEvent(file, options, ref selected, ev)) return;
            }
        }
    }

    private static bool ProcessMenuEvent(FileEntry file, string[] options, ref int selected, ConsoleEventResult ev)
    {
        if (ev.IsMouse)
        {
            var m = ev.Mouse;
            if ((m.dwButtonState & ConsoleEvents.FROM_LEFT_1ST_BUTTON_PRESSED) != 0)
            {
                var (w, h) = (Console.WindowWidth, Console.WindowHeight);
                var boxTop = h / 2 - 2;
                for (int i = 0; i < options.Length; i++)
                {
                    if (m.dwMousePosition.Y == boxTop + 2 + i)
                    {
                        selected = i;
                        RenderMenu(file, options, selected);
                        if (i == 0) PlayVideo(file);
                        else if (i == 1) PlayAudio(file);
                        return true; // close menu after playing
                    }
                }
            }
        }
        else if (ev.IsKey && ev.KeyDown)
        {
            switch (ev.Key)
            {
                case ConsoleKey.UpArrow: selected = (selected - 1 + options.Length) % options.Length; RenderMenu(file, options, selected); break;
                case ConsoleKey.DownArrow: selected = (selected + 1) % options.Length; RenderMenu(file, options, selected); break;
                case ConsoleKey.Enter:
                    if (selected == 0) PlayVideo(file);
                    else if (selected == 1) PlayAudio(file);
                    return true; // close menu after playing
                case ConsoleKey.Escape:
                    if (HandleEsc()) return true;
                    return true;
                case ConsoleKey.Backspace: return true;
            }
        }
        return false;
    }

    private static void RenderMenu(FileEntry file, string[] options, int selected)
    {
        var term = _term!;
        var (w, h) = (term.Width, term.Height);
        var boxTop = h / 2 - 2;
        var width = 34;
        var left = Math.Max((w - width) / 2, 0);

        // Overlay background
        for (int r = 0; r < 6; r++)
        {
            for (int x = 0; x < width; x++)
                term.SetCell(left + x, boxTop + r, ' ', ConsoleColor.White, ConsoleColor.DarkMagenta);
        }

        // File name header
        var nameLine = (" " + Truncate(file.Name, width - 3)).PadRight(width, ' ');
        for (int x = 0; x < nameLine.Length; x++)
            term.SetCell(left + 1 + x, boxTop, nameLine[x], ConsoleColor.Magenta, ConsoleColor.Black);

        // Options
        for (int i = 0; i < options.Length; i++)
        {
            var isSel = i == selected;
            var optLine = (" " + options[i]).PadRight(width - 5, ' ');
            for (int x = 0; x < optLine.Length; x++)
                term.SetCell(left + 2 + x, boxTop + 2 + i, optLine[x],
                    ConsoleColor.White, isSel ? ConsoleColor.DarkCyan : ConsoleColor.DarkMagenta);
        }

        term.Render();
    }

    private static void PlayVideo(FileEntry file)
    {
        try
        {
            var stream = _vhd!.OpenFile(file.Name);
            VideoPlayerWindow.Run(file.Name, stream);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error opening video: {ex.Message}");
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
            Console.WriteLine($"Error opening audio: {ex.Message}");
        }
    }

    private static void AudioLoop(AudioPlayer player, string title)
    {
        var exit = false;
        ConsoleEvents.ClearInput();
        // Clear alternate screen to remove wide characters (CJK) that cell-by-cell clear misses
        Console.Write("\x1b[2J\x1b[H");
        _term!.ResetFrontBuffer();

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
        var term = _term!;
        var (w, h) = (term.Width, term.Height);
        if (w <= 6 || h <= 4) return;

        term.ClearBackBuffer();

        var len = player.Length;
        var time = player.Time;
        var frac = len > 0 ? Math.Clamp((double)time / len, 0, 1) : 0;

        // Top bar
        var top = $"  ♪ Now Playing: {Truncate(title, w - 14)}".PadRight(w - 1, ' ');
        for (int x = 0; x < top.Length; x++)
            term.SetCell(x, 0, top[x], ConsoleColor.White, ConsoleColor.DarkGreen);

        // Info bar
        var volumeStr = $"  Volume: {player.Volume}%".PadRight(Math.Min(w / 2, 24), ' ');
        var stateStr = player.Playing ? "⏸ Playing   Press SPACE to pause" : "▶ Paused    Press SPACE to play";
        var info = (volumeStr + "  " + stateStr).PadRight(w - 1, ' ');
        for (int x = 0; x < info.Length; x++)
            term.SetCell(x, h - 4, info[x], ConsoleColor.White, ConsoleColor.DarkGray);

        // Seek bar
        var timeStr = Format(time).PadRight(9);
        for (int x = 0; x < timeStr.Length; x++)
            term.SetCell(x, h - 3, timeStr[x], ConsoleColor.DarkGray, ConsoleColor.Black);

        var barStart = 10;
        var barW = Math.Max(w - barStart - 12, 10);
        var filled = (int)(barW * frac);

        for (int x = 0; x < barW; x++)
        {
            char ch = x < filled ? '█' : '░';
            term.SetCell(barStart + x, h - 3, ch, ConsoleColor.White, ConsoleColor.DarkGray);
        }

        // Thumb
        if (filled > 0)
            term.SetCell(barStart + filled - 1, h - 3, '│', ConsoleColor.White, ConsoleColor.Cyan);

        var endTimeStr = $"{Format(len)}".PadRight(9);
        for (int x = 0; x < endTimeStr.Length; x++)
            term.SetCell(barStart + barW + 1 + x, h - 3, endTimeStr[x], ConsoleColor.DarkGray, ConsoleColor.Black);

        // Help
        var helpText = "  ←/→ seek   Q/A volume   Space play/pause   Esc stop".PadRight(w - 1, ' ');
        for (int x = 0; x < helpText.Length; x++)
            term.SetCell(x, h - 1, helpText[x], ConsoleColor.DarkGray, ConsoleColor.Black);

        term.Render();
    }

    private static string Format(long ms)
    {
        var t = TimeSpan.FromMilliseconds(ms);
        return t.TotalHours >= 1 ? t.ToString(@"hh\:mm\:ss") : t.ToString(@"mm\:ss");
    }

    private static void Pause()
    {
        Console.WriteLine();
        Console.WriteLine("Press any key to exit...");
        Console.ReadKey(true);
    }
}
