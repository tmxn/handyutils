using System.Text;

namespace Subspace;

public struct Cell : IEquatable<Cell>
{
    public char Character;
    public ConsoleColor ForegroundColor;
    public ConsoleColor BackgroundColor;

    public Cell(char ch, ConsoleColor fg = ConsoleColor.Gray, ConsoleColor bg = ConsoleColor.Black)
    {
        Character = ch;
        ForegroundColor = fg;
        BackgroundColor = bg;
    }

    public bool Equals(Cell other) =>
        Character == other.Character &&
        ForegroundColor == other.ForegroundColor &&
        BackgroundColor == other.BackgroundColor;

    public override bool Equals(object? obj) => obj is Cell other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(Character, ForegroundColor, BackgroundColor);
    public static bool operator ==(Cell left, Cell right) => left.Equals(right);
    public static bool operator !=(Cell left, Cell right) => !left.Equals(right);
}

public class DoubleBufferedTerminal : IDisposable
{
    public int Width { get; private set; }
    public int Height { get; private set; }

    private Cell[,] _frontBuffer;
    private Cell[,] _backBuffer;
    private readonly StringBuilder _renderBuilder = new(8192);
    private bool _initialized;

    public DoubleBufferedTerminal(int width, int height)
    {
        Width = width;
        Height = height;
        _frontBuffer = new Cell[width, height];
        _backBuffer = new Cell[width, height];
        Console.OutputEncoding = System.Text.Encoding.UTF8;
    }

    public void Initialize()
    {
        if (_initialized) return;
        Console.Write("\x1b[?1049h\x1b[?25l");
        ConsoleEvents.EnableMouseInput(true);
        _initialized = true;
    }

    public void SetCell(int x, int y, char ch, ConsoleColor fg = ConsoleColor.Gray, ConsoleColor bg = ConsoleColor.Black)
    {
        if (x >= 0 && x < Width && y >= 0 && y < Height)
            _backBuffer[x, y] = new Cell(ch, fg, bg);
    }

    public void SetLine(int x, int y, string text, ConsoleColor fg = ConsoleColor.Gray, ConsoleColor bg = ConsoleColor.Black)
    {
        for (int i = 0; i < text.Length && (x + i) < Width; i++)
            _backBuffer[x + i, y] = new Cell(text[i], fg, bg);
    }

    public void ClearBackBuffer(ConsoleColor fg = ConsoleColor.Gray, ConsoleColor bg = ConsoleColor.Black)
    {
        Cell emptyCell = new(' ', fg, bg);
        for (int y = 0; y < Height; y++)
            for (int x = 0; x < Width; x++)
                _backBuffer[x, y] = emptyCell;
    }

    public void ResetFrontBuffer()
    {
        // After a terminal clear, sync front buffer to empty so next Render sends all cells
        Cell emptyCell = new(' ', ConsoleColor.Gray, ConsoleColor.Black);
        for (int y = 0; y < Height; y++)
            for (int x = 0; x < Width; x++)
                _frontBuffer[x, y] = emptyCell;
    }

    private static readonly Dictionary<ConsoleColor, string> AnsiFg = new()
    {
        { ConsoleColor.Black, "30" }, { ConsoleColor.DarkBlue, "34" },
        { ConsoleColor.DarkGreen, "2;32" }, { ConsoleColor.DarkCyan, "36" },
        { ConsoleColor.DarkRed, "1;31" }, { ConsoleColor.DarkMagenta, "1;35" },
        { ConsoleColor.DarkYellow, "33" }, { ConsoleColor.Gray, "37" },
        { ConsoleColor.DarkGray, "2;37" }, { ConsoleColor.Blue, "1;34" },
        { ConsoleColor.Green, "1;32" }, { ConsoleColor.Cyan, "1;36" },
        { ConsoleColor.Red, "1;31" }, { ConsoleColor.Magenta, "1;35" },
        { ConsoleColor.Yellow, "1;33" }, { ConsoleColor.White, "1;37" },
    };

    private static readonly Dictionary<ConsoleColor, string> AnsiBg = new()
    {
        { ConsoleColor.Black, "40" }, { ConsoleColor.DarkBlue, "44" },
        { ConsoleColor.DarkGreen, "2;42" }, { ConsoleColor.DarkCyan, "46" },
        { ConsoleColor.DarkRed, "1;41" }, { ConsoleColor.DarkMagenta, "1;45" },
        { ConsoleColor.DarkYellow, "43" }, { ConsoleColor.Gray, "47" },
        { ConsoleColor.DarkGray, "2;47" }, { ConsoleColor.Blue, "1;44" },
        { ConsoleColor.Green, "1;42" }, { ConsoleColor.Cyan, "1;46" },
        { ConsoleColor.Red, "1;41" }, { ConsoleColor.Magenta, "1;45" },
        { ConsoleColor.Yellow, "1;43" }, { ConsoleColor.White, "1;47" },
    };

    public void Render()
    {
        _renderBuilder.Clear();
        _renderBuilder.Append("\x1b[?2026h");

        ConsoleColor? activeFg = null;
        ConsoleColor? activeBg = null;
        int lastX = -1, lastY = -1;

        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                Cell next = _backBuffer[x, y];
                Cell current = _frontBuffer[x, y];

                if (next != current)
                {
                    if (x != lastX + 1 || y != lastY)
                        _renderBuilder.Append($"\x1b[{y + 1};{x + 1}H");

                    if (next.ForegroundColor != activeFg)
                    {
                        _renderBuilder.Append($"\x1b[{AnsiFg.GetValueOrDefault(next.ForegroundColor, "39")}m");
                        activeFg = next.ForegroundColor;
                    }
                    if (next.BackgroundColor != activeBg)
                    {
                        _renderBuilder.Append($"\x1b[{AnsiBg.GetValueOrDefault(next.BackgroundColor, "49")}m");
                        activeBg = next.BackgroundColor;
                    }

                    _renderBuilder.Append(next.Character);
                    _frontBuffer[x, y] = next;
                    lastX = x;
                    lastY = y;
                }
            }
        }

        _renderBuilder.Append("\x1b[0m\x1b[?2026l");
        Console.Write(_renderBuilder.ToString());
    }

    public void Close()
    {
        Console.Write("\x1b[0m\x1b[?25h\x1b[?1049l");
        ConsoleEvents.EnableMouseInput(false);
    }

    public void Dispose() => Close();
}
