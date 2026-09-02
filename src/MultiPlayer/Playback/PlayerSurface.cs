using LibVLCSharp.Shared;
using MultiPlayer.Model;

namespace MultiPlayer.Playback;

/// <summary>
/// One decoder plus the native window it draws into, married for the lifetime of the
/// app. Roles (primary / tile 5 / parked) move between surfaces by re-parenting the
/// HWND, which is why a swap never interrupts playback: LibVLC is not told anything.
/// </summary>
public sealed class PlayerSurface : IDisposable
{
    private readonly LibVLC _vlc;
    private readonly MediaPlayer _mp;
    private bool _disposed;
    private bool _desiredMute = true;

    public PlayerSurface(VlcEngine engine, IntPtr parkingParent, int id)
    {
        Id = id;
        _vlc = engine.Vlc;
        Hwnd = Native.CreateSurface(parkingParent);
        _mp = new MediaPlayer(_vlc)
        {
            EnableHardwareDecoding = engine.HardwareDecoding,
            Hwnd = Hwnd,
            // LibVLC must not handle input: its defaults would eat space and the arrow
            // keys, and double-click would throw a video into its own fullscreen.
            EnableKeyInput = false,
            EnableMouseInput = false,
        };
        _mp.EncounteredError += (_, _) =>
        {
            var e = Entry;
            if (e is not null) e.Missing = true;
        };

        // Mute and volume only stick once an audio output exists, which is not until
        // playback has actually begun. Re-assert on Playing, off the event thread:
        // calling back into LibVLC from its own callback can deadlock.
        _mp.Playing += (_, _) => Task.Run(ApplyAudioState);
        _mp.SnapshotTaken += (_, e) => SnapshotReady?.Invoke(e.Filename);

        // Length arrives shortly after playback opens. Hop off the LibVLC callback
        // thread before touching the player again.
        _mp.LengthChanged += (_, e) =>
        {
            var length = e.Length;
            if (length <= 0) return;
            var entry = Entry;
            Task.Run(async () =>
            {
                if (entry is not null) LengthKnown?.Invoke(entry, length);

                var start = Interlocked.Exchange(ref _startAt, null);
                if (start is not null) SeekTo(start(length));

                if (Interlocked.Exchange(ref _pauseOnceStarted, 0) == 1)
                {
                    // Give the frame at the new position time to reach the screen before
                    // freezing on it, or the tile holds whatever was decoded first.
                    await Task.Delay(250).ConfigureAwait(false);
                    Pause();
                }
            });
        };
    }

    private Func<long, long>? _startAt;

    /// <summary>Raised once LibVLC knows how long the open stream is.</summary>
    public event Action<VideoEntry, long>? LengthKnown;

    public int Id { get; }
    public IntPtr Hwnd { get; }
    public VideoEntry? Entry { get; private set; }
    public MediaPlayer Player => _mp;

    public bool HasMedia => Entry is not null;
    public bool IsPlaying => _mp.IsPlaying;

    /// <summary>Milliseconds; LibVLC reports -1 until the stream is parsed.</summary>
    public long Time => Math.Max(0, _mp.Time);
    public long Length => Math.Max(0, _mp.Length);

    public double Fraction
    {
        get
        {
            var len = _mp.Length;
            return len <= 0 ? 0 : Math.Clamp(_mp.Time / (double)len, 0, 1);
        }
    }

    /// <summary>
    /// Desired mute state. LibVLC's own mute is unreliable before playback starts
    /// (libvlc_audio_set_mute needs a live audio output), so the wanted value is held
    /// here and pushed down whenever LibVLC is in a position to accept it.
    /// </summary>
    public bool Muted
    {
        get => _desiredMute;
        set
        {
            _desiredMute = value;
            ApplyAudioState();
        }
    }

    /// <summary>
    /// Silence is done by deselecting the audio track, not by mute or volume.
    /// <para>
    /// Both of those act on the audio output, and neither is dependable here:
    /// libvlc_audio_set_mute is documented as "does not always work", and the output
    /// resets volume to 100 when it finally comes up, after which further set_volume
    /// calls are simply dropped. Track selection is a decoder-side choice that always
    /// takes, costs nothing to reverse, and leaves video untouched — so a swap still
    /// never interrupts the picture.
    /// </para>
    /// </summary>
    private void ApplyAudioState()
    {
        if (_disposed) return;
        try
        {
            if (_desiredMute)
            {
                _mp.SetAudioTrack(DisabledTrack);
            }
            else
            {
                var track = FirstAudioTrack();
                if (track >= 0) _mp.SetAudioTrack(track);
                if (_mp.Mute) _mp.Mute = false;
                _mp.Volume = FullVolume;
            }

            if (Diag.Enabled)
                Diag.Log($"apply surface {Id} want={(_desiredMute ? "silent" : "audible")} " +
                         $"-> track={_mp.AudioTrack} vol={_mp.Volume}");
        }
        catch (Exception ex)
        {
            Diag.Log($"surface {Id}: audio state failed: {ex.Message}");
        }
    }

    /// <summary>The first real audio track, or -1 when the file carries no audio.</summary>
    private int FirstAudioTrack()
    {
        var tracks = _mp.AudioTrackDescription;
        if (tracks is null) return -1;
        foreach (var t in tracks)
            if (t.Id >= 0) return t.Id;
        return -1;
    }

