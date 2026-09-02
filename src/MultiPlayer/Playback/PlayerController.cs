using System.IO;
using MultiPlayer.Model;

namespace MultiPlayer.Playback;

/// <summary>
/// Everything the two windows share: the pool of decoders, which video holds which
/// role, playlist paging, and transport. Views observe <see cref="LayoutChanged"/> to
/// re-parent surfaces and <see cref="StateChanged"/> to refresh labels.
/// </summary>
public sealed class PlayerController : IDisposable
{
    /// <summary>Primary plus a full 3x3 wall.</summary>
    public const int PoolSize = 10;

    private readonly VlcEngine _engine;
    private readonly PlayerSurface[] _pool;
    private readonly Dictionary<int, PlayerSurface> _tiles = new();

    private readonly Timeline _timeline;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> _durations =
        new(StringComparer.OrdinalIgnoreCase);

    private bool _primaryIntent = true;
    private bool _numberedIntent = true;
    private int _cursor = -1;

    public PlayerController(bool hardwareDecoding = true)
    {
        _engine = new VlcEngine(hardwareDecoding);
        Parking = Native.CreateParkingWindow();
        SurfaceHost.Parking = Parking;
        _pool = Enumerable.Range(0, PoolSize)
                          .Select(i => new PlayerSurface(_engine, Parking, i))
                          .ToArray();
        Primary = _pool[0];

        _timeline = new Timeline(running: _numberedIntent);

        foreach (var s in _pool)
        {
            s.SnapshotReady += _ => FavoritesChanged?.Invoke();
            s.LengthKnown += (entry, length) => _durations[entry.Path] = length;
        }

        Native.SurfaceClicked += OnSurfaceClicked;

        _thumbnailer = new Thumbnailer(_engine, Parking);
        _thumbnailer.Ready += _ => FavoritesChanged?.Invoke();
    }

    private readonly Thumbnailer _thumbnailer;

    /// <summary>
    /// Asks for any favorite picture that is not cached to be rebuilt from its stored
    /// timecode. Cheap when the cache is warm: it queues nothing at all.
    /// </summary>
    private void BackfillThumbnails()
    {
        for (int slot = 0; slot < FavoriteStore.Slots; slot++)
        {
            if (Favorites[slot] is not { } favorite) continue;
            var output = Favorites.ThumbnailPath(slot);
            if (File.Exists(output)) continue;
            _thumbnailer.Request(slot, favorite.Path, favorite.PositionMs, output);
        }
    }

    /// <summary>
    /// A click landing on the picture itself, rather than on a caption strip. The click
    /// arrives as a raw HWND, so it is matched back to whichever slot owns that surface.
    /// </summary>
    private void OnSurfaceClicked(IntPtr hwnd)
    {
        foreach (var key in Keys)
            if (_tiles.TryGetValue(key, out var surface) && surface.Hwnd == hwnd)
            {
                Select(key);
                return;
            }
    }

    public IntPtr Parking { get; }

    /// <summary>Raised when a surface needs to move to a different slot or window.</summary>
    public event Action? LayoutChanged;

    /// <summary>Raised when labels, times or transport state need repainting.</summary>
    public event Action? StateChanged;

    // --- state ---------------------------------------------------------------

    public PlayerSurface Primary { get; private set; }
    public ScreenMode Mode { get; private set; } = ScreenMode.Single;
    public List<VideoEntry> Playlist { get; private set; } = new();
    public string PlaylistName { get; private set; } = "no playlist";
    public int SetIndex { get; private set; }

    /// <summary>Bumped whenever the playlist contents or order change.</summary>
    public int PlaylistRevision { get; private set; }

    public FavoriteStore Favorites { get; } = new();

    /// <summary>True between pressing ` and choosing the slot to store into.</summary>
    public bool FavoriteArmed { get; private set; }

    /// <summary>Raised when a favorite is stored, cleared, or its thumbnail arrives.</summary>
    public event Action? FavoritesChanged;
    private string _lastAction = "ready";

    public string LastAction
    {
        get => _lastAction;
        private set
        {
            _lastAction = value;
            LastActionAt = DateTime.UtcNow;
        }
    }

