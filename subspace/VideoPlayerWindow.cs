using LibVLCSharp.Shared;
using LibVLCSharp.WinForms;

namespace Subspace;

public class VideoPlayerWindow : Form
{
    private readonly LibVLC _libVlc;
    private readonly MediaPlayer _mp;
    private Media? _media;
    private StreamMediaInput? _input;
    private readonly VideoView _videoView;
    private readonly Button _playPauseBtn;
    private readonly SeekBar _seekBar;
    private readonly Label _timeLabel;
    private readonly VolumeGauge _volumeGauge;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly Stream _stream;
    private bool _ended;
    private bool _closing;

    private VideoPlayerWindow(LibVLC libVlc, MediaPlayer mp, Media media, StreamMediaInput input, Stream stream, string title)
    {
        _libVlc = libVlc;
        _mp = mp;
        _media = media;
        _input = input;
        _stream = stream;

        Text = $"Subspace - {title}";
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(960, 620);
        KeyPreview = true;
        BackColor = Color.Black;

        _videoView = new VideoView { Dock = DockStyle.Fill, BackColor = Color.Black };
        _videoView.MediaPlayer = _mp;

        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 46, BackColor = Color.FromArgb(30, 30, 30) };
        _playPauseBtn = new Button
        {
            Text = "Pause",
            Size = new Size(70, 30),
            BackColor = Color.FromArgb(70, 70, 70),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Anchor = AnchorStyles.Left | AnchorStyles.Top,
            Left = 10,
            Top = 8
        };
        _seekBar = new SeekBar
        {
            Height = 16,
            Left = 90,
            Top = 15,
            Width = 300,
            Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top
        };
        _timeLabel = new Label
        {
            Size = new Size(130, 22),
            ForeColor = Color.LightGray,
            Text = "00:00:00 / 00:00:00",
            AutoSize = false,
            Top = 12
        };
        _volumeGauge = new VolumeGauge
        {
            Size = new Size(32, 32),
            Top = 8,
            Volume = _mp.Volume
        };

        bottom.Controls.Add(_playPauseBtn);
        bottom.Controls.Add(_seekBar);
        bottom.Controls.Add(_timeLabel);
        bottom.Controls.Add(_volumeGauge);

        // Position right-aligned controls and stretch seekbar on resize
        void LayoutBottomPanel() => PositionRightControls(bottom);
        bottom.Resize += (_, _) => LayoutBottomPanel();
        Shown += (_, _) => LayoutBottomPanel();

        void PositionRightControls(Panel panel)
        {
            var margin = 8;
            var gap = 6;
            _volumeGauge.Left = panel.ClientSize.Width - margin - _volumeGauge.Width;
            _timeLabel.Left = _volumeGauge.Left - gap - _timeLabel.Width;
            _seekBar.Width = _timeLabel.Left - _seekBar.Left - gap;
        }

        Controls.Add(_videoView);
        Controls.Add(bottom);

        _playPauseBtn.Click += (_, _) => TogglePause();
        _seekBar.SeekRequested += (_, frac) => SeekTo(frac);

        _mp.EndReached += (_, _) => this.TryInvoke(() =>
        {
            _ended = true;
            _playPauseBtn.Text = "Play";
            SyncSeekBar(1.0);
        });

        _timer = new System.Windows.Forms.Timer { Interval = 200 };
        _timer.Tick += (_, _) => UpdateUI();
        _timer.Start();

