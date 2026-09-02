namespace MultiPlayer.Playback;

/// <summary>
/// A single clock the numbered videos are positioned against, so the wall behaves as if
/// every video in the playlist had been looping since the app opened.
/// <para>
/// A video newly dealt into a slot does not start at zero; it starts at
/// <c>clock mod duration</c>, which is where it would have been had it been running all
/// along. Once positioned it simply plays on, staying in step because both the clock and
/// playback advance in real time.
/// </para>
/// <para>
/// The clock stops while the numbered videos are paused, so pausing the wall and paging
/// later does not make the next set jump forward.
/// </para>
/// </summary>
public sealed class Timeline
{
    private readonly object _gate = new();
    private long _accumulated;
    private DateTime? _runningSince;

    public Timeline(bool running)
    {
        if (running) _runningSince = DateTime.UtcNow;
    }

    /// <summary>Milliseconds on the shared clock. May be negative after seeking back.</summary>
    public long Now
    {
        get
        {
            lock (_gate)
                return _accumulated + (_runningSince is { } since
                    ? (long)(DateTime.UtcNow - since).TotalMilliseconds
                    : 0);
        }
    }

    public void SetRunning(bool running)
    {
        lock (_gate)
        {
            if (running)
            {
                _runningSince ??= DateTime.UtcNow;
            }
            else if (_runningSince is { } since)
            {
                _accumulated += (long)(DateTime.UtcNow - since).TotalMilliseconds;
                _runningSince = null;
            }
        }
    }

    /// <summary>Nudges the clock, so later loads land where the visible videos now are.</summary>
    public void Shift(long milliseconds)
    {
        lock (_gate) _accumulated += milliseconds;
    }

    public void Reset()
    {
        lock (_gate)
        {
            _accumulated = 0;
            if (_runningSince is not null) _runningSince = DateTime.UtcNow;
        }
    }

    /// <summary>Where a stream of this length would be right now.</summary>
    public long PositionIn(long lengthMs)
    {
        if (lengthMs <= 0) return 0;
        var now = Now % lengthMs;
        return now < 0 ? now + lengthMs : now;
    }
}