    /// <summary>When the last action was recorded, so the readout can fade itself out.</summary>
    public DateTime LastActionAt { get; private set; } = DateTime.UtcNow;
    public int SeekStep { get; set; } = 10;
    public int ShiftSeekStep { get; set; } = 30;
    public bool Loop { get; set; } = true;
    public bool GlobalMute { get; private set; }
    public bool HardwareDecoding => _engine.HardwareDecoding;

    public int[] Keys => Layouts.For(Mode);
    public int PageSize => Keys.Length;

    public int TotalSets => Math.Max(1, (int)Math.Ceiling(Playlist.Count / (double)PageSize));

    /// <summary>Every surface, so a view can park the ones it is not showing.</summary>
    public IReadOnlyList<PlayerSurface> Pool => _pool;

    public PlayerSurface? Tile(int key) => _tiles.TryGetValue(key, out var s) ? s : null;

    public bool PrimaryPlaying => Primary.IsPlaying;
    public bool NumberedPlaying => _tiles.Values.Any(t => t.IsPlaying);

    // --- playlist ------------------------------------------------------------

    /// <summary>
    /// Replaces the playlist. <paramref name="identity"/> is the file or folder the
    /// entries came from, which keys the thumbnail cache; null for a loose selection.
    /// </summary>
    public void LoadPlaylist(IEnumerable<string> paths, string name, string? identity = null)
    {
        _playlistIdentity = identity;
        Playlist = paths.Select(p => new VideoEntry(p)).ToList();
        PlaylistName = name;
        SetIndex = 0;
        _cursor = -1;
        PlaylistRevision++;

        // Favorites belong to the playlist being opened, so anything held for the last
        // one goes. LoadPlaylistFile fills them back in from the file straight after.
        Favorites.ClearAll();
        Favorites.SetScope(identity ?? name);

        if (Playlist.Count > 0) Primary.Load(Playlist[0], _primaryIntent, Loop, muted: GlobalMute, startAt: TimelineStart());
        else Primary.Clear();

        ApplySet();
        ApplyAudio();
        LastAction = Playlist.Count > 0 ? $"loaded {Playlist.Count} videos" : "playlist empty";
        LogPlaylist("change");
        LayoutChanged?.Invoke();
        StateChanged?.Invoke();
    }

    public void LoadPlaylistFile(string file)
    {
        var document = MvpPlaylist.Load(file);
        LoadPlaylist(document.Paths, Path.GetFileName(file), Path.GetFullPath(file));

        var restored = Favorites.FromRecords(document.Favorites);
        if (restored > 0)
        {
            LastAction = $"loaded {Playlist.Count} videos, {restored} favorites";
            BackfillThumbnails();
        }
        FavoritesChanged?.Invoke();
        StateChanged?.Invoke();
    }

    /// <summary>What the thumbnail cache is keyed on: the playlist file, when there is one.</summary>
    private string? _playlistIdentity;

    public void LoadFolder(string folder, bool recurse)
        => LoadPlaylist(MediaTypes.ScanFolder(folder, recurse),
                        new DirectoryInfo(folder).Name + "\\",
                        Path.GetFullPath(folder));

    /// <summary>
    /// Writes the playlist as it now stands, deletions included, with the favorites
    /// embedded so they travel with it.
    /// </summary>
    public void SavePlaylistFile(string file)
    {
        var favorites = Favorites.ToRecords().ToList();
        MvpPlaylist.Save(file, Playlist.Select(v => v.Path), favorites);

        _playlistIdentity = Path.GetFullPath(file);
        Favorites.RescopeTo(_playlistIdentity);
        PlaylistName = Path.GetFileName(file);

        Note($"saved {Path.GetFileName(file)}"
             + (favorites.Count > 0 ? $" with {favorites.Count} favorites" : ""));
    }

    /// <summary>
    /// The entries the numbered slots should hold for the current set: this page of the
    /// playlist, minus whatever is currently primary. Skipping the primary keeps a video
    /// from occupying a numbered slot and the primary at once, wasting a slot and a
    /// decoder, and it pulls the next entry in to fill the gap. Because pages cover the
    /// whole playlist, every video can reach a numbered slot.
    /// </summary>
    /// <summary>
    /// Where a newly dealt video should open. Resolved against the stream length once
    /// LibVLC reports it, so it works on the first sighting of a file as well as later.
    /// </summary>
    private Func<long, long> TimelineStart() => length => _timeline.PositionIn(length);

