using System.Collections.Concurrent;
using System.IO;
using LibVLCSharp.Shared;

namespace MultiPlayer.Playback;

/// <summary>
/// Rebuilds a favorite's stored frame from its timecode.
/// <para>
/// The timecode in the playlist is the real record; the PNG beside it is only a cache. So
/// a playlist opened on a machine that has never seen it — or after the cache is deleted —
/// gets its pictures back by decoding one frame at the stored position.
/// </para>
/// <para>
/// This runs one file at a time on a background thread, and only for slots whose picture
/// is actually missing. Doing it for every favorite on every launch would be the wrong
/// trade: playlists commonly point at UNC shares, where ten opens would cost seconds and
/// compete with the ten streams already decoding.
/// </para>
/// </summary>
public sealed class Thumbnailer : IDisposable
{
    private readonly record struct Job(int Slot, string Path, long PositionMs, string Output);

    private readonly LibVLC _vlc;
    private readonly IntPtr _surface;
    private readonly MediaPlayer _mp;
    private readonly ConcurrentQueue<Job> _queue = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly CancellationTokenSource _cancel = new();
    private bool _disposed;

    public Thumbnailer(VlcEngine engine, IntPtr parking)
    {
        _vlc = engine.Vlc;
        // Renders into a hidden window parented off screen, so nothing ever flashes up.
        _surface = Native.CreateSurface(parking);
        Native.SetWindowPos(_surface, IntPtr.Zero, 0, 0, 320, 180,
            Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);

        _mp = new MediaPlayer(_vlc)
        {
            EnableHardwareDecoding = engine.HardwareDecoding,
            Hwnd = _surface,
            EnableKeyInput = false,
            EnableMouseInput = false,
        };

        _ = Task.Run(WorkAsync);
    }

    /// <summary>Raised on a background thread once a slot's picture is on disk.</summary>
    public event Action<int>? Ready;

    public void Request(int slot, string path, long positionMs, string output)
    {
        if (_disposed) return;
        _queue.Enqueue(new Job(slot, path, positionMs, output));
        _signal.Release();
    }

    private async Task WorkAsync()
    {
        var token = _cancel.Token;
        while (!token.IsCancellationRequested)
        {
            try
            {
                await _signal.WaitAsync(token).ConfigureAwait(false);
                if (!_queue.TryDequeue(out var job)) continue;
                await RenderAsync(job, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                Diag.Log($"thumbnailer: {ex.Message}");
            }
        }
    }

    private async Task RenderAsync(Job job, CancellationToken token)
    {
        if (!File.Exists(job.Path))
        {
            Diag.Log($"thumbnailer: slot {job.Slot} missing {job.Path}");
            return;
        }

        try
        {
            using var media = new Media(_vlc, new Uri(new Uri(job.Path).AbsoluteUri));
            media.AddOption(":no-audio");           // no output, no interference with the mix
            media.AddOption(":no-sub-autodetect-file");
            _mp.Media = media;
            _mp.Play();

            if (!await WaitAsync(() => _mp.IsPlaying && _mp.Length > 0, TimeSpan.FromSeconds(8), token))
            {
                Diag.Log($"thumbnailer: slot {job.Slot} never opened");
                return;
            }

            var target = Math.Clamp(job.PositionMs, 0, Math.Max(0, _mp.Length - 500));
            _mp.Time = target;

            // Let the seek land and a frame reach the (hidden) output before grabbing it.
            await WaitAsync(() => Math.Abs(_mp.Time - target) < 1500, TimeSpan.FromSeconds(4), token);
            await Task.Delay(350, token).ConfigureAwait(false);

            Directory.CreateDirectory(Path.GetDirectoryName(job.Output)!);
            if (!_mp.TakeSnapshot(0, job.Output, 192, 108))
            {
                Diag.Log($"thumbnailer: slot {job.Slot} snapshot refused");
                return;
            }

            if (await WaitAsync(() => File.Exists(job.Output), TimeSpan.FromSeconds(5), token))
            {
                await Task.Delay(120, token).ConfigureAwait(false);  // let the write finish
                Diag.Log($"thumbnailer: slot {job.Slot} rebuilt at {target}ms");
                Ready?.Invoke(job.Slot);
            }
        }
        finally
        {
            try { _mp.Stop(); } catch { }
        }
    }

    private static async Task<bool> WaitAsync(Func<bool> condition, TimeSpan timeout, CancellationToken token)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            await Task.Delay(50, token).ConfigureAwait(false);
        }
        return false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cancel.Cancel();
        try { _mp.Stop(); } catch { }
        _mp.Dispose();
        Native.DestroyWindow(_surface);
        _cancel.Dispose();
        _signal.Dispose();
    }
}
