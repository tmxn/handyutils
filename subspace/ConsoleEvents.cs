using System.Runtime.InteropServices;

namespace Subspace;

public static class ConsoleEvents
{
    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT_RECORD
    {
        public ushort EventType;
        public Union Union;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct Union
    {
        [FieldOffset(0)] public KEY_EVENT_RECORD KeyEvent;
        [FieldOffset(0)] public MOUSE_EVENT_RECORD MouseEvent;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEY_EVENT_RECORD
    {
        public int bKeyDown;
        public ushort wRepeatCount;
        public ushort wVirtualKeyCode;
        public ushort wVirtualScanCode;
        public ushort uChar;
        public uint dwControlKeyState;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MOUSE_EVENT_RECORD
    {
        public COORD dwMousePosition;
        public uint dwButtonState;
        public uint dwControlKeyState;
        public uint dwEventFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct COORD
    {
        public short X;
        public short Y;
    }

    private const ushort KEY_EVENT = 0x0001;
    private const ushort MOUSE_EVENT = 0x0002;

    public const uint FROM_LEFT_1ST_BUTTON_PRESSED = 0x0001;
    public const uint RIGHTMOST_BUTTON_PRESSED = 0x0002;
    public const uint DOUBLE_CLICK = 0x0002;
    public const uint MOUSE_WHEELED = 0x0004;
    public const uint MOUSE_MOVED = 0x0001;
    public const uint WHEEL_DELTA = 120;

    private static readonly IntPtr ConsoleIn = GetStdHandle(-10);
    public static IntPtr Handle => ConsoleIn;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadConsoleInput(IntPtr hConsoleInput, out INPUT_RECORD lpBuffer, uint nLength, out uint lpNumberOfEventsRead);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNumberOfConsoleInputEvents(IntPtr hConsoleInput, out uint lpcNumberOfEvents);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);

    public static (uint vk, char ch, bool down) ReadKeyEventHop(out bool gotEvent)
    {
        gotEvent = false;
        if (PeekConsoleInput(ConsoleIn, out var rec, 1, out uint read))
        {
            if (read > 0 && rec.EventType == KEY_EVENT)
            {
                ReadConsoleInput(ConsoleIn, out rec, 1, out _);
                gotEvent = true;
                return (rec.Union.KeyEvent.wVirtualKeyCode,
                        (char)rec.Union.KeyEvent.uChar,
                        rec.Union.KeyEvent.bKeyDown != 0);
            }
            else
            {
                ReadConsoleInput(ConsoleIn, out rec, 1, out _);
                gotEvent = false;
                return (0, '\0', false);
            }
        }
        return (0, '\0', false);
    }

    [DllImport("kernel32.dll")]
    private static extern bool PeekConsoleInput(IntPtr hConsoleInput, out INPUT_RECORD lpBuffer, uint nLength, out uint lpNumberOfEventsRead);

    public static bool HasKeyDown()
    {
        uint count;
        if (!GetNumberOfConsoleInputEvents(ConsoleIn, out count) || count == 0) return false;
        return PeekConsoleInput(ConsoleIn, out var rec, 1, out uint read) && read > 0 && rec.EventType == KEY_EVENT ? true : false;
    }

    public static void ClearInput()
    {
        while (GetNumberOfConsoleInputEvents(ConsoleIn, out uint count) && count > 0 && count < 5000)
        {
            ReadConsoleInput(ConsoleIn, out var rec, 1, out _);
        }
    }

    public static bool TryReadEvent(out ConsoleEventResult ev)
    {
        ev = default;
        if (PeekConsoleInput(ConsoleIn, out var rec, 1, out uint read) && read > 0)
        {
            ReadConsoleInput(ConsoleIn, out rec, 1, out _);
            if (rec.EventType == MOUSE_EVENT)
            {
                ev = new ConsoleEventResult(rec.Union.MouseEvent);
                return true;
            }
            if (rec.EventType == KEY_EVENT)
            {
                var k = rec.Union.KeyEvent;
                ev = new ConsoleEventResult((ConsoleKey)k.wVirtualKeyCode, (char)k.uChar, k.bKeyDown != 0);
                return true;
            }
            return false;
        }
        return false;
    }

    public static bool EnableQuickEdit(bool enable)
    {
        if (GetConsoleMode(ConsoleIn, out uint mode))
        {
            const uint ENABLE_QUICK_EDIT = 0x0040;
            const uint ENABLE_EXTENDED_FLAGS = 0x0080;
            var newMode = mode;
            if (enable) newMode = (mode & ~ENABLE_QUICK_EDIT) | ENABLE_EXTENDED_FLAGS;
            else newMode = mode & ~ENABLE_QUICK_EDIT;
            var ok = SetConsoleMode(ConsoleIn, newMode);
            return ok;
        }
        return false;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);
}

public readonly struct ConsoleEventResult
{
    public readonly bool IsMouse;
    public readonly bool IsKey;
    public readonly ConsoleKey Key;
    public readonly char Char;
    public readonly bool KeyDown;
    public readonly ConsoleEvents.MOUSE_EVENT_RECORD Mouse;

    public ConsoleEventResult(ConsoleEvents.MOUSE_EVENT_RECORD mouse)
    {
        IsMouse = true; IsKey = false; Mouse = mouse; Key = default; Char = '\0'; KeyDown = true;
    }

    public ConsoleEventResult(ConsoleKey key, char ch, bool down)
    {
        IsMouse = false; IsKey = true; Key = key; Char = ch; KeyDown = down; Mouse = default;
    }
}
