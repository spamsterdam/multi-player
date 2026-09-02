namespace MultiPlayer.Model;

public enum ScreenMode
{
    /// <summary>Primary and the numbered grid share one window.</summary>
    Single,
    /// <summary>Primary fills one display; the numbered wall fills another.</summary>
    Multi,
}

/// <summary>
/// Which number keys are live, and in what visual order they are laid out.
/// Both layouts are read straight off a 10-key: row order is 7-8-9 / 4-5-6 / 1-2-3,
/// so the key you press is always in the position your fingers expect.
/// </summary>
public static class Layouts
{
    /// <summary>Single-screen: 2 columns x 3 rows, the outer numpad columns.</summary>
    public static readonly int[] Single = { 7, 9, 4, 6, 1, 3 };

    /// <summary>Multi-screen: the full 3x3 numpad face.</summary>
    public static readonly int[] Multi = { 7, 8, 9, 4, 5, 6, 1, 2, 3 };

    public static int[] For(ScreenMode mode) => mode == ScreenMode.Multi ? Multi : Single;

    public static int Columns(ScreenMode mode) => mode == ScreenMode.Multi ? 3 : 2;
}
