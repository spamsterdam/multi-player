using System.IO;

namespace MultiPlayer.Playback;

/// <summary>
/// Opt-in tracing. Set MULTIPLAYER_DEBUG=1 to get a log next to the temp folder;
/// off by default so the shipped app never touches disk on the input path.
/// </summary>
public static class Diag
{
    public static readonly bool Enabled =
        Environment.GetEnvironmentVariable("MULTIPLAYER_DEBUG") == "1";

    private static readonly string Path =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "multiplayer.log");

    private static readonly object Gate = new();

    private static int _headerWritten;

    public static void Log(string message)
    {
        if (!Enabled) return;

        // First line of any trace says which build produced it.
        if (Interlocked.Exchange(ref _headerWritten, 1) == 0)
            Write($"--- Multi-Video Player {MultiPlayer.BuildInfo.Version} ---");

        Write(message);
    }

    private static void Write(string message)
    {
        try
        {
            lock (Gate)
                File.AppendAllText(Path, $"{DateTime.Now:HH:mm:ss.fff}  {message}{Environment.NewLine}");
        }
        catch
        {
            // Tracing must never take the app down.
        }
    }
}
