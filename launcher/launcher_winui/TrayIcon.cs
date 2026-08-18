using System.Runtime.InteropServices;
using Win32Exception = System.ComponentModel.Win32Exception;

namespace LauncherWinUI;

/// <summary>
/// Win32 shell tray icon (Shell_NotifyIcon) hosted on a hidden HWND_MESSAGE
/// window. WinUI 3 has no tray API and we keep the app dependency-free: a
/// message-only window gets its messages from the UI thread's pump (the one
/// WinUI 3 already runs), so everything here runs on the UI thread.
/// </summary>
internal sealed partial class TrayIcon : IDisposable
{
    public const int MenuShow = 1;
    public const int MenuReload = 2;
    public const int MenuExit = 3;

    public event Action? LeftClick;
    public event Action<int>? MenuCommand;

    private readonly WndProcDelegate _wndProc; // strong reference: keeps the delegate alive
    private readonly IntPtr _hwnd;
    private bool _disposed;

    // ------------------------------------------------------------------ P/Invoke

    private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    private const uint WM_APP_TRAY = 0x8001;
    private const uint WM_LBUTTONUP = 0x0202;
    private const uint WM_RBUTTONUP = 0x0205;
    private const uint WM_CONTEXTMENU = 0x0204;
    private static readonly IntPtr HWND_MESSAGE = new(-3);

    private const uint NIM_ADD = 0;
    private const uint NIM_DELETE = 1;

    private const uint NIF_MESSAGE = 0x01;
    private const uint NIF_ICON = 0x02;
    private const uint NIF_TIP = 0x04;

    private const int IMAGE_ICON = 1;
    private const uint LR_LOADFROMFILE = 0x10;
    private const uint LR_DEFAULTSIZE = 0x40;
    private static readonly IntPtr IDI_APPLICATION = new(32512);

    private const uint MF_STRING = 0x00;
    private const uint MF_SEPARATOR = 0x800;
    private const uint TPM_RETURNCMD = 0x0100;
    private const uint TPM_RIGHTBUTTON = 0x2000;

    // Both structs are passed by value with all blittable fields, as required
    // by source-generated P/Invokes (no string members, no ref).

    [StructLayout(LayoutKind.Sequential)]
    private struct WNDCLASSEX
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public IntPtr lpszMenuName;
        public IntPtr lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct NOTIFYICONDATA
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        public fixed ushort szTip[128];
        public uint dwState;
        public uint dwStateMask;
        public fixed ushort szInfo[256];   // unused
        public uint uVersion;
        public fixed ushort szInfoTitle[64]; // unused
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [LibraryImport("user32.dll", EntryPoint = "RegisterClassExW")]
    private static partial ushort RegisterClassExW(WNDCLASSEX wc);