        // Play only once the control handles exist so VLC renders into the VideoView
        // instead of spawning its own detached native window.
        Shown += (_, _) => _mp.Play();
    }

    public static void Run(string title, Stream stream)
    {
        var thread = new Thread(() =>
        {
            Core.Initialize();
            var libVlc = new LibVLC();
            libVlc.Log += (_, _) => { };   // swallow VLC log output so it never hits the console
            var mp = new MediaPlayer(libVlc);
            Media? media = null;
            StreamMediaInput? input = null;
            try
            {
                input = new StreamMediaInput(stream);
                media = new Media(libVlc, input);
                mp.Media = media;
            }
            catch { }

            var window = new VideoPlayerWindow(libVlc, mp, media!, input!, stream, title);
            window.FormClosed += (_, _) =>
            {
                try { mp.Stop(); } catch { }
                try { media?.Dispose(); } catch { }
                try { input?.Dispose(); } catch { }
                try { mp.Dispose(); } catch { }
                try { libVlc.Dispose(); } catch { }
                try { stream.Dispose(); } catch { }
            };
            Application.Run(window);
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
    }

    private void TogglePause()
    {
        // If the media reached the end, restart from the beginning.
        if (_ended || (_mp.Time > 0 && _mp.Length > 0 && _mp.Time >= _mp.Length - 100))
        {
            _ended = false;
            _mp.Stop();
            _mp.Time = 0;
            _mp.Play();
            _playPauseBtn.Text = "Pause";
            return;
        }

        if (_mp.IsPlaying) { _mp.Pause(); _playPauseBtn.Text = "Play"; }
        else { _mp.Play(); _playPauseBtn.Text = "Pause"; }
    }

    private void SeekTo(double frac)
    {
        var len = _mp.Length;
        if (len <= 0) return;
        _ended = false;
        _playPauseBtn.Text = _mp.IsPlaying ? "Pause" : "Play";
        _mp.Time = (long)(frac * len);
        if (_mp.Time >= len - 100 && !_mp.IsPlaying) { _mp.Play(); _playPauseBtn.Text = "Pause"; }
    }

    private void SyncSeekBar(double frac)
    {
        _seekBar.Value = frac;
    }

    private void UpdateUI()
    {
        if (_closing) return;
        var len = _mp.Length;
        var time = _mp.Time;
        if (len > 0)
        {
            _seekBar.Value = Math.Clamp((double)time / len, 0, 1);
            _timeLabel.Text = $"{Format(time)} / {Format(len)}";
        }
        _volumeGauge.Volume = _mp.Volume;
    }

    private static string Format(long ms)
    {
        var t = TimeSpan.FromMilliseconds(ms);
        return t.TotalHours >= 1 ? t.ToString(@"hh\:mm\:ss") : t.ToString(@"mm\:ss");
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        _mp.Volume = Math.Clamp(_mp.Volume + (e.Delta > 0 ? 5 : -5), 0, 100);
        _volumeGauge.Volume = _mp.Volume; // immediate visual feedback
        base.OnMouseWheel(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape) { Close(); return; }
        if (e.KeyCode == Keys.Space) { TogglePause(); e.Handled = true; }
        base.OnKeyDown(e);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _closing = true;
        _timer.Stop();
        base.OnFormClosing(e);
    }
}

// Draws a radial arc gauge indicating volume level (0–100).
// Arc starts at 6 o'clock and sweeps clockwise; 0 volume = empty, 100 = full circle.
public class VolumeGauge : Control
{
    private int _volume;
    public int Volume
    {
        get => _volume;
        set
        {
            var clamped = Math.Clamp(value, 0, 100);
            if (clamped != _volume) { _volume = clamped; Invalidate(); }
        }
    }

    public VolumeGauge()
    {
        BackColor = Color.FromArgb(30, 30, 30); // match bottom panel
        DoubleBuffered = true;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

        var size = Math.Min(Width, Height);
        var margin = 4;
        var rect = new Rectangle((Width - size) / 2, (Height - size) / 2, size - margin, size - margin);

        // Background track (dim)
        using var bgPen = new Pen(Color.FromArgb(80, 80, 80), 3);
        g.DrawEllipse(bgPen, rect);

        // Volume arc — starts at bottom (90°), sweeps clockwise
        if (_volume > 0)
        {
            var sweepAngle = (float)(_volume / 100.0) * 360;
            using var fgPen = new Pen(_volume <= 20 ? Color.Orange : Color.White, 3);
            g.DrawArc(fgPen, rect, 90, sweepAngle);
        }
    }
}

internal static class ControlExtensions
{
    public static void TryInvoke(this Control c, Action a)
    {
        if (c.IsHandleCreated && !c.IsDisposed)
        {
            try { c.BeginInvoke(a); } catch { }
        }
    }
}
