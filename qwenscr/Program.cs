// qsc — window-only screen cropper (screenshot utility for LLMs).
// Captures a whole window or a window-relative region to PNG. Windows-only.
using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

internal static class Native
{
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    // Best effort: physical-pixel captures on scaled displays; ignored if unsupported.
    public static void MakeDpiAware() => SetProcessDpiAwarenessContext(new IntPtr(-4)); // PER_MONITOR_AWARE_V2
}

internal static class Program
{
    private const int ExOk = 0;
    private const int ExUsage = 2;
    private const int ExNoWindow = 3;
    private const int ExCapture = 4;

    private static int Main(string[] args)
    {
        Native.MakeDpiAware();

        if (args.Length == 0)
        {
            Console.Error.WriteLine("qsc: no arguments. See --help.");
            return ExUsage;
        }

        string outPath = null;
        int pid = 0; bool pidSet = false;
        int x = 0, y = 0, w = 0, h = 0;
        double scale = 1.0; bool scaleSet = false;
        bool list = false, help = false;
        int slot = 0; // positional slots: 0=out.png 1=procpid 2=x 3=y 4=w 5=h

        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            if (a == "--help" || a == "-h" || a == "/?") { help = true; continue; }
            if (a == "--list") { list = true; continue; }

            switch (a)
            {
                case "--pid":
                {
                    int v;
                    if (!NextInt(args, ref i, a, out v)) return ExUsage;
                    pid = v; pidSet = true;
                    continue;
                }
                case "--x":
                case "--y":
                case "--w":
                case "--h":
                {
                    int v;
                    if (!NextInt(args, ref i, a, out v)) return ExUsage;
                    if (a == "--x") x = v; else if (a == "--y") y = v; else if (a == "--w") w = v; else h = v;
                    continue;
                }
                case "--scale":
                {
                    if (i + 1 >= args.Length || !double.TryParse(args[i + 1], out scale))
                        return Fail(ExUsage, $"{a} needs a number in (0, 1], e.g. --scale 0.5");
                    i++; scaleSet = true;
                    continue;
                }
            }

            if (a.StartsWith("--") || (a.StartsWith("-") && a.Length > 1 && a != "-"))
                return Fail(ExUsage, $"unknown option '{a}'. See --help.");

            // positional: <out.png> [procpid] [x] [y] [w] [h]
            if (slot >= 6)
                return Fail(ExUsage, $"unexpected extra argument '{a}'. See --help.");
            if (!int.TryParse(a, out int pv))
            {
                if (slot == 0) outPath = a;
                else return Fail(ExUsage, $"argument for slot {slot} must be an integer, got '{a}'. See --help.");
            }
            else
            {
                switch (slot)
                {
                    case 0: outPath = a; break; // a png path that happens to be numeric? treat as out anyway
                    case 1: pid = pv; pidSet = true; break;
                    case 2: x = pv; break;
                    case 3: y = pv; break;
                    case 4: w = pv; break;
                    case 5: h = pv; break;
                }
            }
            slot++;
        }

        if (help) { PrintHelp(); return ExOk; }

        if (list)
        {
            if (outPath != null || pidSet || x != 0 || y != 0 || w != 0 || h != 0 || scaleSet)
                return Fail(ExUsage, "--list takes no other arguments.");
            return ListWindows();
        }

        if (scaleSet && (scale <= 0.0 || scale > 1.0))
            return Fail(ExUsage, "--scale must be in (0, 1], e.g. 0.5");
        if (w < 0 || h < 0)
            return Fail(ExUsage, "w and h must be >= 0 (0 0 = whole window).");
        if ((w > 0) != (h > 0))
            return Fail(ExUsage, "a crop needs both --w and --h (or positional w h).");
        if (x < 0 || y < 0)
            return Fail(ExUsage, "x and y must be >= 0 (window-relative offsets).");

