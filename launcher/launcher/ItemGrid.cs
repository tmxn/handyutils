using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;

namespace Launcher;

/// <summary>
/// Owner-drawn grid for the launcher items. A single window paints every item
/// card in one double-buffered pass, so rebuilding the grid is atomic — no
/// per-button flicker or "render one by one" reveals.
/// </summary>
public sealed class ItemGrid : Control
{
    private const int ButtonWidth = 150;
    private const int ButtonHeight = 44;
    private const int CellStepX = ButtonWidth + 6;
    private const int CellStepY = ButtonHeight + 6;
    private const int Radius = 7;
    private static readonly Padding GridPadding = new(6, 6, 6, 6);

    private List<LauncherItem> _items = new();
    private int _hoverIndex = -1;
    private int _pressedIndex = -1;
    private string _previewText = "";

    private static readonly Font ItemFont = new("Segoe UI", 9.5f, FontStyle.Regular);
    private static readonly Font EmptyFont = new("Segoe UI", 9f, FontStyle.Regular);
    private static readonly Font PreviewFont = new("Consolas", 9f, FontStyle.Regular);

    public List<LauncherItem> Items
    {
        get => _items;
        set
        {
            _items = value ?? new List<LauncherItem>();
            _hoverIndex = -1;
            _pressedIndex = -1;
            Invalidate();
        }
    }

    // Fired when the mouse moves over an item (form shows the command preview).
    public Action<LauncherItem>? PreviewRequested { get; set; }
    public Action? PreviewCleared { get; set; }
    public Action<LauncherItem>? ItemActivated { get; set; }

    private const int PreviewBarHeight = 26;

    /// <summary>Text shown in the bottom preview line of this grid.</summary>
    public string PreviewText
    {
        get => _previewText;
        set
        {
            if (_previewText == value) return;
            _previewText = value ?? "";
            InvalidatePreview();
        }
    }

    // Repaint only the preview line, not the whole grid, so hovering between
    // items never forces a full grid redraw.
    private void InvalidatePreview()
    {
        if (ClientSize.Width <= 0 || ClientSize.Height <= 0) return;
        Invalidate(new Rectangle(0, Height - PreviewBarHeight, Width, PreviewBarHeight));
    }

    /// <summary>Height needed to lay out the current items (plus preview line) at the given width.</summary>
    public int ContentHeight
    {
        get
        {
            int itemsHeight;
            if (_items == null || _items.Count == 0)
                itemsHeight = GridPadding.Top + GridPadding.Bottom + ButtonHeight;
            else
            {
                int cols = Math.Max(1, (ClientSize.Width - GridPadding.Left - GridPadding.Right) / CellStepX);
                int rows = (int)Math.Ceiling((double)_items.Count / cols);
                itemsHeight = GridPadding.Top + GridPadding.Bottom + rows * CellStepY;
            }
            return itemsHeight + PreviewBarHeight;
        }
    }

    public ItemGrid()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.SupportsTransparentBackColor |
                 ControlStyles.UserPaint, true);
        DoubleBuffered = true;
        BackColor = Color.Transparent;
    }

    private Size[] ComputeCells()
    {
        if (_items == null || _items.Count == 0)
            return Array.Empty<Size>();

        int cols = Math.Max(1, (ClientSize.Width - GridPadding.Left - GridPadding.Right) / CellStepX);
        var positions = new Size[_items.Count];
        for (int i = 0; i < _items.Count; i++)
        {
            int col = i % cols;
            int row = i / cols;
            positions[i] = new Size(
                GridPadding.Left + col * CellStepX,
                GridPadding.Top + row * CellStepY);
        }
        return positions;
    }

    private int HitTest(Point p)
    {
        var cells = ComputeCells();
        for (int i = 0; i < cells.Length; i++)
        {
            var r = new Rectangle(cells[i].Width, cells[i].Height, ButtonWidth, ButtonHeight);
            if (r.Contains(p))
                return i;
        }
        return -1;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int index = HitTest(e.Location);
        if (index != _hoverIndex)
        {
            _hoverIndex = index;
            if (index >= 0)
                PreviewRequested?.Invoke(_items[index]);
            else
                PreviewCleared?.Invoke();
            Invalidate();
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hoverIndex != -1)
        {
            _hoverIndex = -1;
            PreviewCleared?.Invoke();
            Invalidate();
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left)
        {
            _pressedIndex = HitTest(e.Location);
            if (_pressedIndex >= 0)
                Invalidate();
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button == MouseButtons.Left && _pressedIndex >= 0)
        {
            int index = _pressedIndex;
            _pressedIndex = -1;
            Invalidate();
            if (index == HitTest(e.Location) && index < _items.Count)
                ItemActivated?.Invoke(_items[index]);
        }
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;

        var palette = ThemeHelper.GetPalette();
        g.Clear(BackColor);

        if (_items == null || _items.Count == 0)
        {
            g.TextRenderingHint = TextRenderingHint.AntiAlias;
            using var emptyBrush = new SolidBrush(palette.Text);
            using var emptyFormat = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center
            };
            g.DrawString("No items in this category", EmptyFont, emptyBrush, new Rectangle(0, 0, Width, Height), emptyFormat);
            return;
        }

        var cells = ComputeCells();
        for (int i = 0; i < _items.Count && i < cells.Length; i++)
        {
            var bounds = new Rectangle(cells[i].Width, cells[i].Height, ButtonWidth - 1, ButtonHeight - 1);
            var drawRect = new Rectangle(bounds.X + 1, bounds.Y + 1, Math.Max(1, bounds.Width - 2), Math.Max(1, bounds.Height - 2));

            Color fill = i == _pressedIndex
                ? palette.ButtonSelectedBackground
                : i == _hoverIndex
                    ? palette.ButtonHoverBackground
                    : palette.ButtonBackground;

            using var path = CreateRoundedPath(drawRect, Radius);
            using var brush = new SolidBrush(fill);
            using var pen = new Pen(palette.ButtonBorder, 1f);
            g.FillPath(brush, path);
            g.DrawPath(pen, path);

            g.TextRenderingHint = TextRenderingHint.AntiAlias;
            using var itemBrush = new SolidBrush(palette.Text);
            using var itemFormat = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
                Trimming = StringTrimming.EllipsisCharacter
            };
            g.DrawString(_items[i].Name, ItemFont, itemBrush, bounds, itemFormat);
        }

        // Bottom command-preview line (same width as the grid). Drawn last (over
        // the cards if ever needed) using grayscale AA so the frequently-changing
        // text doesn't leave ClearType subpixel ghosts over the Mica.
        if (!string.IsNullOrEmpty(_previewText))
        {
            var previewColor = ThemeHelper.IsDarkMode()
                ? Color.FromArgb(180, 180, 180)
                : Color.FromArgb(80, 80, 80);
            var previewRect = new Rectangle(
                GridPadding.Left + 2,
                Height - PreviewBarHeight,
                Width - GridPadding.Horizontal - 4,
                PreviewBarHeight);

            g.TextRenderingHint = TextRenderingHint.AntiAlias;
            using var brush = new SolidBrush(previewColor);
            using var format = new StringFormat
            {
                Alignment = StringAlignment.Near,
                LineAlignment = StringAlignment.Center,
                Trimming = StringTrimming.EllipsisCharacter,
                FormatFlags = StringFormatFlags.NoWrap
            };
            g.DrawString(_previewText, PreviewFont, brush, previewRect, format);
        }
    }

    private static GraphicsPath CreateRoundedPath(Rectangle r, int radius)
    {
        var path = new GraphicsPath();
        int d = radius * 2;
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
