using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MultiPlayer.Model;

namespace MultiPlayer.Views;

/// <summary>
/// One favorite slot. Shows the frame the favorite was stored at, so the bar says what
/// is behind each digit rather than merely that something is.
/// <para>
/// These are ordinary WPF images, not hosted video windows, so the digit badge can sit
/// over the picture — unlike the numbered tiles, where an overlay would be painted over.
/// </para>
/// </summary>
public sealed class FavoriteCell : Border
{
    private readonly Image _thumb;
    private readonly TextBlock _digit;
    private readonly Border _badge;
    private readonly Brush _idleEdge;
    private readonly Brush _emptyEdge;
    private readonly Brush _hotEdge;

    public FavoriteCell(int slot, ResourceDictionary res)
    {
        Slot = slot;
        _idleEdge = (Brush)res["Edge"];
        _emptyEdge = (Brush)res["Hairline"];
        _hotEdge = (Brush)res["AccentDeep"];

        Background = (Brush)res["BgSunken"];
        BorderBrush = _emptyEdge;
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(4);
        Margin = new Thickness(0, 0, 6, 0);
        SnapsToDevicePixels = true;
        Cursor = Cursors.Hand;
        ClipToBounds = true;

        _thumb = new Image
        {
            Stretch = Stretch.UniformToFill,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        RenderOptions.SetBitmapScalingMode(_thumb, BitmapScalingMode.HighQuality);

        _digit = new TextBlock
        {
            Text = slot.ToString(),
            FontFamily = (FontFamily)res["UiFont"],
            FontSize = 11,
            Foreground = (Brush)res["FgDim"],
        };
        _badge = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xB8, 0x16, 0x18, 0x26)),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(4, 1, 4, 1),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(3),
            Child = _digit,
        };

        var grid = new Grid();
        grid.Children.Add(_thumb);
        grid.Children.Add(_badge);
        Child = grid;

        MouseEnter += (_, _) => BorderBrush = _hotEdge;
        MouseLeave += (_, _) => BorderBrush = Occupied ? _idleEdge : _emptyEdge;
        MouseLeftButtonUp += (_, _) => Activated?.Invoke(Slot);
    }

    public int Slot { get; }
    public bool Occupied { get; private set; }

    public event Action<int>? Activated;

    public void Update(Favorite? favorite, string thumbnailPath, ResourceDictionary res)
    {
        Occupied = favorite is not null;
        BorderBrush = Occupied ? _idleEdge : _emptyEdge;
        _digit.Foreground = (Brush)res[Occupied ? "AccentPale" : "FgFaint"];
        _badge.Background = new SolidColorBrush(Color.FromArgb(Occupied ? (byte)0xB8 : (byte)0x00, 0x16, 0x18, 0x26));

        if (favorite is null)
        {
            _thumb.Source = null;
            ToolTip = $"Favorite {Slot} — empty. Press ` then {Slot} to store the primary here.";
            return;
        }

        _thumb.Source = LoadThumbnail(thumbnailPath);
        ToolTip = $"{favorite.Name} @ {TileCell.Format(favorite.PositionMs)}\nPress {Slot} to bring it up as primary.";
    }

    /// <summary>
    /// Reads the PNG through a stream and caches on load, so the file is not left locked
    /// and re-storing a slot is not served the previous image from WPF's URI cache.
    /// </summary>
    private static BitmapImage? LoadThumbnail(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var stream = File.OpenRead(path);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            // A half-written snapshot just means no picture yet.
            return null;
        }
    }
}
