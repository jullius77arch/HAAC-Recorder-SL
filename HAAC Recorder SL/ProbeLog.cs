using System;
using System.Collections.Generic;
using System.IO;
using System.IO.IsolatedStorage;
using System.Text;

namespace HAAC_Recorder_SL
{
    /// <summary>
    /// Append-only log written to isolated storage.
    ///
    /// Isolated storage rather than an in-memory list, because the whole point
    /// of the test is to find out whether the app survives. If it doesn't, the
    /// process is gone and anything held in memory goes with it — so every
    /// line has to be on disk the moment it's written. A failed run is exactly
    /// the run whose log matters most.
    ///
    /// Every write opens and closes the file. That's wasteful in general, but
    /// at one line every five seconds it costs nothing measurable, and it means
    /// there is never an unflushed buffer to lose.
    /// </summary>
    public static class ProbeLog
    {
        private const string LogFileName = "locktest.log";

        private static readonly object Sync = new object();

        public static void Append(string message)
        {
            var line = string.Format(
                "{0:yyyy-MM-dd HH:mm:ss}  {1}",
                DateTime.Now,
                message);

            try
            {
                lock (Sync)
                {
                    using (var store = IsolatedStorageFile.GetUserStoreForApplication())
                    using (var stream = new IsolatedStorageFileStream(
                        LogFileName, FileMode.Append, FileAccess.Write, store))
                    using (var writer = new StreamWriter(stream))
                    {
                        writer.WriteLine(line);
                    }
                }
            }
            catch
            {
                // Logging must never be the thing that breaks a test run.
            }
        }

        public static bool HasEntries()
        {
            try
            {
                using (var store = IsolatedStorageFile.GetUserStoreForApplication())
                {
                    return store.FileExists(LogFileName);
                }
            }
            catch
            {
                return false;
            }
        }

        public static string ReadAll()
        {
            try
            {
                lock (Sync)
                {
                    using (var store = IsolatedStorageFile.GetUserStoreForApplication())
                    {
                        if (!store.FileExists(LogFileName))
                        {
                            return string.Empty;
                        }

                        using (var stream = new IsolatedStorageFileStream(
                            LogFileName, FileMode.Open, FileAccess.Read, store))
                        using (var reader = new StreamReader(stream))
                        {
                            return reader.ReadToEnd();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                return "Couldn't read the log: " + ex.Message;
            }
        }

        /// <summary>
        /// Returns the log split into lines, most recent last. Used to render
        /// a short tail on screen without exporting anything.
        /// </summary>
        public static List<string> ReadLines()
        {
            var lines = new List<string>();
            var text = ReadAll();

            if (string.IsNullOrEmpty(text))
            {
                return lines;
            }

            foreach (var line in text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries))
            {
                lines.Add(line);
            }

            return lines;
        }

        public static void Clear()
        {
            try
            {
                lock (Sync)
                {
                    using (var store = IsolatedStorageFile.GetUserStoreForApplication())
                    {
                        if (store.FileExists(LogFileName))
                        {
                            store.DeleteFile(LogFileName);
                        }
                    }
                }
            }
            catch
            {
            }
        }

        /// <summary>
        /// Builds the header block that goes at the top of an exported log, so
        /// a file pulled off the phone identifies itself without needing the
        /// surrounding context.
        /// </summary>
        public static string BuildHeader()
        {
            var sb = new StringBuilder();

            sb.AppendLine("HAAC Recorder — lock screen survival test");
            sb.AppendLine("Exported: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine("ApplicationIdleDetectionMode disabled: " + App.RunningUnderLockScreenEnabled);

            if (!string.IsNullOrEmpty(App.LockScreenSetupError))
            {
                sb.AppendLine("Setup error: " + App.LockScreenSetupError);
            }

            sb.AppendLine(new string('-', 60));

            return sb.ToString();
        }
    }
}
