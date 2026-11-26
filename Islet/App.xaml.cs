using System;
using System.IO;
using System.Threading;
using System.Windows;
using Islet.Services;

namespace Islet;

public partial class App : Application
{
    Mutex? _mutex;
    Tray? _tray;

    protected override void OnStartup(StartupEventArgs e)
    {
        _mutex = new Mutex(true, "Islet.SingleInstance.v1", out bool created);
        if (!created) { Shutdown(); return; }

        base.OnStartup(e);
        // The island is a tiny transparent (layered) window, which WPF must read back from the GPU every frame anyway.
        // Rendering it on the CPU avoids loading the GPU driver into our process (~30 MB) at negligible CPU cost.
        if (Environment.GetEnvironmentVariable("ISLET_GPU") != "1")
            System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        DispatcherUnhandledException += (_, a) => { Log(a.Exception); a.Handled = true; };
        AppDomain.CurrentDomain.UnhandledException += (_, a) => Log(a.ExceptionObject as Exception);

        var settings = Settings.Load();
        var win = new IslandWindow(settings);
        win.Show();
        _tray = new Tray(win);
        win.Tray = _tray;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        base.OnExit(e);
    }

    static void Log(Exception? ex)
    {
        if (ex == null) return;
        try
        {
            Directory.CreateDirectory(Settings.Dir);
            File.AppendAllText(Path.Combine(Settings.Dir, "error.log"), $"[{DateTime.Now:u}] {ex}\n\n");
        }
        catch { }
    }
}
