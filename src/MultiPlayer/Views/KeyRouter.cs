using System.Windows.Input;
using MultiPlayer.Playback;

namespace MultiPlayer.Views;

/// <summary>
/// The single keyboard map, shared by the control window and the wall so the same
/// keystroke does the same thing whichever window happens to have focus.
/// <para>
/// The two rows of digits do different jobs on purpose: the <b>keypad</b> drives the
/// numbered slots, matching their on-screen 10-key layout, while the <b>number row</b>
/// addresses the ten favorites.
/// </para>
/// </summary>
public static class KeyRouter
{
    /// <summary>Number row 0-9: the favorite slots.</summary>
    public static int? RowDigit(Key key) => key is >= Key.D0 and <= Key.D9 ? key - Key.D0 : null;

    /// <summary>Keypad 1-9: the numbered video slots.</summary>
    public static int? PadDigit(Key key) => key is >= Key.NumPad1 and <= Key.NumPad9 ? key - Key.NumPad0 : null;

    public static bool Handle(PlayerController c, Key key, bool shift, IShellCommands shell)
    {
        // While hidden the app answers to nothing but the unlock sequence.
        if (shell.IsHidden)
        {
            shell.HiddenKey(key);
            return true;
        }

        // ` arms the next number-row press to store rather than recall.
        if (key == Key.OemTilde)
        {
            c.ArmFavorite();
            return true;
        }

        // Delete arms the next digit to remove rather than select.
        if (key == Key.Delete)
        {
            c.ArmRemove();
            return true;
        }

        if (key == Key.Escape && (c.FavoriteArmed || c.RemoveArmed))
        {
            c.ClearFavoriteArm();
            c.ClearRemoveArm();
            c.Note("cancelled");
            return true;
        }

        if (RowDigit(key) is int slot)
        {
            if (c.RemoveArmed) c.ClearFavorite(slot);
            else if (c.FavoriteArmed) c.SetFavorite(slot);
            else c.RecallFavorite(slot);
            return true;
        }

        if (PadDigit(key) is int pad)
        {
            c.ClearFavoriteArm();
            if (c.RemoveArmed) c.RemoveFromPlaylist(pad);
            else if (c.Tile(pad) is null) c.Note($"{pad} is not in this layout");
            else c.Select(pad);
            return true;
        }

        // Anything else cancels a half-finished favorite or removal.
        c.ClearFavoriteArm();
        c.ClearRemoveArm();

        var step = shift ? c.ShiftSeekStep : c.SeekStep;

        switch (key)
        {
            // primary transport
            case Key.A: c.RestartPrimary(); return true;
            case Key.Z: c.SeekPrimary(-step); return true;
            case Key.X: c.TogglePrimary(); return true;
            case Key.C: c.SeekPrimary(step); return true;

            // numbered transport
            case Key.K: c.RestartNumbered(); return true;
            case Key.OemComma: c.SeekNumbered(-step); return true;
            case Key.OemPeriod:
            case Key.Decimal: c.ToggleNumbered(); return true;
            case Key.OemQuestion:
            case Key.Divide: c.SeekNumbered(step); return true;

            case Key.Right: c.Cycle(1); return true;
            case Key.Left: c.Cycle(-1); return true;

            case Key.Down:
            case Key.PageDown: c.ShiftSet(1); return true;
            case Key.Up:
            case Key.PageUp: c.ShiftSet(-1); return true;

            case Key.Space:
                if (shift) c.ToggleNumbered(); else c.TogglePrimary();
                return true;

            case Key.R: c.Shuffle(); return true;
            case Key.T: shell.ToggleAutoAdvance(); return true;
            case Key.F: shell.ToggleFullscreen(); return true;
            case Key.M: c.ToggleMute(); return true;
            case Key.D: shell.ToggleScreenMode(); return true;
            case Key.H: shell.ToggleCaptions(); return true;
            case Key.F1: shell.ToggleLegend(); return true;
            case Key.O: shell.OpenPlaylist(); return true;
            case Key.Insert: shell.AddFiles(); return true;
            // Escape is the panic key: blank the screens and pause.
            case Key.Escape: shell.HideAll(); return true;
        }

        return false;
    }
}

/// <summary>Window-level actions the key map needs but the controller does not own.</summary>
public interface IShellCommands
{
    void ToggleScreenMode();
    void ToggleCaptions();
    void ToggleLegend();
    void OpenPlaylist();
    void AddFiles();
    void ToggleAutoAdvance();
    void ToggleFullscreen();
    void HideAll();

    /// <summary>True while the windows are blacked out.</summary>
    bool IsHidden { get; }

    /// <summary>Receives every key while hidden, to match the unlock sequence.</summary>
    void HiddenKey(Key key);
}
