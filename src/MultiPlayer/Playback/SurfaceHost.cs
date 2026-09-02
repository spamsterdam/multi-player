using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Media;

namespace MultiPlayer.Playback;

/// <summary>
/// A slot in the WPF layout that a video surface can be moved into. The host owns only
/// an empty container HWND; the surface itself belongs to the player pool and is merely
/// re-parented here, so moving a video between slots (or between windows, or between
/// displays) is one SetParent call and never touches the decoder.
/// </summary>
public sealed class SurfaceHost : HwndHost
{
    /// <summary>Hidden window that holds surfaces which are currently off screen.</summary>
    public static IntPtr Parking { get; set; }

    private IntPtr _container;
    private PlayerSurface? _surface;

    public SurfaceHost()
    {
        SizeChanged += (_, _) => Relayout();
        DpiChanged += (_, _) => Relayout();
    }

    public PlayerSurface? Surface => _surface;

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        _container = Native.CreateSurface(hwndParent.Handle, visible: true);
        Attach();
        return new HandleRef(this, _container);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        // A container going away must never take a live decoder's window with it.
        Park();
        if (_container != IntPtr.Zero)
        {
            Native.DestroyWindow(_container);
            _container = IntPtr.Zero;
        }
    }

    /// <summary>
    /// Points this slot at a surface. Callers re-point every slot before parking
    /// leftovers, so a swap moves each surface straight from its old container to its
    /// new one — no hop through the parking window, no flicker.
    /// </summary>
    public void SetSurface(PlayerSurface? surface)
    {
        if (ReferenceEquals(_surface, surface)) { Relayout(); return; }
        _surface = surface;
        Attach();
    }

    private void Attach()
    {
        if (_surface is null || _container == IntPtr.Zero) return;
        var (w, h) = ClientSize();
        _surface.MoveTo(_container, w, h);
    }

    public void Park()
    {
        if (_surface is null) return;
        _surface.Hide();
        Native.SetParent(_surface.Hwnd, Parking);
        _surface = null;
    }

    public void Relayout()
    {
        if (_surface is null || _container == IntPtr.Zero) return;
        var (w, h) = ClientSize();
        _surface.Resize(w, h);
    }

    /// <summary>
    /// The slot size in physical pixels, taken from WPF's arranged size rather than the
    /// container HWND: HwndHost moves its window during arrange, after SizeChanged has
    /// already fired, so reading the container back gives the previous layout's size.
    /// </summary>
    private (int Width, int Height) ClientSize()
    {
        if (ActualWidth > 0 && ActualHeight > 0)
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            return (Math.Max(1, (int)Math.Round(ActualWidth * dpi.DpiScaleX)),
                    Math.Max(1, (int)Math.Round(ActualHeight * dpi.DpiScaleY)));
        }
        if (_container != IntPtr.Zero && Native.GetClientRect(_container, out var r))
            return (Math.Max(1, r.Right - r.Left), Math.Max(1, r.Bottom - r.Top));
        return (16, 9);
    }
}
