using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace ScreenTranslator.Services
{
    public sealed class LoggingService
    {
        private readonly object _sync = new object();
        private readonly List<string> _recentLogs = new List<string>(256);
        private readonly string _logDir;
        private readonly string _logFilePath;
        private const int MaxMemoryLogs = 250;
        private const long MaxLogFileBytes = 2 * 1024 * 1024; // 2 MB

        private static readonly Regex BearerRegex = new Regex(
            @"(Bearer|DeepL-Auth-Key)\s+[A-Za-z0-9\-_\.:]+",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex KeyParamRegex = new Regex(
            @"(api[_-]?key|apikey|key|token|secret|password)\s*([=:])\s*([^\s&""',;]+)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex SkTokenRegex = new Regex(
            @"\bsk-[A-Za-z0-9\-_]{6,}",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public string LogFilePath => _logFilePath;

        public event Action<string> LogAdded;

        public LoggingService(string customDirectory = null)
        {
            _logDir = string.IsNullOrWhiteSpace(customDirectory)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ScreenTranslator", "logs")
                : customDirectory;

            try
            {
                Directory.CreateDirectory(_logDir);
            }
            catch
            {
                // Ignore if directory creation fails in restricted sandbox
            }

            _logFilePath = Path.Combine(_logDir, "app.log");
        }

        public static string SanitizeMessage(string input)
        {
            if (string.IsNullOrEmpty(input)) return string.Empty;
            string result = BearerRegex.Replace(input, "$1 [REDACTED]");
            result = KeyParamRegex.Replace(result, "$1$2[REDACTED]");
            result = SkTokenRegex.Replace(result, "sk-[REDACTED]");
            return result;
        }

        public void Info(string component, string message) => Write("INFO", component, message);

        public void Warn(string component, string message) => Write("WARN", component, message);

        public void Error(string component, string message, Exception ex = null)
        {
            string details = ex != null ? $"{message} | {ex.GetType().Name}: {ex.Message}" : message;
            Write("ERROR", component, details);
        }

        public IReadOnlyList<string> GetRecentLogs()
        {
            lock (_sync)
            {
                return _recentLogs.ToArray();
            }
        }

        private void Write(string level, string component, string message)
        {
            string safeMessage = SanitizeMessage(message);
            string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] [{component}] {safeMessage}";

            lock (_sync)
            {
                if (_recentLogs.Count >= MaxMemoryLogs)
                {
                    _recentLogs.RemoveAt(0);
                }
                _recentLogs.Add(line);

                try
                {
                    if (File.Exists(_logFilePath))
                    {
                        var info = new FileInfo(_logFilePath);
                        if (info.Length > MaxLogFileBytes)
                        {
                            string backup = _logFilePath + ".old";
                            if (File.Exists(backup)) File.Delete(backup);
                            File.Move(_logFilePath, backup);
                        }
                    }
                    File.AppendAllText(_logFilePath, line + Environment.NewLine, Encoding.UTF8);
                }
                catch
                {
                    // Avoid crashing app on file I/O contention
                }
            }

            try
            {
                LogAdded?.Invoke(line);
            }
            catch
            {
                // Ignore UI listener exceptions
            }
        }
    }
}
