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
        _playPauseBtn = new Button { Text = "Pause", Size = new Size(70, 30), Location = new Point(10, 8), BackColor = Color.FromArgb(70, 70, 70), ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
        _seekBar = new SeekBar { Location = new Point(92, 15), Width = 700, Height = 16 };
        _timeLabel = new Label { Size = new Size(180, 22), Location = new Point(800, 12), ForeColor = Color.LightGray, Text = "00:00:00 / 00:00:00", AutoSize = false };

        bottom.Controls.Add(_playPauseBtn);
        bottom.Controls.Add(_seekBar);
        bottom.Controls.Add(_timeLabel);

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
    }

    private static string Format(long ms)
    {
        var t = TimeSpan.FromMilliseconds(ms);
        return t.TotalHours >= 1 ? t.ToString(@"hh\:mm\:ss") : t.ToString(@"mm\:ss");
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        _mp.Volume = Math.Clamp(_mp.Volume + (e.Delta > 0 ? 5 : -5), 0, 100);
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
