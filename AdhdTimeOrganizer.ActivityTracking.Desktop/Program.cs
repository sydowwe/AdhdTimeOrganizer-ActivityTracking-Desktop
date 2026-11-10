using AdhdTimeOrganizer.ActivityTracking.Desktop;
using AdhdTimeOrganizer.ActivityTracking.Desktop.Helpers;
using AdhdTimeOrganizer.ActivityTracking.Desktop.Models;
using AdhdTimeOrganizer.ActivityTracking.Desktop.Services;
using Serilog;
using Velopack;

// First, before anything else: the installer and the updater start the exe with hook arguments, and Run() handles them
// and exits. The uninstall hook removes the Run entry if it starts this exe; the data folder (config, outbox, state) is
// not in the install folder and is left alone, so a reinstall picks up where it stopped.
// Run() does NOT apply a downloaded update: it would do so ahead of the single-instance check, and the updater stops every
// process in the install folder — a second launch would end the running tracker without its exit. The update is applied
// below, by the first instance only (UpdateService.ApplyPendingAtStart).
VelopackApp.Build()
    .SetAutoApplyOnStartup(false)
    .OnBeforeUninstallFastCallback(_ => AutoStartHelper.Current.RemoveIfOwn())
    .Run();

Directory.CreateDirectory(Program.LogDirectory);

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Debug()
    .WriteTo.File(
        Path.Combine(Program.LogDirectory, "log-.txt"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 7,
        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
    .WriteTo.Debug()
    .CreateLogger();

// Prevent multiple instances
using var mutex = new Mutex(true, "AdhdTimeOrganizer_SingleInstance", out var isNew);

// A release downloaded earlier is applied now, before anything runs: the updater waits for this process to exit (the
// mutex goes with it) and starts the new build with the same arguments.
if (UpdateService.ApplyPendingAtStart(isNew, args))
{
    Log.CloseAndFlush();
    return;
}

if (!isNew)
{
    Log.Warning("Another instance is already running — exiting");
    MessageBox.Show("Activity Tracker is already running.", "Already Running",
        MessageBoxButtons.OK, MessageBoxIcon.Information);
    return;
}

Application.ThreadException += (_, e) =>
    Log.Error(e.Exception, "Unhandled UI thread exception");

AppDomain.CurrentDomain.UnhandledException += (_, e) =>
    Log.Fatal(e.ExceptionObject as Exception, "Unhandled domain exception");

TaskScheduler.UnobservedTaskException += (_, e) =>
    Log.Error(e.Exception, "Unobserved task exception");

Application.EnableVisualStyles();
Application.SetCompatibleTextRenderingDefault(false);
Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);

Log.Information("Application starting");

var config = AppConfig.Load();

try
{
    Application.Run(new TrayApplicationContext(config));
}
finally
{
    Log.Information("Application exiting");
    Log.CloseAndFlush();
}

public partial class Program
{
    public static string LogDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AdhdTimeOrganizer", "logs");
}
