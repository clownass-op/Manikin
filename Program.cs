using System;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace ManikinApp
{
    static class Program
    {
        static string LogFile
        {
            get { return Path.Combine(Path.GetTempPath(), "fb-debug.log"); }
        }

        static void Log(string line)
        {
            try { File.AppendAllText(LogFile, DateTime.Now.ToString("HH:mm:ss.fff") + " [startup] " + line + Environment.NewLine); } catch { }
        }

        // Global last-resort handlers: without these any exception on the UI
        // thread (or an async-void continuation like OnLoad) silently kills
        // the process, which looks like "double-click -> flashes -> gone".
        // Now the window stays up and the real error is shown + logged.
        [STAThread]
        static int Main(string[] args)
        {
            // Which store saves go to, logged once at startup so a token that
            // is missing or expired is visible in the log, not just as saves
            // quietly landing in Supabase.
            Log("github store: " + (GitHub.Writable
                ? "writable, saves go to " + GitHub.Owner + "/" + GitHub.Repo
                : "read-only (no github.key), saves fall back to Supabase"));

            // Offscreen render regression suite: no dialogs, exit code = result.
            if (HeadlessRunner.Requested(args))
            {
                AppDomain.CurrentDomain.UnhandledException += (s, e) => Log("FATAL headless: " + e.ExceptionObject);
                Application.ThreadException += (s, e) => Log("headless UI thread: " + e.Exception);
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
                return HeadlessRunner.Run(args);
            }

            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                var ex = e.ExceptionObject as Exception;
                Log("FATAL AppDomain: " + (ex != null ? ex.ToString() : e.ExceptionObject));
                MessageBox.Show("Manikin hit an unexpected error and has to close.\n\n" +
                    (ex != null ? ex.Message : e.ExceptionObject.ToString()) +
                    "\n\nDetails were written to " + LogFile,
                    "Manikin", MessageBoxButtons.OK, MessageBoxIcon.Error);
            };
            Application.ThreadException += (s, e) =>
            {
                Log("FATAL UI thread: " + e.Exception);
                MessageBox.Show("Manikin hit an unexpected error.\n\n" + e.Exception.Message +
                    "\n\nDetails were written to " + LogFile,
                    "Manikin", MessageBoxButtons.OK, MessageBoxIcon.Error);
            };
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            try
            {
                Application.Run(new AppForm());
            }
            catch (Exception ex)
            {
                Log("FATAL Main: " + ex);
                MessageBox.Show("Manikin could not start.\n\n" + ex.Message +
                    "\n\nDetails were written to " + LogFile,
                    "Manikin", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            return 0;
        }
    }
}
