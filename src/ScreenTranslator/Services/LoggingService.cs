using System;
using System.Collections.Generic;

namespace ScreenTranslator.Services
{
    public sealed class LoggingService
    {
        public LoggingService(string logDirectory = null) { }
        public void Info(string component, string message) { }
        public void Warn(string component, string message) { }
        public void Error(string component, string message, Exception ex = null) { }
        public IReadOnlyList<string> GetRecentLogs() => Array.Empty<string>();
        public string LogFilePath => string.Empty;
        public static string SanitizeMessage(string input) => input ?? string.Empty;
    }
}
