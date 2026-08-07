using System;
using System.IO;
using System.Threading;

namespace CustomSceneCreator {
    /// <summary>
    /// File logger. Deliberately dependency-free (no MCM, no campaign) because the earliest thing
    /// this mod does runs at main-menu time, before any game exists.
    /// </summary>
    internal static class TraceLogger {
        private const int MaxLogLines = 4000;
        private const int TrimCheckInterval = 200;

        private static readonly object Sync = new();
        private static int _sequence;
        private static bool _sessionStarted;
        private static int _writesSinceTrim;

        /// <summary>Set false to silence the log; the boot spike leaves it on.</summary>
        public static bool Enabled = true;

        private static string LogDirectoryPath =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "Mount and Blade II Bannerlord", "logs");

        public static string LogFilePath =>
            Path.Combine(LogDirectoryPath, "CustomSceneCreator.trace.log");

        public static void StartSession(string reason) {
            lock (Sync) {
                try {
                    Directory.CreateDirectory(LogDirectoryPath);
                    if (!_sessionStarted) {
                        File.AppendAllText(LogFilePath,
                            Environment.NewLine + "===== New Custom Scene Creator Session =====" + Environment.NewLine);
                        TrimLogFileToLimit();
                        _sessionStarted = true;
                    }
                } catch {
                    return;
                }
            }
            Write("Session", reason);
        }

        public static void Write(string source, string message) {
            if (!Enabled) return;
            lock (Sync) {
                try {
                    Directory.CreateDirectory(LogDirectoryPath);
                    int entryNumber = ++_sequence;
                    string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{entryNumber:D5}] " +
                                  $"[T{Thread.CurrentThread.ManagedThreadId}] {source}: {message}{Environment.NewLine}";
                    File.AppendAllText(LogFilePath, line);

                    if (++_writesSinceTrim >= TrimCheckInterval) {
                        _writesSinceTrim = 0;
                        TrimLogFileToLimit();
                    }
                } catch {
                }
            }
        }

        /// <summary>Logs an exception with its full chain — boot failures are usually nested.</summary>
        public static void WriteException(string source, string context, Exception ex) {
            Write(source, $"{context} — {ex.GetType().Name}: {ex.Message}");
            Exception? inner = ex.InnerException;
            int depth = 0;
            while (inner != null && depth++ < 5) {
                Write(source, $"    inner[{depth}] {inner.GetType().Name}: {inner.Message}");
                inner = inner.InnerException;
            }
            Write(source, $"    stack: {ex.StackTrace}");
        }

        private static void TrimLogFileToLimit() {
            if (!File.Exists(LogFilePath)) return;
            string[] lines = File.ReadAllLines(LogFilePath);
            if (lines.Length <= MaxLogLines) return;

            string[] newestLines = new string[MaxLogLines];
            Array.Copy(lines, lines.Length - MaxLogLines, newestLines, 0, MaxLogLines);
            File.WriteAllLines(LogFilePath, newestLines);
        }
    }
}