        // --- bounds mode: --pid <n> with no output path ---
        if (outPath == null)
        {
            if (!pidSet)
                return Fail(ExUsage, "missing <out.png> and process. Usage: qsc <out.png> [procpid] [x] [y] [w] [h] | qsc --pid <n> | qsc --list. See --help.");
            if (scaleSet)
                return Fail(ExUsage, "bounds mode (--pid <n> with no <out.png>) takes no --scale.");
            if (x != 0 || y != 0 || w != 0 || h != 0)
                return Fail(ExUsage, "bounds mode takes only --pid <n>; pass <out.png> to capture.");
            return PrintBounds(pid);
        }

        if (!pidSet)
            return Fail(ExUsage, "missing process id. Pass <procpid> positionally or --pid <n>; use --list to find window PIDs.");

        return Capture(pid, outPath, x, y, w, h, scale);
    }

    // ---------- modes ----------

    private static int ListWindows()
    {
        Console.WriteLine("PID        X      Y     W    H  TITLE");
        foreach (Process p in Process.GetProcesses())
        {
            try
            {
                IntPtr hwnd = p.MainWindowHandle;
                if (hwnd == IntPtr.Zero) continue;
                string title = p.MainWindowTitle ?? "";
                if (!Native.GetWindowRect(hwnd, out Native.RECT r)) continue;
                int bw = r.Right - r.Left, bh = r.Bottom - r.Top;
                if (bw <= 0 || bh <= 0) continue;
                Console.WriteLine($"{p.Id,6} {r.Left,6} {r.Top,6} {bw,6} {bh,6}  {title}");
            }
            catch { /* some processes deny access; skip */ }
            finally { p.Dispose(); }
        }
        return ExOk;
    }

    private static int PrintBounds(int pid)
    {
        (IntPtr hwnd, int l, int t, int w, int h, string name) win = GetWindow(pid);
        if (win.hwnd == IntPtr.Zero)
            return Fail(ExNoWindow, win.name);
        Console.WriteLine($"{win.l} {win.t} {win.w} {win.h}");
        return ExOk;
    }

    private static int Capture(int pid, string outPath, int x, int y, int w, int h, double scale)
    {
        (IntPtr hwnd, int l, int t, int w, int h, string name) win = GetWindow(pid);
        if (win.hwnd == IntPtr.Zero)
            return Fail(ExNoWindow, win.name);
        int winL = win.l, winT = win.t, winW = win.w, winH = win.h;

        bool crop = w > 0 && h > 0;
        if (crop && (x + w > winW || y + h > winH))
            return Fail(ExUsage,
                $"crop ({x},{y} {w}x{h}) extends past the window ({winW}x{winH}). " +
                $"Coords are window-relative: need 0 <= x, 0 <= y, x+w <= {winW}, y+h <= {winH}.");

        int srcL = winL + (crop ? x : 0);
        int srcT = winT + (crop ? y : 0);
        int bw = crop ? w : winW;
        int bh = crop ? h : winH;

        Bitmap bmp = null;
        try
        {
            bmp = new Bitmap(bw, bh);
            using (Graphics g = Graphics.FromImage(bmp))
                g.CopyFromScreen(srcL, srcT, 0, 0, new Size(bw, bh));

            if (scale < 1.0)
            {
                int sw = Math.Max(1, (int)Math.Round(bw * scale, MidpointRounding.AwayFromZero));
                int sh = Math.Max(1, (int)Math.Round(bh * scale, MidpointRounding.AwayFromZero));
                Bitmap small = new Bitmap(sw, sh);
                using (Graphics g2 = Graphics.FromImage(small))
                {
                    g2.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g2.PixelOffsetMode = PixelOffsetMode.Half;
                    g2.CompositingMode = CompositingMode.SourceCopy;
                    g2.DrawImage(bmp, 0, 0, sw, sh);
                }
                bmp.Dispose();
                bmp = small;
            }

            string dir = Path.GetDirectoryName(Path.GetFullPath(outPath));
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            bmp.Save(outPath, ImageFormat.Png);
            Console.WriteLine($"SAVED {outPath} {bmp.Width}x{bmp.Height} WIN X={winL} Y={winT} W={winW} H={winH}");
            return ExOk;
        }
        catch (Exception ex)
        {
            return Fail(ExCapture, $"capture/save failed for '{outPath}': {ex.Message}");
        }
        finally
        {
            bmp?.Dispose();
        }
    }

    // ---------- helpers ----------

    // Returns (hwnd, left, top, width, height, errName). hwnd==Zero means failure, errName is the message.
    private static (IntPtr hwnd, int l, int t, int w, int h, string name) GetWindow(int pid)
    {
        Process proc;
        try { proc = Process.GetProcessById(pid); }
        catch (ArgumentException)
        { return (IntPtr.Zero, 0, 0, 0, 0, $"process {pid} not found. Use --list to see windowed processes."); }
        try
        {
            IntPtr hwnd = proc.MainWindowHandle;
            if (hwnd == IntPtr.Zero)
                return (IntPtr.Zero, 0, 0, 0, 0, $"process {pid} ({proc.ProcessName}) has no main window. Use --list to find a windowed PID.");
            if (!Native.GetWindowRect(hwnd, out Native.RECT r))
                return (IntPtr.Zero, 0, 0, 0, 0, $"GetWindowRect failed for pid {pid} (win32 error {Marshal.GetLastWin32Error()}).");
            int bw = r.Right - r.Left, bh = r.Bottom - r.Top;
            if (bw <= 0 || bh <= 0)
                return (IntPtr.Zero, 0, 0, 0, 0, $"window of pid {pid} has invalid bounds {bw}x{bh}.");
            return (hwnd, r.Left, r.Top, bw, bh, null);
        }
        finally { proc.Dispose(); }
    }

    // Reads "flag value" where value must be a positive int (pid) or non-negative int (x/y/w/h).
    private static bool NextInt(string[] args, ref int i, string flag, out int value)
    {
        value = 0;
        if (i + 1 >= args.Length || !int.TryParse(args[i + 1], out value))
        {
            Fail(ExUsage, $"{flag} needs an integer value, got '{(i + 1 < args.Length ? args[i + 1] : "<missing>")}'.");
            return false;
        }
        i++;
        if (flag == "--pid" && value <= 0)
        {
            Fail(ExUsage, $"--pid must be > 0, got {value}.");
            return false;
        }
        return true;
    }

    private static int Fail(int code, string msg)
    {
        Console.Error.WriteLine($"qsc: {msg}");
        return code;
    }

    private static void PrintHelp()
    {
        Console.WriteLine(@"qsc — window-only screen cropper (screenshot to PNG)

Usage:
  qsc <out.png> [procpid] [x] [y] [w] [h]      positional (ps1-compatible)
  qsc --list                                   processes with a main window: PID X Y W H TITLE
  qsc --pid <n>                                print window bounds only: X Y W H
  qsc <out.png> --pid <n> [--x n] [--y n] [--w n] [--h n] [--scale f]

Options:
  --pid <n>    target process (positional procpid also works)
  --x / --y    crop offset from the window's top-left (window-relative pixels)
  --w / --h    crop size; both > 0 → crop that region, otherwise capture the whole window
  --scale <f>  downsample the captured image (0 < f <= 1), e.g. --scale 0.5
  --help       this text

Coordinates are window-relative (offset from the window's top-left), not screen coords.
The window's GetWindowRect bounds may be negative on multi-monitor setups; that is handled internally.

Stdout (capture): SAVED <path> <w>x<h> WIN X=<l> Y=<t> W=<w> H=<h>
Exit codes: 0 ok · 2 usage/arg error · 3 process not found or no main window · 4 capture/IO error");
    }
}
