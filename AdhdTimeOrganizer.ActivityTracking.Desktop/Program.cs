using AdhdTimeOrganizer.ActivityTracking.Desktop;
using AdhdTimeOrganizer.ActivityTracking.Desktop.Models;
using Serilog;

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
