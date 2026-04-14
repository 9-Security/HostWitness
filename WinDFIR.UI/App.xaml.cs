using System;
using System.IO;
using System.Text;
using System.Windows;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace WinDFIR.UI;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
        DispatcherUnhandledException += App_DispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;

        try
        {
            base.OnStartup(e);
            var mainWindow = new MainWindow();
            MainWindow = mainWindow;
            mainWindow.Show();
        }
        catch (Exception ex)
        {
            WriteStartupLog("OnStartup exception", ex);
            MessageBox.Show("Application failed to start. See startup log for details.", "HostWitness", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(-1);
        }
    }

    private void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        WriteStartupLog("DispatcherUnhandledException", e.Exception);
        e.Handled = true;
        Shutdown(-1);
    }

    private void CurrentDomain_UnhandledException(object? sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
        {
            WriteStartupLog("UnhandledException", ex);
        }
    }

    private void TaskScheduler_UnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        WriteStartupLog("UnobservedTaskException", e.Exception);
        e.SetObserved();
    }

    private static void WriteStartupLog(string context, Exception ex)
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HostWitness", "logs");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "startup.log");
            var builder = new StringBuilder();
            builder.AppendLine($"[{DateTime.UtcNow:O}] {context}");
            builder.AppendLine(ex.ToString());
            builder.AppendLine();
            File.AppendAllText(path, builder.ToString());
        }
        catch
        {
            // Ignore logging failures
        }
    }
}
