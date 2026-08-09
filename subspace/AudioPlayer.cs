using LibVLCSharp.Shared;

namespace Subspace;

public class AudioPlayer : IDisposable
{
    private readonly LibVLC _libVlc;
    private readonly MediaPlayer _mp;
    private Media? _media;
    private StreamMediaInput? _input;
    private Stream? _stream;
    private bool _playing;
    private bool _disposed;

    public AudioPlayer()
    {
        Core.Initialize();
        _libVlc = new LibVLC(new[] { "--no-video", "--vout=none" });
        _libVlc.Log += (_, _) => { };   // swallow VLC log output so it never hits the console
        _mp = new MediaPlayer(_libVlc);
    }

    public void Load(Stream stream)
    {
        Stop();
        _stream = stream;
        _input = new StreamMediaInput(stream);
        _media = new Media(_libVlc, _input);
        _mp.Play(_media);
        _playing = true;
    }

    public bool IsPlaying => _playing;

    public bool Playing => _mp.IsPlaying;

    public long Time => _mp.Time;
    public long Length => _mp.Length;
    public int Volume { get => _mp.Volume; set => _mp.Volume = Math.Clamp(value, 0, 100); }

    public void TogglePlayPause()
    {
        if (_media == null) return;
        if (_mp.IsPlaying) { _mp.Pause(); _playing = false; }
        else
        {
            if (_mp.Time > 0 && _mp.Length > 0 && _mp.Time >= _mp.Length - 100)
            {
                _mp.Stop();
                _mp.Time = 0;
            }
            _mp.Play();
            _playing = true;
        }
    }

    public void Seek(double fraction)
    {
        if (_mp.Length > 0)
        {
            _mp.Time = (long)(fraction * _mp.Length);
        }
    }

    public void Stop()
    {
        if (_mp.IsPlaying) _mp.Stop();
        _playing = false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _mp.Stop(); } catch { }
        try { _media?.Dispose(); } catch { }
        try { _input?.Dispose(); } catch { }
        try { _mp.Dispose(); } catch { }
        try { _libVlc.Dispose(); } catch { }
        try { _stream?.Dispose(); } catch { }
    }
}
