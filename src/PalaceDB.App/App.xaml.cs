using System.IO;
using System.Windows;
using System.Windows.Threading;
using PalaceDB.Core.Metadata;
using PalaceDB.Core.Settings;

namespace PalaceDB.App;

public partial class App : Application
{
    /// <summary>
    /// Per-user folder holding the log, the settings and the WebView2 profile.
    /// <c>PALACEDB_DATA_DIR</c> redirects it, which keeps the automated tests out of the
    /// real user profile.
    /// </summary>
    public static string DataFolder { get; } = ResolveDataFolder();

    /// <summary>Log file used for start-up failures that have no window to report them in.</summary>
    public static string LogPath { get; } = Path.Combine(DataFolder, "palacedb.log");

    private static string ResolveDataFolder()
    {
        var custom = Environment.GetEnvironmentVariable("PALACEDB_DATA_DIR");
        if (!string.IsNullOrWhiteSpace(custom))
        {
            try
            {
                return Path.GetFullPath(custom);
            }
            catch
            {
                // Fall back to the default location below.
            }
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PalaceDB");
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log("Unhandled: " + args.ExceptionObject);

        // A database path on the command line opens directly.
        var files = Environment.GetCommandLineArgs().Skip(1)
            .Where(a => !a.StartsWith('-'))
            .ToArray();

        var window = new MainWindow();
        MainWindow = window;

        // Without a path on the command line the session picks up where the last one left
        // off: the most recently used database that still exists opens straight away.
        var target = files.Length > 0 ? files[0] : MostRecentFile();
        if (target is not null)
        {
            // Placed before the first paint, because a remembered position applied after
            // Show() is what made the window jump every time a file was opened.
            window.PlaceBeforeShow(MetaStore.ReadWindowState(target));
            window.OpenFileWhenReady(target);
        }

        window.Show();
    }

    /// <summary>
    /// The database a start without arguments reopens. A missing or unreadable settings
    /// file means "nothing to reopen", never a failed start.
    /// </summary>
    private static string? MostRecentFile()
    {
        try
        {
            return AppSettings.Load(AppSettings.PathFor(DataFolder)).MostRecentExisting();
        }
        catch
        {
            return null;
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log("Dispatcher: " + e.Exception);
        MessageBox.Show(e.Exception.Message, "PalaceDB", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }

    /// <summary>Appends a line to the log file. Never throws.</summary>
    public static void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}{Environment.NewLine}");
        }
        catch
        {
            // Logging must never break the application.
        }
    }
}
