using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using MultiPlayer.Model;
using MultiPlayer.Playback;

namespace MultiPlayer.Views;

public partial class MainWindow : Window, IShellCommands
{
    /// <summary>
    /// Dwell times T steps through, in seconds. 0 is off, and the cycle wraps back to it.
    /// </summary>
    private static readonly int[] AutoAdvanceSeconds = { 0, 60, 30, 10 };

    private readonly PlayerController _controller;
    private readonly DispatcherTimer _timer;
    private readonly DispatcherTimer _autoAdvance;
    private readonly Dictionary<int, TileCell> _tiles = new();
    private readonly ObservableCollection<PlaylistRow> _rows = new();
    private readonly List<FavoriteCell> _favorites = new();

    /// <summary>Typed on the blacked-out window to bring the app back.</summary>
    private static readonly Key[] UnlockSequence = { Key.Escape, Key.Enter, Key.Escape };

    private WallWindow? _wall;
    private List<MonitorInfo> _monitors = new();
    private bool _suspendLayout;
    private bool _showTitles = true;
    private bool _controlsVisible = true;
    private int _rowsRevision = -1;
    private int _reportedUnreachable = -1;

    private bool _closing;
    private bool _hidden;
    private int _unlockMatched;
    private int _autoIndex;
    private bool _fullscreen;
    private WindowState _savedState;
    private WindowStyle _savedStyle;
    private ResizeMode _savedResize;
    private Rect _savedBounds;

    public MainWindow(PlayerController controller)
    {
        _controller = controller;
        InitializeComponent();

        // Carry the build in the title so a screenshot identifies its own version.
        Title = BuildInfo.Title;

        PlaylistList.ItemsSource = _rows;
        ScreenPicker.DisplayMemberPath = nameof(MonitorInfo.Label);

        _controller.LayoutChanged += ApplyLayout;
        _controller.StateChanged += Refresh;
        // Thumbnails arrive on a LibVLC thread once the snapshot lands.
        _controller.FavoritesChanged += () => Dispatcher.BeginInvoke(RefreshFavorites);

        WireCommands();
        BuildTiles();
        BuildFavorites();
        UpdateModeControls();

        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(250),
        };
        _timer.Tick += (_, _) => _controller.Tick();
        _timer.Start();

        _autoAdvance = new DispatcherTimer();
        _autoAdvance.Tick += (_, _) => _controller.ShiftSet(1);

        KeyHook.Attach(this, (key, shift) => KeyRouter.Handle(_controller, key, shift, this));

