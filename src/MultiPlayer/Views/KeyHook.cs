using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using MultiPlayer.Playback;

namespace MultiPlayer.Views;

/// <summary>
/// Delivers key presses straight off the window's message queue.
/// <para>
/// Nothing in either window is focusable — buttons, the playlist and the display picker
/// all opt out so a stray Tab or Space can never land on a control instead of the
/// transport. That leaves WPF with no focused element, and WPF only routes KeyDown to a
/// focused element, so the usual PreviewKeyDown never fires. Hooking WM_KEYDOWN on the
/// HWND sidesteps focus entirely: as long as the window is active, the keys arrive.
/// </para>
/// </summary>
public static class KeyHook
{
    private const int WM_KEYDOWN = 0x0100;

    /// <summary>Calls <paramref name="handler"/> with the key and whether shift is held.</summary>
    public static void Attach(Window window, Func<Key, bool, bool> handler)
    {
        window.SourceInitialized += (_, _) =>
        {
            if (PresentationSource.FromVisual(window) is not HwndSource source)
            {
                Diag.Log($"KeyHook: no HwndSource for {window.GetType().Name}");
                return;
            }
            Diag.Log($"KeyHook: attached to {window.GetType().Name} hwnd={source.Handle:X}");
            source.AddHook((IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
            {
                // WM_KEYDOWN only: WM_SYSKEYDOWN would turn Alt+D into a plain D and
                // fight the system menu.
                if (msg != WM_KEYDOWN) return IntPtr.Zero;
                var key = KeyInterop.KeyFromVirtualKey((int)wParam);
                Diag.Log($"KeyHook: WM_KEYDOWN vk={(int)wParam} key={key}");
                if (key != Key.None && handler(key, Native.IsShiftDown())) handled = true;
                return IntPtr.Zero;
            });
        };
    }
}
