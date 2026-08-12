using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;

namespace Launcher;

public class GlassButton : Control
{
    private bool _isSelected;
    private bool _isHovered;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            Invalidate();
            Parent?.Invalidate(Bounds, true);
        }
    }

    public GlassButton()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.UserPaint, true);

        DoubleBuffered = true;
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        _isHovered = true;
        Invalidate();
        base.OnMouseEnter(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _isHovered = false;
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;

        var palette = ThemeHelper.GetPalette();

        var outer = new Rectangle(0, 0, Width - 1, Height - 1);
        var drawRect = new Rectangle(1, 1, Math.Max(1, Width - 3), Math.Max(1, Height - 3));

        // Ensure corner pixels outside rounded path blend with parent panel, not stale pixels.
        g.Clear(Parent?.BackColor ?? palette.RightPanelBackground);

        Color fill = IsSelected
            ? palette.ButtonSelectedBackground
            : _isHovered
                ? palette.ButtonHoverBackground
                : palette.ButtonBackground;

        using var path = CreateRoundedPath(drawRect, 7);
        using var brush = new SolidBrush(fill);
        using var pen = new Pen(palette.ButtonBorder, 1f);

        g.FillPath(brush, path);
        g.DrawPath(pen, path);

        // Use GDI+ DrawString with AntiAlias for Mica compatibility
        g.TextRenderingHint = TextRenderingHint.AntiAlias;
        using var textBrush = new SolidBrush(palette.Text);
        using var format = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
            Trimming = StringTrimming.EllipsisCharacter
        };
        g.DrawString(Text, Font, textBrush, outer, format);
    }

    private static GraphicsPath CreateRoundedPath(Rectangle r, int radius)
    {
        GraphicsPath path = new();
        int d = radius * 2;
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