    [LibraryImport("user32.dll", EntryPoint = "CreateWindowExW")]
    private static partial IntPtr CreateWindowExW(uint exStyle, [MarshalAs(UnmanagedType.LPWStr)] string className,
        [MarshalAs(UnmanagedType.LPWStr)] string windowName, uint style, int x, int y, int w, int h,
        IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [LibraryImport("user32.dll", EntryPoint = "DefWindowProcW")]
    private static partial IntPtr DefWindowProcW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [LibraryImport("shell32.dll", EntryPoint = "Shell_NotifyIconW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShellNotifyIcon(uint dwMessage, NOTIFYICONDATA data);

    [LibraryImport("user32.dll", EntryPoint = "LoadImageW")]
    private static partial IntPtr LoadImage(IntPtr instance, [MarshalAs(UnmanagedType.LPWStr)] string name, int type, int w, int h, uint loadFlags);

    [LibraryImport("user32.dll", EntryPoint = "LoadIconW")]
    private static partial IntPtr LoadIcon(IntPtr instance, IntPtr resource);

    [LibraryImport("user32.dll", EntryPoint = "CreatePopupMenu")]
    private static partial IntPtr CreatePopupMenu();

    [LibraryImport("user32.dll", EntryPoint = "AppendMenuW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AppendMenu(IntPtr menu, uint flags, IntPtr item, [MarshalAs(UnmanagedType.LPWStr)] string text);

    [LibraryImport("user32.dll", EntryPoint = "TrackPopupMenu")]
    private static partial int TrackPopupMenu(IntPtr menu, uint flags, int x, int y, int reserved, IntPtr hwnd, IntPtr rect);

    [LibraryImport("user32.dll", EntryPoint = "DestroyMenu")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyMenu(IntPtr menu);

    // -------------------------------------------------------------- public part

    public unsafe TrayIcon(string iconPath, string tooltip)
    {
        _wndProc = WndProcHandler;

        const string className = "LauncherWinUI.TrayMessageWindow";
        IntPtr classNamePtr = Marshal.StringToHGlobalUni(className);
        try
        {
            var wc = new WNDCLASSEX
            {
                cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
                lpszMenuName = IntPtr.Zero,
                lpszClassName = classNamePtr,
            };
            if (RegisterClassExW(wc) == 0)
                throw new Win32Exception();
        }
        finally
        {
            Marshal.FreeHGlobal(classNamePtr);
        }

        // HWND_MESSAGE parent: invisible, no z-order, messages still reach the
        // thread queue that WinUI 3 is pumping on the UI thread.
        _hwnd = CreateWindowExW(0, className, "launcher_winui tray", 0,
            0, 0, 0, 0, HWND_MESSAGE, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (_hwnd == IntPtr.Zero)
            throw new Win32Exception();

        IntPtr hIcon = LoadTrayIcon(iconPath);

        var ni = new NOTIFYICONDATA
        {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = _hwnd,
            uID = 1,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
            uCallbackMessage = WM_APP_TRAY,
            hIcon = hIcon,
        };
        // szTip is an inline 127-wchar buffer (NUL stops it at 128).
        int tipLen = Math.Min(tooltip.Length, 127);
        ushort* tip = ni.szTip; // implicitly pinned in unsafe context
        for (int i = 0; i < tipLen; i++)
            tip[i] = (ushort)tooltip[i];

        if (!ShellNotifyIcon(NIM_ADD, ni))
            throw new Win32Exception();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        var ni = new NOTIFYICONDATA
        {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = _hwnd,
            uID = 1,
        };
        ShellNotifyIcon(NIM_DELETE, ni);
    }

    // ------------------------------------------------------------------ private

    private static IntPtr LoadTrayIcon(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                IntPtr h = LoadImage(IntPtr.Zero, path, IMAGE_ICON, 0, 0, LR_LOADFROMFILE | LR_DEFAULTSIZE);
                if (h != IntPtr.Zero)
                    return h;
            }
        }
        catch (Exception ex)
        {
            App.Log($"tray icon load failed ({path}): {ex.Message}");
        }
        return LoadIcon(IntPtr.Zero, IDI_APPLICATION);
    }

    private IntPtr WndProcHandler(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_APP_TRAY)
        {
            uint trayEvent = (uint)lParam.ToInt64();
            if (trayEvent == WM_LBUTTONUP)
            {
                LeftClick?.Invoke();
            }
            else if (trayEvent == WM_RBUTTONUP || trayEvent == WM_CONTEXTMENU)
            {
                ShowMenu();
            }
            return IntPtr.Zero;
        }
        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    private void ShowMenu()
    {
        IntPtr menu = CreatePopupMenu();
        if (menu == IntPtr.Zero)
            return;

        AppendMenu(menu, MF_STRING, (IntPtr)MenuShow, "Open launcher");
        AppendMenu(menu, MF_STRING, (IntPtr)MenuReload, "Reload config");
        AppendMenu(menu, MF_SEPARATOR, IntPtr.Zero, "");
        AppendMenu(menu, MF_STRING, (IntPtr)MenuExit, "Exit");

        if (!Native.GetCursorPos(out Native.Point p))
        {
            DestroyMenu(menu);
            return;
        }

        // TPM_RETURNCMD: TrackPopupMenu's return value IS the clicked item id.
        int id = TrackPopupMenu(menu, TPM_RETURNCMD | TPM_RIGHTBUTTON, p.X, p.Y, 0, _hwnd, IntPtr.Zero);
        DestroyMenu(menu);
        if (id != 0)
            MenuCommand?.Invoke(id);
    }
}
