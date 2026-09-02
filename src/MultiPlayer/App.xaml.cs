using System.IO;
using System.Windows;
using MultiPlayer.Model;
using MultiPlayer.Playback;
using MultiPlayer.Views;

namespace MultiPlayer;

public partial class App : Application
{
    private PlayerController? _controller;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Paths can be handed in on the command line, so the app can be the default
        // handler for .mvp and can take a group of files dropped onto its icon.
        var opened = e.Args.Where(a => !a.StartsWith('-')).ToArray();
        var softwareDecoding = e.Args.Any(a => a.Equals("--no-hw", StringComparison.OrdinalIgnoreCase));

        try
        {
            _controller = new PlayerController(hardwareDecoding: !softwareDecoding);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                "LibVLC could not be initialised.\n\n" + ex.Message +
                "\n\nThe libvlc folder must sit next to MultiPlayer.exe.",
                "Multi-Video Player", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        var window = new MainWindow(_controller);
        MainWindow = window;
        window.Show();

        if (opened.Length > 0) LoadFromCommandLine(_controller, opened);
    }

    /// <summary>
    /// A playlist or folder as the first argument opens it; anything after that is added
    /// to it. A list of plain video files just becomes the playlist.
    /// </summary>
    private static void LoadFromCommandLine(PlayerController controller, string[] paths)
    {
        try
        {
            var first = paths[0];
            var isPlaylist = MvpPlaylist.HasExtension(first);

            if (Directory.Exists(first)) controller.LoadFolder(first, recurse: false);
            else if (isPlaylist) controller.LoadPlaylistFile(first);
            else
            {
                controller.AddFiles(paths);
                return;
            }

            if (paths.Length > 1) controller.AddFiles(paths.Skip(1));
        }
        catch (Exception ex)
        {
            controller.Note("could not open: " + ex.Message);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _controller?.Dispose();
        base.OnExit(e);
    }
}
