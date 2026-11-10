using System.IO;
using System.Windows;
using PalaceDB.Core.Metadata;
using Microsoft.Web.WebView2.Core;
using MetaWindowState = PalaceDB.Core.Metadata.WindowState;

namespace PalaceDB.App;

/// <summary>
/// Thin shell around the WebView2 control. All user interface lives in the HTML
/// application; this window only provides the native host, the file dialogs and the
/// window chrome.
/// </summary>
public partial class MainWindow : Window
{
    private static readonly string WebRoot =
        Path.Combine(AppContext.BaseDirectory, "web");

    private EditorHost? _host;
    private string? _pendingFile;
    private bool _ready;

    /// <summary>
    /// True once the window geometry is settled: either a remembered placement has been
    /// applied before the window was shown, or the window is already on screen. Either
    /// way a file opened afterwards must not move it - the window used to jump every
    /// time a database was opened, because the placement arrived after the first paint.
    /// </summary>
    private bool _placementRestored;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Closing += OnClosing;

        AllowDrop = true;
        DragOver += OnDragOver;
        Drop += OnDrop;

        // Drag-and-drop hit-tests the element under the cursor. The WebView2 control
        // covers the whole window, so it has to accept drops itself or the OLE target
        // reports "none" and the cursor turns into the no-entry sign.
        Web.AllowDrop = true;
        Web.DragOver += OnDragOver;
        Web.Drop += OnDrop;
    }

    public EditorHost? Host => _host;

    /// <summary>Opens a file once the web application has finished loading.</summary>
    public void OpenFileWhenReady(string path)
    {
        if (_ready && _host is not null)
            _host.OpenDocument(path);
        else
            _pendingFile = path;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        // The window is on screen from here on, so its geometry is settled: a file
        // opened from now on must not move it.
        _placementRestored = true;

        try
        {
            var userData = Path.Combine(App.DataFolder, "WebView2");
            Directory.CreateDirectory(userData);

            var options = new CoreWebView2EnvironmentOptions();

            // Set PALACEDB_CDP to a port to attach DevTools for automated testing.
            var debugPort = Environment.GetEnvironmentVariable("PALACEDB_CDP");
            if (!string.IsNullOrWhiteSpace(debugPort))
                options.AdditionalBrowserArguments = $"--remote-debugging-port={debugPort}";

            var environment = await CoreWebView2Environment.CreateAsync(null, userData, options);
            await Web.EnsureCoreWebView2Async(environment);

            // WebView2 registers its own drop target by default, which swallows the drop
            // before the WPF window sees it and makes "drop a .cdb here" do nothing.
            // Turning it off lets the window's DragOver/Drop handlers run.
            Web.AllowExternalDrop = false;

            var core = Web.CoreWebView2;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;
            core.Settings.IsZoomControlEnabled = false;
            core.Settings.AreDevToolsEnabled = true;

            // A virtual host avoids file:// restrictions on ES modules.
            core.SetVirtualHostNameToFolderMapping(
                "palacedb.local", WebRoot, CoreWebView2HostResourceAccessKind.Allow);

            core.WebMessageReceived += OnWebMessage;
            core.NavigationCompleted += OnNavigationCompleted;

            _host = new EditorHost(this, core);
            core.Navigate("https://palacedb.local/index.html");
        }
        catch (Exception ex)
        {
            App.Log("WebView2 initialisation failed: " + ex);
            MessageBox.Show(
                "The WebView2 runtime could not be initialised.\n\n" +
                ex.Message +
                "\n\nInstall the Microsoft Edge WebView2 Runtime and start the application again.",
                "PalaceDB", MessageBoxButton.OK, MessageBoxImage.Error);
            Close();
        }
    }

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (!e.IsSuccess)
        {
            MessageBox.Show($"The editor interface could not be loaded ({e.WebErrorStatus}).",
                "PalaceDB", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        _ready = true;
        if (_pendingFile is { } file)
        {
            _pendingFile = null;
            _host?.OpenDocument(file);
        }
    }

    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        _host?.HandleMessage(e.WebMessageAsJson);
    }

    /// <summary>Updates the native title bar from the document state.</summary>
    public void UpdateTitle(string documentTitle, bool dirty)
    {
        Title = string.IsNullOrEmpty(documentTitle)
            ? "PalaceDB"
            : $"{documentTitle}{(dirty ? " *" : string.Empty)} - PalaceDB";
    }

    // ---------------------------------------------------------------- window geometry (F17)

    /// <summary>
    /// Places the window before it is shown, from the geometry remembered for a file that
    /// is about to be opened. This is the only moment a remembered position is used: a
    /// window that is already on screen stays exactly where the user put it.
    /// </summary>
    public void PlaceBeforeShow(MetaWindowState? state)
    {
        if (state is null || !state.HasPosition)
            return;

        var rect = WindowPlacement.Fit(state, Desktop());
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = rect.Left;
        Top = rect.Top;
        Width = rect.Width;
        Height = rect.Height;

        if (state.Maximized)
            WindowState = System.Windows.WindowState.Maximized;

        _placementRestored = true;
    }

    /// <summary>
    /// Restores the geometry remembered for a file. Only honoured before the window is
    /// shown (see <see cref="PlaceBeforeShow"/>); afterwards the window keeps its place,
    /// so opening a database never makes it jump.
    /// </summary>
    public void ApplyPlacement(MetaWindowState? state)
    {
        if (_placementRestored)
            return;
        _placementRestored = true;

        if (state is null || !state.HasPosition)
            return;

        var rect = WindowPlacement.Fit(state, Desktop());
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = rect.Left;
        Top = rect.Top;
        Width = rect.Width;
        Height = rect.Height;

        if (state.Maximized)
            WindowState = System.Windows.WindowState.Maximized;
    }

    /// <summary>
    /// Reads the current geometry. A maximised window reports its restore bounds, so
    /// un-maximising later brings back the size the user actually chose.
    /// </summary>
    public bool TryCapturePlacement(out MetaWindowState state)
    {
        state = new MetaWindowState();

        var maximized = WindowState == System.Windows.WindowState.Maximized;
        var bounds = maximized ? RestoreBounds : new Rect(Left, Top, ActualWidth, ActualHeight);

        if (bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0 ||
            double.IsNaN(bounds.Width) || double.IsNaN(bounds.Height))
        {
            return false;
        }

        state = WindowPlacement.Capture(
            new PlacementRect(bounds.X, bounds.Y, bounds.Width, bounds.Height),
            maximized);
        return true;
    }

    /// <summary>
    /// Placement for a window opened from this one when the file has no geometry of its
    /// own: shifted a little, so the new database does not hide exactly behind the one
    /// that is already on screen. Returns null when this window has no position yet, which
    /// lets the new window use its default placement.
    /// </summary>
    public MetaWindowState? CascadePlacement(double step = 32)
    {
        if (!TryCapturePlacement(out var current) || !current.HasPosition)
            return null;

        var rect = WindowPlacement.Fit(current, Desktop());
        var shifted = new PlacementRect(rect.Left + step, rect.Top + step, rect.Width, rect.Height);

        // Fit() inside PlaceBeforeShow pulls the shifted rectangle back onto the desktop.
        return WindowPlacement.Capture(shifted, current.Maximized);
    }

    /// <summary>The desktop covered by all monitors, in device-independent pixels.</summary>
    private static PlacementRect Desktop() => new(
        SystemParameters.VirtualScreenLeft,
        SystemParameters.VirtualScreenTop,
        SystemParameters.VirtualScreenWidth,
        SystemParameters.VirtualScreenHeight);

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = TryGetDroppedFile(e) is null ? DragDropEffects.None : DragDropEffects.Copy;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        OpenDroppedFile(TryGetDroppedFile(e));
        e.Handled = true;
    }

    /// <summary>
    /// Opens a dropped file. Shared with the automation hook below so the drop path can be
    /// exercised without a real mouse drag.
    /// </summary>
    private bool OpenDroppedFile(string? path)
    {
        if (path is null || _host is null)
            return false;

        _host.OpenDocument(path);
        return true;
    }

    /// <summary>
    /// Runs the drop handler for a set of dropped paths, for the automated tests. A real
    /// drag cannot be produced from the DevTools protocol, and the selection rule plus the
    /// open path are both worth covering.
    /// </summary>
    internal bool SimulateDrop(params string[] files) => OpenDroppedFile(SelectDatabaseFile(files));

    private static string? TryGetDroppedFile(DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
            return null;

        return e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files
            ? SelectDatabaseFile(files)
            : null;
    }

    /// <summary>
    /// Picks the database out of a dropped selection. Dropping a mixed set should still
    /// find the `.cdb` in it, and a drop of unrelated files opens nothing.
    /// </summary>
    private static string? SelectDatabaseFile(IEnumerable<string> files) =>
        files.FirstOrDefault(
            file => file.EndsWith(".cdb", StringComparison.OrdinalIgnoreCase));

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_host is null)
            return;

        if (!_host.ConfirmClose())
        {
            e.Cancel = true;
            return;
        }

        // Remember the geometry only when the window really goes away.
        _host.SaveWindowPlacement();

        // Give the file's write slot back, so another window showing the same database
        // can stop being read-only. The closing event can fire again for a window that
        // was cancelled earlier, and Detach is safe to call twice.
        _host.Detach();
        _host = null;
    }
}