    /// <summary>
    /// Appends videos to the playlist, leaving everything already on screen alone. Used
    /// both to grow a running playlist and to start one from nothing, so there is no need
    /// to open an existing playlist just to get going.
    /// </summary>
    public void AddFiles(IEnumerable<string> paths)
    {
        var candidates = paths.SelectMany(Expand).ToList();

        // Adding the same file twice would only cost a slot and a decoder.
        var seen = new HashSet<string>(Playlist.Select(v => v.Path), StringComparer.OrdinalIgnoreCase);
        var added = candidates.Where(p => seen.Add(p)).Select(p => new VideoEntry(p)).ToList();
        if (added.Count == 0)
        {
            Note(candidates.Count == 0 ? "nothing to add" : "already in the playlist");
            return;
        }

        var wasEmpty = Playlist.Count == 0;
        Playlist.AddRange(added);
        PlaylistRevision++;

        if (wasEmpty)
        {
            // Starting from nothing: name the playlist after where the files came from and
            // put the first one up as primary.
            var folder = Path.GetDirectoryName(added[0].Path);
            PlaylistName = folder is null ? "untitled" : new DirectoryInfo(folder).Name + "\\";
            _playlistIdentity ??= folder;
            Favorites.SetScope(_playlistIdentity);
            Primary.Load(added[0], _primaryIntent, Loop, muted: GlobalMute, startAt: TimelineStart());
            _cursor = -1;
        }

        ApplySet();
        ApplyAudio();
        LastAction = $"added {added.Count} ({Playlist.Count} in the playlist)";
        LogPlaylist("change");
        LayoutChanged?.Invoke();
        StateChanged?.Invoke();
    }

    /// <summary>A dropped or picked path as the videos it stands for.</summary>
    private static IEnumerable<string> Expand(string path)
    {
        if (Directory.Exists(path)) return MediaTypes.ScanFolder(path, recurse: false);

        if (MvpPlaylist.HasExtension(path))
        {
            try { return MvpPlaylist.Load(path).Paths; }
            catch { return Array.Empty<string>(); }
        }

        return new[] { path };
    }

    private VideoEntry?[] EntriesForSet()
    {
        var wanted = new VideoEntry?[PageSize];
        var used = new HashSet<VideoEntry>();
        if (Primary.Entry is { } primary) used.Add(primary);

        var cursor = SetIndex * PageSize;
        for (int i = 0; i < wanted.Length; i++)
        {
            while (cursor < Playlist.Count)
            {
                var candidate = Playlist[cursor++];
                if (!used.Add(candidate)) continue;
                wanted[i] = candidate;
                break;
            }
        }
        return wanted;
    }

    /// <summary>
    /// Binds the current set to surfaces. A surface already playing the entry a slot
    /// wants is reused exactly where it is, so paging with overlap, and switching
    /// between the 6-tile and 9-tile layouts, keeps those videos running.
    /// </summary>
    private void ApplySet()
    {
        var keys = Keys;
        var wanted = EntriesForSet();

        var claimed = new HashSet<PlayerSurface> { Primary };
        var assigned = new PlayerSurface?[keys.Length];

        // Pass 1: keep whatever is already playing the right file.
        for (int i = 0; i < keys.Length; i++)
        {
            if (wanted[i] is null) continue;
            var match = _pool.FirstOrDefault(p => !claimed.Contains(p) && ReferenceEquals(p.Entry, wanted[i]));
            if (match is null) continue;
            assigned[i] = match;
            claimed.Add(match);
        }

        // Pass 2: fill the rest from whatever is free.
        for (int i = 0; i < keys.Length; i++)
        {
            if (assigned[i] is not null) continue;
            var free = _pool.FirstOrDefault(p => !claimed.Contains(p));
            if (free is null) continue;
            assigned[i] = free;
            claimed.Add(free);
            if (wanted[i] is null) free.Clear();
            else free.Load(wanted[i]!, _numberedIntent, Loop, muted: true, startAt: TimelineStart());
        }

        // Anything left over stops, so an idle slot costs no decoder.
        foreach (var s in _pool)
            if (!claimed.Contains(s) && s.HasMedia) s.Clear();

        _tiles.Clear();
        for (int i = 0; i < keys.Length; i++)
            if (assigned[i] is not null) _tiles[keys[i]] = assigned[i]!;
    }

