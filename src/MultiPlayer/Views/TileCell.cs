using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MultiPlayer.Playback;

namespace MultiPlayer.Views;

/// <summary>
/// One numbered slot, in two shapes. With <c>withVideo</c> it carries a live surface and
/// a caption strip; without, it is the compact read-only map of the wall that the control
/// screen shows in dual-screen mode.
/// <para>
/// The caption sits below the picture rather than over it: a hosted video window always
/// paints on top of WPF, so anything overlaid would simply be invisible.
/// </para>
/// </summary>
public sealed class TileCell : Border
{
    private readonly TextBlock _number;
    private readonly TextBlock _title;
    private readonly TextBlock _time;
    private readonly Border _progressFill;
    private readonly Border _progressTrack;
    private readonly Grid _caption;
    private readonly Brush _idleEdge;
    private readonly Brush _hotEdge;
    private readonly Brush _alertEdge;
    private readonly Brush _alertText;
    private readonly Brush _captionText;
    private readonly Brush _timeText;

    public TileCell(int key, bool withVideo, double numberSize, ResourceDictionary res)
    {
        Key = key;
        _idleEdge = (Brush)res["Edge"];
        _hotEdge = (Brush)res["AccentDeep"];
        _alertEdge = (Brush)res["AlertEdge"];
        _alertText = (Brush)res["AlertText"];
        _captionText = (Brush)res["FgMuted"];
        _timeText = (Brush)res["FgDim"];

        Background = (Brush)res["BgSunken"];
        BorderBrush = _idleEdge;
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(6);
        SnapsToDevicePixels = true;
        Cursor = Cursors.Hand;

        _number = new TextBlock
        {
            Text = key.ToString(),
            FontFamily = (FontFamily)res["UiFont"],
            FontSize = numberSize,
            Foreground = (Brush)res["FgFaint"],
            VerticalAlignment = VerticalAlignment.Center,
        };
        _title = new TextBlock
        {
            FontFamily = (FontFamily)res["UiFont"],
            FontSize = 11,
            Foreground = (Brush)res["FgMuted"],
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _time = new TextBlock
        {
            FontFamily = (FontFamily)res["UiFont"],
            FontSize = 11,
            Foreground = (Brush)res["FgDim"],
            VerticalAlignment = VerticalAlignment.Center,
        };

        _progressTrack = new Border { Height = 2, Background = (Brush)res["TrackBrush"] };
        _progressFill = new Border
        {
            Height = 2,
            Background = (Brush)res["AccentDim"],
            HorizontalAlignment = HorizontalAlignment.Left,
            Width = 0,
        };
        var track = new Grid();
        track.Children.Add(_progressTrack);
        track.Children.Add(_progressFill);

        _caption = withVideo ? BuildStrip() : BuildMap();

        var root = new Grid();
        if (withVideo)
        {
            // picture / progress / caption
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Host = new SurfaceHost();
            Grid.SetRow(Host, 0);
            Grid.SetRow(track, 1);
            Grid.SetRow(_caption, 2);
            root.Children.Add(Host);
        }
        else
        {
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(track, 0);
            Grid.SetRow(_caption, 1);
        }
        root.Children.Add(track);
        root.Children.Add(_caption);
        Child = root;

        MouseEnter += (_, _) => BorderBrush = _hotEdge;
        MouseLeave += (_, _) => BorderBrush = _missing ? _alertEdge : _idleEdge;
        MouseLeftButtonUp += (_, _) => Activated?.Invoke(Key);
    }

    /// <summary>Caption under a live tile: number, filename, elapsed, all on one line.</summary>
    private Grid BuildStrip()
    {
        var grid = new Grid { Margin = new Thickness(8, 4, 8, 5) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _title.Margin = new Thickness(8, 0, 8, 0);
        Grid.SetColumn(_number, 0);
        Grid.SetColumn(_title, 1);
        Grid.SetColumn(_time, 2);
        grid.Children.Add(_number);
        grid.Children.Add(_title);
        grid.Children.Add(_time);
        return grid;
    }

    /// <summary>
    /// Map cell: the number stays large and on the left where the 10-key puts it, with the
    /// filename and elapsed stacked beside it so a narrow column still reads.
    /// </summary>
    private Grid BuildMap()
    {
        var grid = new Grid { Margin = new Thickness(8, 6, 8, 6) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var stack = new StackPanel { Margin = new Thickness(9, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        _title.VerticalAlignment = VerticalAlignment.Top;
        _time.VerticalAlignment = VerticalAlignment.Top;
        _time.Margin = new Thickness(0, 2, 0, 0);
        stack.Children.Add(_title);
        stack.Children.Add(_time);

        Grid.SetColumn(_number, 0);
        Grid.SetColumn(stack, 1);
        grid.Children.Add(_number);
        grid.Children.Add(stack);
        return grid;
    }

    public int Key { get; }
    public SurfaceHost? Host { get; }

    private bool _missing;

    public event Action<int>? Activated;

    public void Update(PlayerSurface? surface, bool showTitles)
    {
        var entry = surface?.Entry;
        var missing = entry?.Missing == true;
        _missing = missing;

        _title.Text = entry is null ? "empty" : (showTitles || missing ? entry.Name : "");
        _time.Text = entry is null ? "" : missing ? "unreachable" : Format(surface!.Time);
        _number.Opacity = entry is null ? 0.35 : 1.0;

        // Say it in three places at once — colour alone reads as a style choice, and on a
        // wall of nine black rectangles one red filename is easy to miss.
        _title.Foreground = missing ? _alertText : _captionText;
        _time.Foreground = missing ? _alertText : _timeText;
        BorderBrush = missing ? _alertEdge : _idleEdge;

        var width = _progressTrack.ActualWidth;
        _progressFill.Width = width > 0 ? Math.Max(0, width * (surface?.Fraction ?? 0)) : 0;
    }

    public void SetNumberBrush(Brush brush) => _number.Foreground = brush;

    /// <summary>Hides the caption strip so the wall is nothing but picture.</summary>
    public void SetCaptionVisible(bool visible)
        => _caption.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;

    public static string Format(long milliseconds)
    {
        var t = TimeSpan.FromMilliseconds(Math.Max(0, milliseconds));
        return t.TotalHours >= 1
            ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}"
            : $"{t.Minutes:00}:{t.Seconds:00}";
    }
}
