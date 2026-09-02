using System.IO;
using System.Text;

namespace MultiPlayer.Model;

/// <summary>A video and the position it was marked at.</summary>
public sealed class Favorite
{
    public string Path { get; set; } = "";
    public string Name { get; set; } = "";

    /// <summary>Playback position when the favorite was stored, in milliseconds.</summary>
    public long PositionMs { get; set; }
}

/// <summary>
/// Ten slots addressed by the number row.
/// <para>
/// Favorites belong to a playlist, not to the machine: they are read out of the
/// <c>.mvp</c> when it is opened and written back into it when it is saved. Nothing is
/// carried over between runs on its own, so opening a playlist gives you exactly the
/// favorites that playlist was saved with.
/// </para>
/// <para>
/// The stored frames are the one exception. They are a picture cache, not state, kept in
/// a hidden <c>.multiplayer</c> folder beside the playlist so they travel with it and stay
/// out of the way. Losing that folder costs nothing: the timecode in the playlist is the
/// record, and any missing frame is decoded again from it.
/// </para>
/// </summary>
public sealed class FavoriteStore
{
    public const int Slots = 10;

    /// <summary>
    /// The order the slots are shown in, left to right: the number row's own order, so
    /// the bar reads the way the keys sit under your hand, with 0 at the far end.
    /// </summary>
    public static readonly int[] DisplayOrder = { 1, 2, 3, 4, 5, 6, 7, 8, 9, 0 };

    private const string CacheFolder = ".multiplayer";

    private readonly Favorite?[] _items = new Favorite?[Slots];
    private string _cacheDirectory = FallbackDirectory;
    private string _prefix = "unsaved";

    /// <summary>Used when the playlist's own folder cannot be written to.</summary>
    private static string FallbackDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), CacheFolder);

    public Favorite? this[int slot] => slot >= 0 && slot < Slots ? _items[slot] : null;

    public bool Any => _items.Any(f => f is not null);

    public string ThumbnailPath(int slot) => Path.Combine(_cacheDirectory, $"{_prefix}.fav-{slot}.png");

    /// <summary>
    /// Points the picture cache at a hidden folder beside the playlist, so the frames sit
    /// with the media they came from and move with it. Falls back to LocalAppData when
    /// that folder cannot be written — a read-only share, say.
    /// </summary>
    public void SetScope(string? playlistIdentity)
    {
        _prefix = Sanitize(playlistIdentity is null
            ? "unsaved"
            : Directory.Exists(playlistIdentity)
                ? new DirectoryInfo(playlistIdentity).Name
                : Path.GetFileNameWithoutExtension(playlistIdentity));

        if (playlistIdentity is not null && TryUseFolderBeside(playlistIdentity)) return;

        _cacheDirectory = FallbackDirectory;
        TryCreate(_cacheDirectory);
    }

    private bool TryUseFolderBeside(string playlistIdentity)
    {
        try
        {
            var folder = Directory.Exists(playlistIdentity)
                ? playlistIdentity
                : Path.GetDirectoryName(playlistIdentity);
            if (string.IsNullOrEmpty(folder)) return false;

            var cache = Path.Combine(folder, CacheFolder);
            var info = Directory.CreateDirectory(cache);
            // A leading dot hides nothing on Windows; the attribute does.
            if (!info.Attributes.HasFlag(FileAttributes.Hidden))
                info.Attributes |= FileAttributes.Hidden;

            _cacheDirectory = cache;
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void Set(int slot, Favorite favorite)
    {
        if (slot < 0 || slot >= Slots) return;
        TryCreate(_cacheDirectory);
        _items[slot] = favorite;
    }

    public void Clear(int slot)
    {
        if (slot < 0 || slot >= Slots) return;
        _items[slot] = null;
        TryDelete(ThumbnailPath(slot));
    }

    public void ClearAll()
    {
        for (int i = 0; i < Slots; i++) _items[i] = null;
    }

    /// <summary>The favorites as they should be written into a playlist file.</summary>
    public IEnumerable<MvpFavorite> ToRecords()
    {
        for (int i = 0; i < Slots; i++)
            if (_items[i] is { } f)
                yield return new MvpFavorite(i, f.PositionMs, f.Path);
    }

    /// <summary>Replaces the slots with what a playlist file carried. Returns how many.</summary>
    public int FromRecords(IEnumerable<MvpFavorite> records)
    {
        ClearAll();
        int count = 0;
        foreach (var r in records)
        {
            if (r.Slot < 0 || r.Slot >= Slots) continue;
            _items[r.Slot] = new Favorite
            {
                Path = r.Path,
                Name = Path.GetFileName(r.Path),
                PositionMs = r.PositionMs,
            };
            count++;
        }
        return count;
    }

    /// <summary>
    /// Moves the cached frames to follow a playlist saved under a new name, so a
    /// Save As does not leave the bar blank.
    /// </summary>
    public void RescopeTo(string playlistIdentity)
    {
        var previous = Enumerable.Range(0, Slots).Select(ThumbnailPath).ToArray();
        SetScope(playlistIdentity);

        try
        {
            for (int slot = 0; slot < Slots; slot++)
            {
                var to = ThumbnailPath(slot);
                if (!File.Exists(previous[slot]) ||
                    string.Equals(previous[slot], to, StringComparison.OrdinalIgnoreCase)) continue;
                File.Copy(previous[slot], to, overwrite: true);
            }
        }
        catch
        {
            // Pictures are a nicety, and anything lost here is rebuilt from its timecode.
        }
    }

    private static void TryCreate(string directory)
    {
        try { Directory.CreateDirectory(directory); } catch { }
    }

    /// <summary>Keeps a playlist name usable as part of a file name.</summary>
    private static string Sanitize(string name)
    {
        var cleaned = new StringBuilder(name.Length);
        foreach (var c in name)
            cleaned.Append(Array.IndexOf(Path.GetInvalidFileNameChars(), c) >= 0 ? '_' : c);
        var result = cleaned.ToString().Trim();
        return result.Length == 0 ? "unsaved" : result;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