    /// <summary>Steps through the sets, wrapping round at either end.</summary>
    public void ShiftSet(int delta)
    {
        var total = TotalSets;
        if (total <= 1)
        {
            Note("only one set");
            return;
        }

        var next = ((SetIndex + delta) % total + total) % total;
        if (next == SetIndex) return;
        SetIndex = next;
        ApplySet();
        ApplyAudio();
        LastAction = $"loaded set {SetIndex + 1}";
        LayoutChanged?.Invoke();
        StateChanged?.Invoke();
    }

    // --- the swap ------------------------------------------------------------

    /// <summary>
    /// Exchanges the roles of the primary video and the video on <paramref name="key"/>.
    /// Only the surfaces move; neither decoder is stopped, reopened or seeked, so both
    /// videos carry on from exactly where they were.
    /// </summary>
    public void Select(int key)
    {
        if (!_tiles.TryGetValue(key, out var tile)) return;
        if (ReferenceEquals(tile, Primary)) return;

        var demoted = Primary;
        Primary = tile;
        _tiles[key] = demoted;

        _cursor = Array.IndexOf(Keys, key);
        ApplyAudio();
        LastAction = $"{key} to primary";
        LayoutChanged?.Invoke();
        StateChanged?.Invoke();
    }

    public void Cycle(int delta)
    {
        var keys = Keys;
        if (keys.Length == 0) return;
        var next = ((_cursor + delta) % keys.Length + keys.Length) % keys.Length;
        Select(keys[next]);
    }

    // --- shuffle -------------------------------------------------------------

    /// <summary>
    /// Reorders the playlist and draws a fresh set into the numbered slots. The primary
    /// is deliberately left alone: this is for changing what is on deck, not what is on
    /// screen. Videos that happen to land in the new set again keep playing untouched.
    /// </summary>
    public void Shuffle()
    {
        if (Playlist.Count < 2)
        {
            Note("nothing to shuffle");
            return;
        }

        for (int i = Playlist.Count - 1; i > 0; i--)
        {
            int j = Random.Shared.Next(i + 1);
            (Playlist[i], Playlist[j]) = (Playlist[j], Playlist[i]);
        }

        SetIndex = 0;
        _cursor = -1;
        PlaylistRevision++;
        ApplySet();
        ApplyAudio();
        LastAction = "shuffled";
        LayoutChanged?.Invoke();
        StateChanged?.Invoke();
    }

    // --- favorites -----------------------------------------------------------

    public void ArmFavorite()
    {
        FavoriteArmed = true;
        Note("press 0-9 to store the primary");
        FavoritesChanged?.Invoke();
    }

    public void ClearFavoriteArm()
    {
        if (!FavoriteArmed) return;
        FavoriteArmed = false;
        FavoritesChanged?.Invoke();
    }

    /// <summary>Stores the primary and its current position in a slot, with a frame grab.</summary>
    public void SetFavorite(int slot)
    {
        FavoriteArmed = false;

        if (Primary.Entry is not { } entry)
        {
            Note("nothing playing to store");
            FavoritesChanged?.Invoke();
            return;
        }

        var position = Primary.Time;
        Favorites.Set(slot, new Favorite { Path = entry.Path, Name = entry.Name, PositionMs = position });

        Primary.Snapshot(Favorites.ThumbnailPath(slot), 192, 108);

        LastAction = $"favorite {slot} = {entry.Name} @ {position / 1000}s";
        FavoritesChanged?.Invoke();
        StateChanged?.Invoke();
    }

