using System.IO;

namespace MultiPlayer.Model;

public sealed class VideoEntry
{
    public VideoEntry(string path)
    {
        Path = path;
        Name = System.IO.Path.GetFileName(path);
        if (string.IsNullOrEmpty(Name)) Name = path;
    }

    /// <summary>Absolute local or UNC path, exactly as it appeared in the playlist.</summary>
    public string Path { get; }

    public string Name { get; }

    /// <summary>False once the player reports the file could not be opened.</summary>
    public bool Missing { get; set; }

    public override string ToString() => Name;
}

public static class MediaTypes
{
    /// <summary>
    /// Container extensions worth offering in a folder scan. LibVLC opens far more than
    /// this; the list only decides what a folder sweep picks up, and a playlist's own
    /// entries are always taken at face value.
    /// </summary>
    public static readonly HashSet<string> Video = new(StringComparer.OrdinalIgnoreCase)
    {
        ".avi", ".mp4", ".wmv", ".m4v", ".mkv", ".mov", ".mpg", ".rmvb", ".vob", ".flv",
        ".mts", ".ogv", ".webm", ".3gp", ".dat", ".divx", ".h264", ".m2ts", ".m2v",
        ".mp4v", ".mpeg", ".mpeg2", ".mpeg4", ".mpg2", ".qt", ".rm", ".ts", ".xvid",
        ".264", ".3g2", ".3gp2", ".3gpp",
    };

    public static bool IsVideo(string path) => Video.Contains(System.IO.Path.GetExtension(path));

    public static IEnumerable<string> ScanFolder(string folder, bool recurse)
        => Directory.EnumerateFiles(folder, "*.*", recurse ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
                    .Where(IsVideo)
                    .OrderBy(p => p, StringComparer.OrdinalIgnoreCase);
}