        DragOver += OnDragOver;
        Drop += OnDrop;
        Loaded += (_, _) => { RefreshMonitors(); ApplyLayout(); Refresh(); };
        Closing += OnClosing;
    }

    // --- wiring ---------------------------------------------------------------

    private void WireCommands()
    {
        HideSidebarButton.Click += (_, _) => SetSidebar(false);
        ShowSidebarButton.Click += (_, _) => SetSidebar(true);

        AddButton.Click += (_, _) => AddFiles();
        OpenButton.Click += (_, _) => OpenPlaylist();
        SaveButton.Click += (_, _) => SavePlaylist();

        PrevSetButton.Click += (_, _) => _controller.ShiftSet(-1);
        NextSetButton.Click += (_, _) => _controller.ShiftSet(1);
        AutoButton.Click += (_, _) => ToggleAutoAdvance();

        ModeButton.Click += (_, _) => ToggleScreenMode();
        MuteButton.Click += (_, _) => _controller.ToggleMute();
        HideButton.Click += (_, _) => HideAll();
        FullscreenButton.Click += (_, _) => ToggleFullscreen();

        ScreenPicker.SelectionChanged += (_, _) =>
        {
            if (_wall is not null && ScreenPicker.SelectedItem is MonitorInfo m) _wall.PlaceOn(m);
        };

        PrimaryRestart.Click += (_, _) => _controller.RestartPrimary();
        PrimaryBack.Click += (_, _) => _controller.SeekPrimary(-Step());
        PrimaryToggle.Click += (_, _) => _controller.TogglePrimary();
        PrimaryForward.Click += (_, _) => _controller.SeekPrimary(Step());

        AllRestart.Click += (_, _) => _controller.RestartNumbered();
        AllBack.Click += (_, _) => _controller.SeekNumbered(-Step());
        AllToggle.Click += (_, _) => _controller.ToggleNumbered();
        AllForward.Click += (_, _) => _controller.SeekNumbered(Step());
    }

    private int Step() => (Keyboard.Modifiers & ModifierKeys.Shift) != 0
        ? _controller.ShiftSeekStep
        : _controller.SeekStep;

    private void SetSidebar(bool open)
    {
        Sidebar.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        SidebarColumn.Width = open ? new GridLength(236) : new GridLength(0);
        ShowSidebarButton.Visibility = open ? Visibility.Collapsed : Visibility.Visible;
    }

    // --- tiles ----------------------------------------------------------------

    /// <summary>
    /// Rebuilds the numbered tiles. They always live in TileArea; what changes with the
    /// mode is how many there are and which window TileArea currently sits in.
    /// </summary>
    private void BuildTiles()
    {
        foreach (var cell in _tiles.Values)
        {
            cell.Host?.Park();
            cell.Host?.Dispose();
        }
        _tiles.Clear();
        TileArea.Children.Clear();
        TileArea.RowDefinitions.Clear();
        TileArea.ColumnDefinitions.Clear();

        var keys = _controller.Keys;
        var multi = _controller.Mode == ScreenMode.Multi;
        var cols = Layouts.Columns(_controller.Mode);
        var rows = (int)Math.Ceiling(keys.Length / (double)cols);

        for (int r = 0; r < rows; r++)
            TileArea.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        for (int c = 0; c < cols; c++)
            TileArea.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        for (int i = 0; i < keys.Length; i++)
        {
            var cell = new TileCell(keys[i], withVideo: true, numberSize: multi ? 30 : 22,
                                    Application.Current.Resources)
            {
                Margin = new Thickness(0, 0, i % cols == cols - 1 ? 0 : 11, 11),
            };
            if (multi) cell.SetNumberBrush((Brush)Application.Current.Resources["AccentBright"]);
            cell.Activated += k => _controller.Select(k);
            cell.SetCaptionVisible(_controlsVisible);
            Grid.SetRow(cell, i / cols);
            Grid.SetColumn(cell, i % cols);
            TileArea.Children.Add(cell);
            _tiles[keys[i]] = cell;
        }
    }

    /// <summary>
    /// Puts every surface where the controller says it belongs. Called after a swap, a set
    /// change, a mode change or a move between windows; each surface moves with one
    /// SetParent, never a reload.
    /// </summary>
    private void ApplyLayout()
    {
        if (_suspendLayout) return;

        if (_hidden)
        {
            // Nothing may render while hidden — a hosted video window paints over WPF, so
            // the curtain alone would not blank the window or its taskbar preview.
            //
            // Park through the hosts, not the surfaces: a host that still believes it owns
            // a surface treats the next SetSurface as a no-op and never fetches it back
            // out of the parking window, so revealing would show an empty frame.
            PrimaryHost.Visibility = Visibility.Collapsed;
            PrimaryHost.Park();
            foreach (var cell in _tiles.Values)
            {
                if (cell.Host is not { } host) continue;
                host.Visibility = Visibility.Collapsed;
                host.Park();
            }
            foreach (var surface in _controller.Pool) surface.Park(_controller.Parking);
            return;
        }

        var used = new HashSet<PlayerSurface>();

        var primary = _controller.Primary;
        // A missing file still counts as "has media" to LibVLC, but there is no picture to
        // show, so the placeholder has to take over or the reason is painted over by black.
        var hasPicture = primary.HasMedia && primary.Entry?.Missing != true;
        PrimaryHost.Visibility = hasPicture ? Visibility.Visible : Visibility.Collapsed;
        PrimaryPlaceholder.Visibility = hasPicture ? Visibility.Collapsed : Visibility.Visible;
        var hasPrimary = hasPicture;
        if (hasPrimary)
        {
            PrimaryHost.SetSurface(primary);
            used.Add(primary);
        }
        else PrimaryHost.Park();

        foreach (var key in _controller.Keys)
        {
            if (!_tiles.TryGetValue(key, out var cell) || cell.Host is not { } host) continue;
            var surface = _controller.Tile(key);

            if (surface is not null && surface.HasMedia && surface.Entry?.Missing != true)
            {
                host.Visibility = Visibility.Visible;
                host.SetSurface(surface);
                used.Add(surface);
            }
            else
            {
                host.Visibility = Visibility.Collapsed;
                host.Park();
            }
        }

        // Anything not on screen goes back to the hidden parent so it can never be
        // destroyed by a window teardown.
        foreach (var s in _controller.Pool)
            if (!used.Contains(s)) s.Park(_controller.Parking);

        RebuildRowsIfNeeded();
        RefreshRowMarkers();
    }

    // --- refresh --------------------------------------------------------------

    private void Refresh()
    {
        var p = _controller.Primary;
        PrimaryTitleText.Text = p.Entry?.Name ?? "nothing loaded";
        PrimaryOnlyTitle.Text = p.Entry?.Name ?? "";
        PrimaryTimeText.Text = $"{TileCell.Format(p.Time)} / {TileCell.Format(p.Length)}";
        PrimaryProgress.Width = PrimaryTrack.ActualWidth * p.Fraction;
        PrimaryToggle.Content = _controller.PrimaryPlaying ? "❙❙" : "▶";
        AllToggle.Content = _controller.NumberedPlaying ? "❙❙" : "▶";

        SetLabel.Text = $"SET {_controller.SetIndex + 1} / {_controller.TotalSets}";
        PlaylistNameText.Text = _controller.PlaylistName;
        PlaylistMetaText.Text = _controller.Playlist.Count == 0
            ? "add files, or drop them here"
            : $"{_controller.Playlist.Count} videos · {_controller.TotalSets} sets of {_controller.PageSize}";
        MuteButton.Content = _controller.GlobalMute ? "unmute" : "mute";

        // The readout is a confirmation, not a status line: let it go after 5s — unless it
        // is reporting a failure, which must stay until something supersedes it.
        var fresh = DateTime.UtcNow - _controller.LastActionAt < TimeSpan.FromSeconds(5);
        LastActionText.Text = fresh || _controller.LastActionIsFailure ? _controller.LastAction : "";
        LastActionText.Foreground = (Brush)Application.Current.Resources[
            _controller.LastActionIsFailure ? "AlertText" : "AccentDeep"];

        RefreshUnreachable();

        foreach (var (key, cell) in _tiles) cell.Update(_controller.Tile(key), _showTitles);
        RefreshArmedState();
    }

    /// <summary>
    /// Says why the screen is black. Three places, because one unplugged share can take out
    /// a whole playlist and a wall of black rectangles otherwise reads as a broken build.
    /// </summary>
    private void RefreshUnreachable()
    {
        var missing = _controller.UnreachableCount;
        var total = _controller.CheckedCount;

        // The sweep answers a few seconds after the layout was applied. Re-apply once when
        // its verdict changes, or a surface stays attached painting black over the reason.
        if (missing != _reportedUnreachable)
        {
            _reportedUnreachable = missing;
            ApplyLayout();
        }

        if (missing == 0 || total == 0)
        {
            AlertBar.Visibility = Visibility.Collapsed;
            PrimaryReasonText.Visibility = Visibility.Collapsed;
            return;
        }

        var root = _controller.UnreachableRoot;
        var where = string.IsNullOrEmpty(root) ? "" : $" — nothing found under {root}";
        AlertText.Text = missing == total
            ? $"None of these {total} files can be reached{where}. Check the drive or share is connected."
            : $"{missing} of {total} files cannot be reached{where}.";
        AlertBar.Visibility = Visibility.Visible;

        // The placeholder is where the eye goes when the picture is black, so put the
        // reason there too rather than only in a bar at the top.
        var primary = _controller.Primary.Entry;
        if (primary?.Missing == true)
        {
            PrimaryReasonText.Text = string.IsNullOrEmpty(root)
                ? "this file cannot be reached"
                : $"cannot be reached — {root} is not responding";
            PrimaryReasonText.Visibility = Visibility.Visible;
        }
        else PrimaryReasonText.Visibility = Visibility.Collapsed;
    }

    private void UpdateModeControls()
    {
        var multi = _controller.Mode == ScreenMode.Multi;
        ModeButton.Content = multi ? "switch to single screen" : "switch to multi-screen";
        ModeButton.ToolTip = multi
            ? "Bring the numbered videos back onto this screen (D)"
            : "Move the numbered videos onto a second screen (D)";
        ScreenPicker.Visibility = multi ? Visibility.Visible : Visibility.Collapsed;

        // On the wall the primary column is gone entirely; its transport moves in beside
        // the numbered one so both stay reachable.
        PrimarySection.Visibility = multi ? Visibility.Collapsed : Visibility.Visible;
        Splitter.Visibility = multi ? Visibility.Collapsed : Visibility.Visible;
        TransportDivider.Visibility = multi ? Visibility.Visible : Visibility.Collapsed;
        PrimaryColumn.Width = multi ? new GridLength(0) : new GridLength(48, GridUnitType.Star);
        PrimaryColumn.MinWidth = multi ? 0 : 240;
        SplitColumn.Width = new GridLength(multi ? 0 : 18);
        TileColumn.Width = new GridLength(multi ? 1 : 52, GridUnitType.Star);
        TileColumn.MinWidth = multi ? 0 : 260;

        LegendText.Text = multi
            ? "keypad 1–9 swap  ·  row 0–9 favorites (` stores)  ·  a z x c primary  ·  k , . / numbered  ·  r shuffle  ·  t auto  ·  ins add  ·  del remove  ·  ↑ ↓ set  ·  f fullscreen  ·  esc hide  ·  d single screen"
            : "keypad 7 9 4 6 1 3 swap  ·  row 0–9 favorites (` stores)  ·  a z x c primary  ·  k , . / numbered  ·  r shuffle  ·  t auto  ·  ins add  ·  del remove  ·  ↑ ↓ set  ·  esc hide  ·  d multi-screen";
    }

    // --- playlist rows --------------------------------------------------------

    private void RebuildRowsIfNeeded()
    {
        // Keyed on the revision, not the count: a shuffle changes order without changing
        // how many there are.
        if (_rowsRevision == _controller.PlaylistRevision) return;
        _rowsRevision = _controller.PlaylistRevision;
        _rows.Clear();
        for (int i = 0; i < _controller.Playlist.Count; i++)
            _rows.Add(new PlaylistRow(i + 1, _controller.Playlist[i]));
    }

    private void RefreshRowMarkers()
    {
        var res = Application.Current.Resources;
        var onPrimary = (Brush)res["AccentWash"];
        var onWall = new SolidColorBrush(Color.FromArgb(0x0D, 0xE9, 0xE9, 0xED));
        onWall.Freeze();

        var slotFor = new Dictionary<VideoEntry, string>();
        if (_controller.Primary.Entry is { } pe) slotFor[pe] = "MAIN";
        foreach (var key in _controller.Keys)
            if (_controller.Tile(key)?.Entry is { } te && !slotFor.ContainsKey(te)) slotFor[te] = key.ToString();

        foreach (var row in _rows)
        {
            var slot = slotFor.TryGetValue(row.Entry, out var s) ? s : "";
            row.Slot = slot;
            row.Background = slot == "MAIN" ? onPrimary : slot.Length > 0 ? onWall : Brushes.Transparent;
            row.Foreground = slot == "MAIN"
                ? (Brush)res["FgBright"]
                : slot.Length > 0 ? (Brush)res["FgSoft"] : (Brush)res["FgDim"];
        }
    }

    // --- favorites ------------------------------------------------------------

    private void BuildFavorites()
    {
        FavoritesGrid.Children.Clear();
        _favorites.Clear();
        foreach (var slot in FavoriteStore.DisplayOrder)
        {
            var cell = new FavoriteCell(slot, Application.Current.Resources);
            cell.Activated += s => _controller.RecallFavorite(s);
            FavoritesGrid.Children.Add(cell);
            _favorites.Add(cell);
        }
        RefreshFavorites();
    }

    /// <summary>Reloads the stored frames. Only worth doing when a slot actually changed.</summary>
    private void RefreshFavorites()
    {
        var res = Application.Current.Resources;
        foreach (var cell in _favorites)
            cell.Update(_controller.Favorites[cell.Slot], _controller.Favorites.ThumbnailPath(cell.Slot), res);
        RefreshArmedState();
    }

    /// <summary>
    /// Cheap enough to run on every tick, which is what keeps a finished store or removal
    /// from leaving its prompt on screen.
    /// </summary>
    private void RefreshArmedState()
    {
        var res = Application.Current.Resources;
        var storing = _controller.FavoriteArmed;
        var removing = _controller.RemoveArmed;

        var caption = storing ? "PICK A SLOT" : removing ? "PICK TO REMOVE" : "FAVORITES";
        if (FavoritesLabel.Text == caption) return;

        FavoritesLabel.Text = caption;
        FavoritesLabel.Foreground = (Brush)res[storing || removing ? "AccentPale" : "FgMuted"];
        FavoritesBar.Background = storing || removing
            ? (Brush)res["AccentWashSoft"]
            : Brushes.Transparent;
    }

    // --- screen mode ----------------------------------------------------------

    private void RefreshMonitors()
    {
        _monitors = Native.GetMonitors();
        var previous = ScreenPicker.SelectedItem as MonitorInfo;
        ScreenPicker.ItemsSource = _monitors;
        ScreenPicker.SelectedItem = _monitors.FirstOrDefault(m => m.Device == previous?.Device) ?? DefaultWallMonitor();
    }

    private MonitorInfo? DefaultWallMonitor()
    {
        var here = Native.MonitorForWindow(new WindowInteropHelper(this).Handle);
        return _monitors.FirstOrDefault(m => m.Device != here?.Device) ?? _monitors.FirstOrDefault();
    }

    public void ToggleScreenMode()
    {
        if (_controller.Mode == ScreenMode.Single) EnterMulti();
        else ExitMulti();
    }

    private void EnterMulti()
    {
        RefreshMonitors();
        var target = ScreenPicker.SelectedItem as MonitorInfo ?? DefaultWallMonitor();
        if (target is null)
        {
            _controller.Note("no display found");
            return;
        }

        _suspendLayout = true;
        _wall = new WallWindow(_controller, this);
        _wall.PlaceOn(target);
        _wall.Closed += (_, _) =>
        {
            _wall = null;
            // On shutdown the wall is closed by OnClosing. Running ExitMulti then would
            // move the control surface back, reload media and restart playback while the app is
            // trying to exit, and the process never finishes coming down.
            if (_closing) return;
            if (_controller.Mode == ScreenMode.Multi) ExitMulti();
        };
        _wall.Show();

        _controller.SetMode(ScreenMode.Multi);
        MoveControlsToWall();
        _suspendLayout = false;

        BuildTiles();
        UpdateModeControls();
        ApplyLayout();
        Activate();

        if (_monitors.Count < 2) _controller.Note("only one display — the wall is covering it");
    }

    private void ExitMulti()
    {
        _suspendLayout = true;
        if (_fullscreen) ToggleFullscreen();

        MoveControlsHome();
        var wall = _wall;
        _wall = null;
        wall?.Close();

        _controller.SetMode(ScreenMode.Single);
        _suspendLayout = false;

        BuildTiles();
        UpdateModeControls();
        ApplyLayout();
        Activate();
    }

    /// <summary>
    /// Hands the whole control surface to the wall window. The elements are moved, not
    /// rebuilt, so only one of them ever exists and two copies cannot drift apart.
    /// Hosted video windows are parked as their containers are torn down, and
    /// re-attached by the ApplyLayout that follows.
    /// </summary>
    private void MoveControlsToWall()
    {
        if (_wall is null) return;

        PrimarySlot.Content = null;
        PrimaryTransportSlot.Content = null;
        Root.Children.Remove(ControlSurface);

        _wall.Host(ControlSurface);
        PrimaryOnlySlot.Content = PrimaryPane;
        ExtraTransportSlot.Content = PrimaryTransport;
        PrimaryOnly.Visibility = Visibility.Visible;
    }

    private void MoveControlsHome()
    {
        PrimaryOnly.Visibility = Visibility.Collapsed;
        PrimaryOnlySlot.Content = null;
        ExtraTransportSlot.Content = null;

        _wall?.Host(null);
        if (!Root.Children.Contains(ControlSurface)) Root.Children.Insert(0, ControlSurface);

        PrimarySlot.Content = PrimaryPane;
        PrimaryTransportSlot.Content = PrimaryTransport;
    }

    // --- fullscreen -----------------------------------------------------------

    /// <summary>
    /// Fills this display with the primary picture. Only meaningful in multi-screen mode,
    /// where this window holds nothing else.
    /// </summary>
    public void ToggleFullscreen()
    {
        if (!_fullscreen && _controller.Mode != ScreenMode.Multi)
        {
            _controller.Note("fullscreen is for multi-screen mode");
            return;
        }

        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;

        if (!_fullscreen)
        {
            _savedState = WindowState;
            _savedStyle = WindowStyle;
            _savedResize = ResizeMode;
            _savedBounds = new Rect(Left, Top, Width, Height);

            var monitor = Native.MonitorForWindow(hwnd);
            WindowState = WindowState.Normal;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            PrimaryOnlyBar.Visibility = Visibility.Collapsed;

            if (monitor is not null)
                Native.SetWindowPos(hwnd, IntPtr.Zero, monitor.X, monitor.Y, monitor.Width, monitor.Height,
                    Native.SWP_NOZORDER | Native.SWP_NOACTIVATE | Native.SWP_FRAMECHANGED);

            _fullscreen = true;
            FullscreenButton.Content = "exit fullscreen";
            _controller.Note("fullscreen — f to exit");
        }
        else
        {
            WindowStyle = _savedStyle;
            ResizeMode = _savedResize;
            PrimaryOnlyBar.Visibility = Visibility.Visible;
            Left = _savedBounds.X;
            Top = _savedBounds.Y;
            Width = _savedBounds.Width;
            Height = _savedBounds.Height;
            WindowState = _savedState;

            _fullscreen = false;
            FullscreenButton.Content = "fullscreen";
        }
    }

    // --- auto advance ---------------------------------------------------------

    /// <summary>Steps to the next dwell time: off, 60s, 30s, 10s, and back to off.</summary>
    public void ToggleAutoAdvance()
    {
        _autoIndex = (_autoIndex + 1) % AutoAdvanceSeconds.Length;
        ApplyAutoAdvance();
        _controller.Note(AutoAdvanceSeconds[_autoIndex] > 0
            ? $"auto advance every {AutoAdvanceSeconds[_autoIndex]}s"
            : "auto advance off");
    }

    private void ApplyAutoAdvance()
    {
        var seconds = AutoAdvanceSeconds[_autoIndex];
        _autoAdvance.Stop();
        if (seconds > 0)
        {
            _autoAdvance.Interval = TimeSpan.FromSeconds(seconds);
            _autoAdvance.Start();
        }

        AutoButton.Content = seconds > 0 ? $"auto {seconds}s" : "auto off";
        AutoButton.Foreground = (Brush)Application.Current.Resources[
            seconds > 0 ? "AccentPale" : "FgMuted"];
    }

    // --- hiding ---------------------------------------------------------------

    public bool IsHidden => _hidden;

    /// <summary>
    /// Pauses everything, blacks the app's own windows out, and minimises. The desktop is
    /// left alone — whatever else is running simply shows through. Because the windows are
    /// genuinely black rather than merely minimised, their taskbar previews are black too.
    /// </summary>
    public void HideAll()
    {
        if (_hidden) return;

        _hidden = true;
        _unlockMatched = 0;
        _controller.SuspendAll();
        _autoAdvance.Stop();

        Curtain.Visibility = Visibility.Visible;
        _wall?.SetCurtain(true);
        ApplyLayout();          // parks every surface, so no picture survives anywhere

        // The wall has no taskbar entry of its own, so it goes away entirely rather than
        // sitting minimised where it cannot be got back.
        _wall?.Hide();
        WindowState = WindowState.Minimized;
    }

    /// <summary>
    /// While hidden, every key goes here and nowhere else. Coming back is deliberately not
    /// one keypress: Esc, Enter, Esc. Anything else restarts the sequence, so a hand
    /// brushing the keyboard cannot put the video back on screen.
    /// </summary>
    public void HiddenKey(Key key)
    {
        if (key == UnlockSequence[_unlockMatched])
        {
            _unlockMatched++;
            if (_unlockMatched == UnlockSequence.Length) Reveal();
            return;
        }
        _unlockMatched = key == UnlockSequence[0] ? 1 : 0;
    }

    private void Reveal()
    {
        if (!_hidden) return;

        _hidden = false;
        _unlockMatched = 0;

        Curtain.Visibility = Visibility.Collapsed;
        _wall?.SetCurtain(false);
        if (_wall is not null)
        {
            _wall.Show();
            _wall.WindowState = WindowState.Normal;
        }

        ApplyLayout();
        ApplyAutoAdvance();      // resume cycling at whatever dwell was chosen
        _controller.ResumeAll();
        Activate();
    }

    /// <summary>Strips the control surface back to picture only.</summary>
    public void ToggleCaptions()
    {
        _controlsVisible = !_controlsVisible;
        _showTitles = _controlsVisible;

        var visibility = _controlsVisible ? Visibility.Visible : Visibility.Collapsed;
        HeaderBar.Visibility = visibility;
        LegendBar.Visibility = visibility;
        FavoritesBar.Visibility = visibility;
        NumberedTransportRow.Visibility = visibility;
        Sidebar.Visibility = visibility;
        SidebarColumn.Width = _controlsVisible ? new GridLength(236) : new GridLength(0);
        Stage.Margin = new Thickness(_controlsVisible ? 17 : 6);
        foreach (var cell in _tiles.Values) cell.SetCaptionVisible(_controlsVisible);

        _controller.Note(_controlsVisible ? "controls on" : "picture only");
    }

    public void ToggleLegend()
    {
        LegendBar.Visibility = LegendBar.Visibility == Visibility.Visible
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    // --- files ----------------------------------------------------------------

    public void AddFiles()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Add videos to the playlist",
            Multiselect = true,
            Filter = "Video and playlists (*.mp4;*.mkv;*.mov;*.avi;*.wmv;*.m4v;*.webm;*.ts;*.m2ts;*.mvp)|"
                   + "*.mp4;*.mkv;*.mov;*.avi;*.wmv;*.m4v;*.webm;*.ts;*.m2ts;*.mvp|All files (*.*)|*.*",
        };
        if (dialog.ShowDialog(this) != true) return;
        _controller.AddFiles(dialog.FileNames);
    }

    public void OpenPlaylist()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Open a playlist, or pick videos",
            Multiselect = true,
            Filter = "Playlists and video (*.mvp;*.mp4;*.mkv;*.mov;*.avi;*.wmv;*.m4v;*.webm;*.ts;*.m2ts)|"
                   + "*.mvp;*.mp4;*.mkv;*.mov;*.avi;*.wmv;*.m4v;*.webm;*.ts;*.m2ts|"
                   + "Playlist (*.mvp)|*.mvp|All files (*.*)|*.*",
        };
        if (dialog.ShowDialog(this) != true) return;
        Accept(dialog.FileNames);
    }

    private void SavePlaylist()
    {
        if (_controller.Playlist.Count == 0)
        {
            _controller.Note("nothing to save");
            return;
        }
        var dialog = new SaveFileDialog
        {
            Title = "Save playlist",
            Filter = "Playlist (*.mvp)|*.mvp",
            DefaultExt = MvpPlaylist.Extension,
            FileName = "playlist" + MvpPlaylist.Extension,
        };
        if (dialog.ShowDialog(this) == true) _controller.SavePlaylistFile(dialog.FileName);
    }

    /// <summary>
    /// Takes whatever was dropped or picked: a playlist, a set of files, a folder, or a
    /// single video (in which case its whole folder is loaded, starting at that file).
    /// </summary>
    private void Accept(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return;

        if (paths.Count == 1 && Directory.Exists(paths[0]))
        {
            _controller.LoadFolder(paths[0], recurse: false);
            return;
        }

        if (paths.Count == 1 && MvpPlaylist.HasExtension(paths[0]))
        {
            try { _controller.LoadPlaylistFile(paths[0]); }
            catch (Exception ex) { _controller.Note("could not read playlist: " + ex.Message); }
            return;
        }

        if (paths.Count == 1 && MediaTypes.IsVideo(paths[0]))
        {
            var folder = Path.GetDirectoryName(paths[0]);
            if (folder is not null)
            {
                var all = MediaTypes.ScanFolder(folder, recurse: false).ToList();
                var start = all.FindIndex(p => string.Equals(p, paths[0], StringComparison.OrdinalIgnoreCase));
                if (start > 0)
                {
                    // Start the playlist at the file that was opened.
                    all = all.Skip(start).Concat(all.Take(start)).ToList();
                }
                _controller.LoadPlaylist(all, new DirectoryInfo(folder).Name + "\\", Path.GetFullPath(folder));
                return;
            }
        }

        var expanded = new List<string>();
        foreach (var p in paths)
        {
            if (Directory.Exists(p)) expanded.AddRange(MediaTypes.ScanFolder(p, recurse: false));
            else expanded.Add(p);
        }
        _controller.LoadPlaylist(expanded, $"{expanded.Count} selected");
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files || files.Length == 0) return;

        // Dropping a playlist opens it; dropping media adds to what is already loaded,
        // since replacing a running wall is rarely what a drag means.
        var isPlaylist = files.Length == 1 && MvpPlaylist.HasExtension(files[0]);

        if (isPlaylist || _controller.Playlist.Count == 0) Accept(files);
        else _controller.AddFiles(files);
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        _closing = true;
        _timer.Stop();
        _autoAdvance.Stop();

        var wall = _wall;
        _wall = null;
        wall?.Close();

        foreach (var cell in _tiles.Values) cell.Host?.Park();
        PrimaryHost.Park();
    }
}

/// <summary>One line in the playlist sidebar.</summary>
public sealed class PlaylistRow : INotifyPropertyChanged
{
    private string _slot = "";
    private Brush _background = Brushes.Transparent;
    private Brush _foreground = Brushes.Gray;

    public PlaylistRow(int number, VideoEntry entry)
    {
        Number = number;
        Entry = entry;
    }

    public int Number { get; }
    public VideoEntry Entry { get; }
    public string Name => Entry.Name;

    public string Slot { get => _slot; set => Set(ref _slot, value); }
    public Brush Background { get => _background; set => Set(ref _background, value); }
    public Brush Foreground { get => _foreground; set => Set(ref _foreground, value); }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