    /// <summary>
    /// Brings a stored favorite up as primary at the position it was stored. If the video
    /// is already on a numbered slot it is swapped in, which keeps its decoder warm;
    /// otherwise the primary opens it directly at the offset.
    /// </summary>
    public void RecallFavorite(int slot)
    {
        FavoriteArmed = false;

        if (Favorites[slot] is not { } favorite)
        {
            Note($"favorite {slot} is empty");
            return;
        }

        // Prefer the playlist's own entry so set paging keeps recognising it.
        var entry = Playlist.FirstOrDefault(v => string.Equals(v.Path, favorite.Path, StringComparison.OrdinalIgnoreCase))
                    ?? new VideoEntry(favorite.Path);

        if (ReferenceEquals(Primary.Entry, entry))
        {
            Primary.SeekTo(favorite.PositionMs);
            Note($"favorite {slot}: {favorite.Name}");
            return;
        }

        var onTile = Keys.FirstOrDefault(k => ReferenceEquals(_tiles.GetValueOrDefault(k)?.Entry, entry), -1);
        if (onTile > 0)
        {
            Select(onTile);
            Primary.SeekTo(favorite.PositionMs);
            Note($"favorite {slot}: {favorite.Name}");
            return;
        }

        Primary.Load(entry, _primaryIntent, Loop, muted: GlobalMute, startAt: _ => favorite.PositionMs);
        ApplySet();
        ApplyAudio();
        LastAction = $"favorite {slot}: {favorite.Name}";
        LayoutChanged?.Invoke();
        StateChanged?.Invoke();
    }

    // --- removing entries ----------------------------------------------------

    /// <summary>True between pressing Delete and choosing what to drop.</summary>
    public bool RemoveArmed { get; private set; }

    public void ArmRemove()
    {
        RemoveArmed = true;
        FavoriteArmed = false;
        Note("press a keypad digit to remove that video, or a row digit to clear a favorite");
        FavoritesChanged?.Invoke();
    }

    public void ClearRemoveArm()
    {
        if (!RemoveArmed) return;
        RemoveArmed = false;
        FavoritesChanged?.Invoke();
    }

    /// <summary>
    /// Drops the video on a numbered slot from the playlist. The slot refills from the
    /// current page, so the wall never leaves a hole.
    /// </summary>
    public void RemoveFromPlaylist(int key)
    {
        RemoveArmed = false;

        if (Tile(key)?.Entry is not { } entry)
        {
            Note($"nothing on {key} to remove");
            return;
        }

        Playlist.Remove(entry);
        PlaylistRevision++;
        SetIndex = Math.Clamp(SetIndex, 0, TotalSets - 1);
        ApplySet();
        ApplyAudio();
        LastAction = $"removed {entry.Name} ({Playlist.Count} left)";
        LayoutChanged?.Invoke();
        StateChanged?.Invoke();
    }

    public void ClearFavorite(int slot)
    {
        RemoveArmed = false;
        if (Favorites[slot] is null)
        {
            Note($"favorite {slot} is already empty");
            return;
        }
        Favorites.Clear(slot);
        LastAction = $"cleared favorite {slot}";
        FavoritesChanged?.Invoke();
        StateChanged?.Invoke();
    }

    // --- screen mode ---------------------------------------------------------

    public void SetMode(ScreenMode mode)
    {
        if (Mode == mode) return;
        Mode = mode;
        SetIndex = Math.Clamp(SetIndex, 0, TotalSets - 1);
        ApplySet();
        ApplyAudio();
        LastAction = mode == ScreenMode.Multi ? "multi-screen" : "single screen";
        LayoutChanged?.Invoke();
        StateChanged?.Invoke();
    }

    public void ToggleMode() => SetMode(Mode == ScreenMode.Multi ? ScreenMode.Single : ScreenMode.Multi);

    // --- transport -----------------------------------------------------------

    /// <summary>Audio follows the primary role, never a particular file.</summary>
    private void ApplyAudio()
    {
        foreach (var s in _pool) s.Muted = !ReferenceEquals(s, Primary) || GlobalMute;
    }

    /// <summary>
    /// Re-asserts the wanted audio state. A player that was still starting up when a
    /// swap happened would otherwise come up audible, because LibVLC silently ignores
    /// mute until it has an audio output.
    /// </summary>
    private void ReconcileAudio()
    {
        foreach (var s in _pool) s.EnsureAudioState();
    }

