using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace SennheiserTray
{
    public static class AppLogger
    {
        private const long MaximumLogBytes = 5 * 1024 * 1024;
        private const int MaximumEntryCharacters = 16 * 1024;
        private static readonly object SyncRoot = new();
        private static readonly string LogDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SennheiserTray",
            "logs");
        private static string LogPath => Path.Combine(
            LogDirectory,
            $"app-{DateTime.Now:yyyy-MM-dd}.log");
        private static bool _cleanupCompleted;

        public static void Initialize()
        {
            try
            {
                lock (SyncRoot)
                {
                    Directory.CreateDirectory(LogDirectory);
                    RemoveExpiredLogs(DateTime.Now.Date.AddDays(-30));
                    _cleanupCompleted = true;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Could not initialize app logs: {ex}");
            }
        }

        public static void LogException(string context, Exception exception)
        {
            ArgumentNullException.ThrowIfNull(exception);
            string entry = FormatEntry(context, exception);
            try
            {
                lock (SyncRoot)
                {
                    Directory.CreateDirectory(LogDirectory);
                    if (!_cleanupCompleted)
                    {
                        RemoveExpiredLogs(DateTime.Now.Date.AddDays(-30));
                        _cleanupCompleted = true;
                    }

                    RotateCurrentLogIfNeeded(entry);
                    File.AppendAllText(LogPath, entry, Encoding.UTF8);
                }
            }
            catch (Exception logException)
            {
                Debug.WriteLine($"Could not write app log: {logException}");
                Debug.WriteLine(entry);
            }
        }

        private static string FormatEntry(string context, Exception exception)
        {
            string entry = $"{DateTimeOffset.Now:O} [{context}] {exception.GetType().Name}: {exception.Message}";
            string? stackTrace = exception.StackTrace;
            if (!string.IsNullOrWhiteSpace(stackTrace))
            {
                string[] frames = stackTrace.Split(
                    new[] { Environment.NewLine },
                    StringSplitOptions.RemoveEmptyEntries);
                entry += Environment.NewLine + string.Join(Environment.NewLine, frames.Take(4));
            }

            Exception? cause = exception.InnerException;
            for (int depth = 0; cause != null && depth < 3; depth++, cause = cause.InnerException)
            {
                entry += Environment.NewLine + $"Caused by {cause.GetType().Name}: {cause.Message}";
            }

            if (entry.Length > MaximumEntryCharacters)
            {
                entry = entry[..MaximumEntryCharacters] + " [entry truncated]";
            }

            return entry + Environment.NewLine;
        }

        private static void RotateCurrentLogIfNeeded(string entry)
        {
            if (!File.Exists(LogPath))
            {
                return;
            }

            long entryBytes = Encoding.UTF8.GetByteCount(entry);
            if (new FileInfo(LogPath).Length + entryBytes <= MaximumLogBytes)
            {
                return;
            }

            string backupPath = LogPath + ".1";
            if (File.Exists(backupPath))
            {
                File.Delete(backupPath);
            }

            File.Move(LogPath, backupPath);
        }

        private static void RemoveExpiredLogs(DateTime cutoffDate)
        {
            foreach (string path in Directory.EnumerateFiles(LogDirectory, "app-*"))
            {
                string fileName = Path.GetFileName(path);
                int extensionIndex = fileName.IndexOf(".log", StringComparison.Ordinal);
                if (extensionIndex < 0 || !fileName.StartsWith("app-", StringComparison.Ordinal))
                {
                    continue;
                }

                fileName = fileName[..extensionIndex];
                string dateText = fileName["app-".Length..];
                if (DateTime.TryParseExact(
                    dateText,
                    "yyyy-MM-dd",
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None,
                    out DateTime logDate)
                    && logDate.Date < cutoffDate)
                {
                    File.Delete(path);
                }
            }
        }
    }
}
