using System;
using System.Threading;
using System.Windows.Forms;

namespace Wendel;

internal static class Program
{
    private const int DefaultSimulatedMonitors = 2;

    [STAThread]
    private static int Main(string[] args)
    {
        using var mutex = new Mutex(true, @"Local\wendel-single-instance", out bool createdNew);
        if (!createdNew)
            return 1;

        Log.Rotate();
        Log.Info($"wendel {Application.ProductVersion} started");
        Application.ThreadException += OnThreadException;
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Log.Error(e.IsTerminating ? "Unhandled exception, exiting" : "Unhandled exception", e.ExceptionObject.ToString());

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        using (var context = new TrayContext(CreateBackend(args)))
            Application.Run(context);
        Log.Info("wendel exited");
        return 0;
    }

    /// <summary>
    /// <c>--simulate [N]</c> replaces the real monitors with N simulated ones. Only the first has
    /// an ambient light sensor, so one run shows a slider with and one without a checkbox.
    /// </summary>
    private static IMonitorBackend CreateBackend(string[] args)
    {
        int index = Array.IndexOf(args, "--simulate");
        if (index < 0)
            return new DdcBackend();
        int count = index + 1 < args.Length && int.TryParse(args[index + 1], out int n) && n >= 0
            ? n
            : DefaultSimulatedMonitors;
        Log.Info($"Simulated monitors: {count}");
        var backend = new SimulatedBackend(count);
        if (count > 0)
            backend.Monitors[0].SensorAnswers = true;
        return backend;
    }

    /// <summary>Logs an exception on the UI thread, then offers the same dialog WinForms shows by default.</summary>
    private static void OnThreadException(object sender, ThreadExceptionEventArgs e)
    {
        Log.Error("Unhandled exception", e.Exception.ToString());
        using var dialog = new ThreadExceptionDialog(e.Exception);
        if (dialog.ShowDialog() == DialogResult.Abort)
            Application.Exit();
    }
}
