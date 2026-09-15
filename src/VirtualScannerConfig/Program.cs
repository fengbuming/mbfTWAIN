using System;
using System.Threading;
using System.Windows.Forms;

namespace MbfTwain.VirtualScannerConfig;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        DiagnosticsLog.Write("UI", $"Process start logPath={DiagnosticsLog.PathOnDisk}");

        using (Mutex? singleInstance = AcquireSingleInstance())
        {
            if (singleInstance is null)
            {
                DiagnosticsLog.Write("UI", "Another UI instance is already running; exiting");
                return;
            }

            RunApplication();
        }
    }

    private static void RunApplication()
    {
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, args) =>
        {
            DiagnosticsLog.WriteException("UI", args.Exception, "Application.ThreadException");
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception exception)
            {
                DiagnosticsLog.WriteException("UI", exception, "AppDomain.CurrentDomain.UnhandledException");
            }
            else
            {
                DiagnosticsLog.Write("UI", $"Unhandled exception object: {args.ExceptionObject}");
            }
        };

        ApplicationConfiguration.Initialize();
        try
        {
            Application.Run(new MainForm());
            DiagnosticsLog.Write("UI", "Application.Run exited normally");
        }
        catch (Exception exception)
        {
            DiagnosticsLog.WriteException("UI", exception, "Application.Run");
            throw;
        }
    }

    private static Mutex? AcquireSingleInstance()
    {
        const string name = "mbfTwain.VirtualScannerConfig.SingleInstance.v1";
        try
        {
            // Scope the named mutex to the current user and session (the .NET 10
            // default DACL grants the user SID), so duplicate detection works
            // across UAC integrity levels for the same user.
            var options = new NamedWaitHandleOptions
            {
                CurrentUserOnly = true,
                CurrentSessionOnly = true,
            };

            Mutex mutex = new(true, name, options, out bool createdNew);
            if (!createdNew)
            {
                mutex.Dispose();
                return null;
            }

            return mutex;
        }
        catch (Exception exception)
        {
            DiagnosticsLog.WriteException("UI", exception, "AcquireSingleInstance");
            return null;
        }
    }
}
