using System.Globalization;
using System.IO;
using System.Text;

namespace MultiPlayer.Model;

/// <summary>A favorite as it is written into a playlist.</summary>
public readonly record struct MvpFavorite(int Slot, long PositionMs, string Path);

/// <summary>What a playlist file contains: the entries, plus any favorites stored with it.</summary>
public sealed class MvpDocument
{
    public List<string> Paths { get; } = new();
    public List<MvpFavorite> Favorites { get; } = new();
}

/// <summary>
/// Reader and writer for the <c>.mvp</c> playlist format.
/// <para>
/// A plain text file, one absolute path per line, under a <c>#MVP</c> signature. Lines
/// beginning with <c>#</c> are directives rather than entries, which is what lets extra
/// information travel in the same file without a reader mistaking it for a video. The one
/// directive so far is <c>#FAV</c>, which carries the favorites.
/// </para>
/// <code>
/// #MVP
/// D:\clips\one.mp4
/// D:\clips\two.mp4
/// #FAV 1 45000 D:\clips\two.mp4
/// </code>
/// <para>
/// Written as UTF-8 with no byte order mark and CRLF endings, including after the last
/// line. Reading is deliberately more forgiving: a BOM, bare LF endings, blank lines and
/// unknown directives are all accepted.
/// </para>
/// </summary>
public static class MvpPlaylist
{
    public const string Extension = ".mvp";
    public const string Signature = "#MVP";

    private const string FavoriteDirective = "#FAV";

    public static bool HasExtension(string path)
        => System.IO.Path.GetExtension(path).Equals(Extension, StringComparison.OrdinalIgnoreCase);

    public static string Build(IEnumerable<string> paths, IEnumerable<MvpFavorite>? favorites = null)
    {
        var sb = new StringBuilder();
        sb.Append(Signature).Append("\r\n");
        foreach (var p in paths) sb.Append(p).Append("\r\n");

        foreach (var f in favorites ?? Enumerable.Empty<MvpFavorite>())
            sb.Append(FavoriteDirective).Append(' ')
              .Append(f.Slot.ToString(CultureInfo.InvariantCulture)).Append(' ')
              .Append(f.PositionMs.ToString(CultureInfo.InvariantCulture)).Append(' ')
              .Append(f.Path).Append("\r\n");

        return sb.ToString();
    }

    public static void Save(string file, IEnumerable<string> paths, IEnumerable<MvpFavorite>? favorites = null)
        => File.WriteAllText(file, Build(paths, favorites),
                             new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

    public static MvpDocument Parse(string text)
    {
        if (text.Length > 0 && text[0] == '\uFEFF') text = text[1..];
        var lines = text.Split('\n');
        if (lines.Length == 0 || !lines[0].Trim().Equals(Signature, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Not a playlist (missing {Signature} signature).");

        var doc = new MvpDocument();
        for (int i = 1; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0) continue;

            if (line.StartsWith('#'))
            {
                if (TryParseFavorite(line, out var favorite)) doc.Favorites.Add(favorite);
                continue;
            }
            doc.Paths.Add(line);
        }
        return doc;
    }

    private static bool TryParseFavorite(string line, out MvpFavorite favorite)
    {
        favorite = default;
        if (!line.StartsWith(FavoriteDirective + " ", StringComparison.OrdinalIgnoreCase)) return false;

        // "#FAV <slot> <ms> <path>" — the path is taken whole so spaces survive.
        var rest = line[(FavoriteDirective.Length + 1)..];
        var parts = rest.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3) return false;
        if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var slot)) return false;
        if (!long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var position)) return false;

        favorite = new MvpFavorite(slot, position, parts[2].Trim());
        return true;
    }

    public static MvpDocument Load(string file) => Parse(File.ReadAllText(file));
}
