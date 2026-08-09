using System.ComponentModel;
using System.Drawing.Drawing2D;

namespace Subspace;

public class SeekBar : Control
{
    private double _value;      // 0..1
    private bool _tracking;

    [DefaultValue(0.0)]
    public double Value
    {
        get => _value;
        set { _value = Math.Clamp(value, 0, 1); Invalidate(); }
    }

    [Browsable(true)]
    public event EventHandler<double>? SeekRequested;

    public SeekBar()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw, true);
        BackColor = Color.FromArgb(60, 60, 60);
        Size = new Size(560, 16);
        Cursor = Cursors.Hand;
    }

    private Rectangle BarArea
    {
        get
        {
            var m = 2;
            var w = Math.Max(ClientSize.Width - m * 2 - 2, 4);
            return new Rectangle(m, (ClientSize.Height - 8) / 2, w, 8);
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        var rect = BarArea;

        // track
        using var track = new SolidBrush(Color.FromArgb(45, 45, 45));
        g.FillRectangle(track, rect);
        g.DrawRectangle(Pens.Black, rect);

        // fill
        int fillW = (int)(rect.Width * _value);
        if (fillW > 0)
        {
            using var fill = new LinearGradientBrush(rect, Color.FromArgb(0, 160, 180), Color.FromArgb(0, 200, 220), 0f);
            using var clip = new Region(new Rectangle(rect.X, rect.Y, fillW, rect.Height));
            var old = g.Clip;
            g.Clip = clip;
            g.FillRectangle(fill, rect);
            g.Clip = old;
        }

        // thumb
        int tx = rect.X + fillW;
        using var thumb = new SolidBrush(Color.FromArgb(240, 240, 240));
        g.FillEllipse(thumb, tx - 3, rect.Y - 3, 14, 14);
        using var thumbO = new Pen(Color.FromArgb(0, 160, 180), 1.5f);
        g.DrawEllipse(thumbO, tx - 3, rect.Y - 3, 14, 14);
    }

    private void SeekFromClient(int x, bool fire = true)
    {
        var rect = BarArea;
        double frac = rect.Width > 0 ? (x - rect.X) / (double)rect.Width : 0;
        frac = Math.Clamp(frac, 0, 1);
        Value = frac;
        if (fire) SeekRequested?.Invoke(this, frac);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left)
        {
            _tracking = true;
            Capture = true;
            SeekFromClient(e.X);
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_tracking && e.Button == MouseButtons.Left)
        {
            SeekFromClient(e.X);
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (_tracking && e.Button == MouseButtons.Left)
        {
            _tracking = false;
            Capture = false;
            SeekFromClient(e.X);
        }
    }
}

