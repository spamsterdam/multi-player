using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using MultiPlayer.Playback;

namespace MultiPlayer.Views;

/// <summary>
/// The second screen. In multi-screen mode this window hosts the entire control surface —
/// playlist, header, the 3x3 wall, both transports, favorites and legend — which is moved
/// across from the main window rather than rebuilt here. The primary display is then left
/// carrying nothing but picture.
/// </summary>
public sealed class WallWindow : Window
{
    private readonly ContentControl _slot = new();
    private readonly Border _curtain = new()
    {
        Background = Brushes.Black,
        Visibility = Visibility.Collapsed,
    };

    private MonitorInfo? _target;

    public WallWindow(PlayerController controller, IShellCommands shell)
    {
        Title = "Multi-Video Player - Wall";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.Manual;
        ShowInTaskbar = false;
        // The operator works wherever the control surface is; opening the wall must not yank focus
        // across displays mid-keystroke.
        ShowActivated = false;
        Background = Brushes.Black;
        UseLayoutRounding = true;
        AllowDrop = true;

        var root = new Grid();
        root.Children.Add(_slot);
        root.Children.Add(_curtain);   // last child, so it covers the control surface when shown
        Content = root;

        KeyHook.Attach(this, (key, shift) => KeyRouter.Handle(controller, key, shift, shell));
    }

    /// <summary>Takes the control surface handed over from the main window.</summary>
    public void Host(UIElement? controls) => _slot.Content = controls;

    /// <summary>Blacks the window out without disturbing what it holds.</summary>
    public void SetCurtain(bool on)
        => _curtain.Visibility = on ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// Positions the window over a display in physical pixels. WPF's Left/Top are DIPs
    /// against a DPI that may not be this monitor's, so placement is done with
    /// SetWindowPos once the HWND exists and before anything is painted.
    /// </summary>
    public void PlaceOn(MonitorInfo monitor)
    {
        _target = monitor;
        Apply();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        Apply();
    }

    private void Apply()
    {
        if (_target is null) return;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        Native.SetWindowPos(hwnd, IntPtr.Zero, _target.X, _target.Y, _target.Width, _target.Height,
            Native.SWP_NOZORDER | Native.SWP_NOACTIVATE | Native.SWP_FRAMECHANGED);
    }
}