    private const int DisabledTrack = -1;
    private const int FullVolume = 100;

    /// <summary>
    /// Corrects any drift between what we want and what LibVLC actually has. Called on
    /// the UI tick, so a player whose output appeared late still ends up silent.
    /// </summary>
    public void EnsureAudioState()
    {
        if (_disposed || Entry is null) return;

        var current = _mp.AudioTrack;
        if (_desiredMute)
        {
            if (current != DisabledTrack) ApplyAudioState();
        }
        else if (current < 0 && FirstAudioTrack() >= 0)
        {
            // Wants sound and has some to give, but no track is selected yet.
            ApplyAudioState();
        }
    }

    /// <summary>What LibVLC reports right now, for tracing.</summary>
    public string AudioReport => $"{Entry?.Name ?? "-"} track={_mp.AudioTrack} vol={_mp.Volume} want={(_desiredMute ? "silent" : "audible")}";

    /// <summary>
    /// Opens a video. <paramref name="startAt"/> is given the stream length once LibVLC
    /// reports it and returns where playback should sit.
    /// <para>
    /// The position is applied as a seek rather than LibVLC's <c>:start-time</c>, because
    /// that option is part of the input item and so is re-applied on every repeat: a clip
    /// opened at 90s of 120s would loop 90-120 forever and never show the first 90
    /// seconds. Seeking leaves the loop running the whole file.
    /// </para>
    /// </summary>
    public void Load(VideoEntry entry, bool autoPlay, bool loop, bool muted, Func<long, long>? startAt = null)
    {
        Entry = entry;
        _desiredMute = muted;
        _startAt = startAt;
        using var media = new Media(_vlc, new Uri(ToUri(entry.Path)));
        if (loop) media.AddOption(":input-repeat=65535");
        media.AddOption(":no-sub-autodetect-file");
        _mp.Media = media;
        ApplyAudioState();

        // Always start, even when the group is paused: a media that has never played
        // shows nothing at all, so paging while paused would black out the wall. It is
        // parked on its first frame instead, once positioned.
        _pauseOnceStarted = autoPlay ? 0 : 1;
        _mp.Play();
    }

    private int _pauseOnceStarted;

    /// <summary>Drive letters and UNC shares both round-trip through Uri, spaces escaped.</summary>
    private static string ToUri(string path) => new Uri(path).AbsoluteUri;

    public void Clear()
    {
        _mp.Stop();
        _mp.Media = null;
        Entry = null;
    }

    public void Play() { if (HasMedia) _mp.Play(); }
    public void Pause() { if (HasMedia && _mp.CanPause) _mp.SetPause(true); }

    public void SetPaused(bool paused)
    {
        if (!HasMedia) return;
        if (paused) Pause(); else Play();
    }

    public void Restart()
    {
        if (HasMedia) _mp.Time = 0;
    }

    /// <summary>Jumps to an absolute position in milliseconds.</summary>
    public void SeekTo(long milliseconds)
    {
        if (!HasMedia) return;
        var len = _mp.Length;
        var t = Math.Max(0, milliseconds);
        _mp.Time = len > 0 ? Math.Min(t, Math.Max(0, len - 500)) : t;
    }

    /// <summary>
    /// Writes the frame on screen right now to a PNG. LibVLC does this asynchronously and
    /// answers on <see cref="SnapshotReady"/>, from one of its own threads.
    /// </summary>
    public bool Snapshot(string path, uint width, uint height)
    {
        if (!HasMedia) return false;
        try { return _mp.TakeSnapshot(0, path, width, height); }
        catch (Exception ex)
        {
            Diag.Log($"surface {Id}: snapshot failed: {ex.Message}");
            return false;
        }
    }

    public event Action<string>? SnapshotReady;

    public void Seek(int seconds)
    {
        if (!HasMedia) return;
        var len = _mp.Length;
        var t = _mp.Time + seconds * 1000L;
        t = len > 0 ? Math.Clamp(t, 0, Math.Max(0, len - 500)) : Math.Max(0, t);
        _mp.Time = t;
    }

    /// <summary>Moves the native surface under a new parent and fills it. Playback is untouched.</summary>
    public void MoveTo(IntPtr parent, int width, int height)
    {
        if (_disposed) return;
        Native.SetParent(Hwnd, parent);
        Native.SetWindowPos(Hwnd, IntPtr.Zero, 0, 0, Math.Max(1, width), Math.Max(1, height),
            Native.SWP_NOZORDER | Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
    }

    public void Resize(int width, int height)
    {
        if (_disposed) return;
        Native.SetWindowPos(Hwnd, IntPtr.Zero, 0, 0, Math.Max(1, width), Math.Max(1, height),
            Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
    }

    public void Hide() => Native.ShowWindow(Hwnd, Native.SW_HIDE);

    /// <summary>Takes the surface off screen without disturbing playback.</summary>
    public void Park(IntPtr parking)
    {
        if (_disposed) return;
        Hide();
        Native.SetParent(Hwnd, parking);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _mp.Stop();
        _mp.Dispose();
        Native.DestroyWindow(Hwnd);
    }
}
