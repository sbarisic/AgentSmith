using System.IO;
using System.Windows;
using AgentSmith.Application;

namespace AgentSmith.Desktop;

public partial class App : System.Windows.Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        RecorderTheme.Apply();
        Microsoft.Win32.SystemEvents.UserPreferenceChanged += ThemeChanged;
        try
        {
            string? directory = null; var executable = Path.Combine(AppContext.BaseDirectory, "smith.exe");
            var smoke = false;
            for (var i = 0; i < e.Args.Length; i++)
            {
                switch (e.Args[i])
                {
                    case "--data-dir" when i + 1 < e.Args.Length: directory = e.Args[++i]; break;
                    case "--smith" when i + 1 < e.Args.Length: executable = e.Args[++i]; break;
                    case "--smoke-test": smoke = true; break;
                    default: throw new ArgumentException("Usage: AgentSmith.Recorder [--data-dir directory] [--smith path]");
                }
            }
            if (smoke && directory is null) throw new ArgumentException("Smoke check requires an explicit disposable --data-dir.");
            var recorder = new CallRecorder(directory, executable);
            var window = new MainWindow(recorder);
            if (smoke)
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown;
                try { await window.RunSmokeCheck(); }
                finally { recorder.Dispose(); }
                Shutdown(0); return;
            }
            MainWindow = window; window.Show();
        }
        catch (Exception ex)
        {
            if (e.Args.Contains("--smoke-test")) Console.Error.WriteLine(ex);
            else MessageBox.Show(ex.Message, "AgentSmith Recorder", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }
    private void ThemeChanged(object sender, Microsoft.Win32.UserPreferenceChangedEventArgs e) => Dispatcher.BeginInvoke(new Action(() => RecorderTheme.Apply()));
    protected override void OnExit(ExitEventArgs e)
    {
        Microsoft.Win32.SystemEvents.UserPreferenceChanged -= ThemeChanged;
        base.OnExit(e);
    }
}