    public void ToggleMute()
    {
        GlobalMute = !GlobalMute;
        ApplyAudio();
        Note(GlobalMute ? "muted" : "audio on primary");
    }

    public void TogglePrimary()
    {
        _primaryIntent = !Primary.IsPlaying;
        Primary.SetPaused(!_primaryIntent);
        Note(_primaryIntent ? "primary playing" : "primary paused");
    }

    public void ToggleNumbered()
    {
        _numberedIntent = !NumberedPlaying;
        foreach (var t in _tiles.Values) t.SetPaused(!_numberedIntent);
        // Hold the shared clock while the wall is stopped, so paging after a pause does
        // not drop the next set in further ahead than the videos you were looking at.
        _timeline.SetRunning(_numberedIntent);
        Note(_numberedIntent ? "numbered playing" : "numbered paused");
    }

    public void SeekPrimary(int seconds)
    {
        Primary.Seek(seconds);
        Note(Signed(seconds) + " primary");
    }

    public void SeekNumbered(int seconds)
    {
        foreach (var t in _tiles.Values) t.Seek(seconds);
        // Move the clock with them, so the next set is dealt in at the same offset.
        _timeline.Shift(seconds * 1000L);
        Note(Signed(seconds) + " numbered");
    }

    public void RestartPrimary()
    {
        Primary.Restart();
        Note("primary to start");
    }

    public void RestartNumbered()
    {
        foreach (var t in _tiles.Values) t.Restart();
        _timeline.Reset();
        Note("numbered to start");
    }

    private static string Signed(int s) => (s > 0 ? "+" : "-") + Math.Abs(s) + "s";

    private int _ticks;

    // --- hiding ---------------------------------------------------------------

    private bool _suspended;
    private bool _suspendedPrimary;
    private bool _suspendedNumbered;

    /// <summary>Freezes everything, remembering what was running so it can be put back.</summary>
    public void SuspendAll()
    {
        if (_suspended) return;
        _suspended = true;
        _suspendedPrimary = PrimaryPlaying;
        _suspendedNumbered = NumberedPlaying;

        Primary.Pause();
        foreach (var t in _tiles.Values) t.Pause();
        _timeline.SetRunning(false);
        StateChanged?.Invoke();
    }

    /// <summary>Puts back exactly what was playing before the hide.</summary>
    public void ResumeAll()
    {
        if (!_suspended) return;
        _suspended = false;

        Primary.SetPaused(!_suspendedPrimary);
        foreach (var t in _tiles.Values) t.SetPaused(!_suspendedNumbered);
        _timeline.SetRunning(_suspendedNumbered);
        Note("back");
    }

    public void Tick()
    {
        ReconcileAudio();
        if (Diag.Enabled && ++_ticks % 20 == 0) LogAudio("tick");
        StateChanged?.Invoke();
    }

    /// <summary>Dumps the playlist and what is on air, for tracing.</summary>
    public void LogPlaylist(string why)
    {
        if (!Diag.Enabled) return;
        Diag.Log($"playlist[{why}] '{PlaylistName}' n={Playlist.Count} " +
                 $"primary={Primary.Entry?.Name ?? "-"} " +
                 $"tiles=[{string.Join(" ", Keys.Select(k => k + ":" + (Tile(k)?.Entry?.Name ?? "-")))}] " +
                 $"entries=[{string.Join(" ", Playlist.Select(v => v.Name))}]");
    }

    /// <summary>Dumps what LibVLC actually reports per player, for tracing.</summary>
    public void LogAudio(string when)
    {
        foreach (var s in _pool)
            Diag.Log($"audio[{when}] surface {s.Id}: {s.AudioReport}");
    }

    public void Note(string action)
    {
        LastAction = action;
        StateChanged?.Invoke();
    }

    public void Dispose()
    {
        Native.SurfaceClicked -= OnSurfaceClicked;
        _thumbnailer.Dispose();
        foreach (var s in _pool) s.Dispose();
        _engine.Dispose();
        if (Parking != IntPtr.Zero) Native.DestroyWindow(Parking);
    }
}
