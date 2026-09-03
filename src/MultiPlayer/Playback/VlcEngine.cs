using LibVLCSharp.Shared;

namespace MultiPlayer.Playback;

/// <summary>Owns the single process-wide LibVLC instance and the options every player inherits.</summary>
public sealed class VlcEngine : IDisposable
{
    public LibVLC Vlc { get; }
    public bool HardwareDecoding { get; }

    public VlcEngine(bool hardwareDecoding = true)
    {
        Core.Initialize();
        HardwareDecoding = hardwareDecoding;

        var options = new List<string>
        {
            "--no-osd",                 // nothing drawn over the frame but our own controls
            "--no-video-title-show",
            "--no-spu",                 // no subtitle rendering pass
            "--no-sub-autodetect-file",
            "--no-snapshot-preview",
            "--no-stats",
            "--no-lua",
            "--no-interact",
            "--quiet",
            "--drop-late-frames",       // ten streams: keep wall-clock, lose the odd frame
            "--file-caching=400",
            "--network-caching=1200",   // playlists routinely point at UNC shares
            "--vout=direct3d11",
        };

        // d3d11va keeps decoded frames in GPU memory, so ten 1080p streams cost
        // decoder blocks rather than PCIe bandwidth and CPU colour conversion.
        options.Add(hardwareDecoding ? "--avcodec-hw=d3d11va" : "--avcodec-hw=none");

        Vlc = new LibVLC(options.ToArray());
    }

    public void Dispose() => Vlc.Dispose();
}
