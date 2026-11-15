using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace SennheiserTray
{
    public static class AppLogger
    {
        private static readonly object SyncRoot = new();
        private static readonly string LogDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SennheiserTray",
            "logs");
        private static string LogPath => Path.Combine(
            LogDirectory,
            $"app-{DateTime.Now:yyyy-MM-dd}.log");

        public static void LogException(string context, Exception exception)
        {
            ArgumentNullException.ThrowIfNull(exception);
            string entry = $"{DateTimeOffset.Now:O} [{context}] {exception}{Environment.NewLine}";
            try
            {
                lock (SyncRoot)
                {
                    Directory.CreateDirectory(LogDirectory);
                    File.AppendAllText(LogPath, entry, Encoding.UTF8);
                    RemoveExpiredLogs(DateTime.Now.Date.AddDays(-30));
                }
            }
            catch (Exception logException)
            {
                Debug.WriteLine($"Could not write app log: {logException}");
                Debug.WriteLine(entry);
            }
        }

        private static void RemoveExpiredLogs(DateTime cutoffDate)
        {
            foreach (string path in Directory.EnumerateFiles(LogDirectory, "app-*.log"))
            {
                string fileName = Path.GetFileNameWithoutExtension(path);
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
