using System;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;
using ScreenTranslator.Models;

namespace ScreenTranslator.Services
{
    public sealed class SettingsService
    {
        private readonly LoggingService _logger;
        private readonly string _settingsFilePath;
        private readonly object _sync = new object();
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("ScreenTranslator.DPAPI.v1");
        private const string StartupRegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string StartupValueName = "ScreenTranslator";

        public AppSettings Current { get; private set; } = new AppSettings();

        public string SettingsFilePath => _settingsFilePath;

        public SettingsService(LoggingService logger, string customFilePath = null)
        {
            _logger = logger;
            if (string.IsNullOrWhiteSpace(customFilePath))
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "ScreenTranslator");
                Directory.CreateDirectory(dir);
                _settingsFilePath = Path.Combine(dir, "settings.json");
            }
            else
            {
                string dir = Path.GetDirectoryName(customFilePath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                _settingsFilePath = customFilePath;
            }
        }

        public void Load()
        {
            lock (_sync)
            {
                try
                {
                    if (!File.Exists(_settingsFilePath))
                    {
                        Current = new AppSettings();
                        SaveInternal();
                        return;
                    }

                    byte[] bytes = File.ReadAllBytes(_settingsFilePath);
                    using var ms = new MemoryStream(bytes);
                    var serializer = new DataContractJsonSerializer(typeof(AppSettings));
                    if (serializer.ReadObject(ms) is AppSettings loaded)
                    {
                        EnsureDefaults(loaded);
                        Current = loaded;
                        _logger?.Info("SettingsService", "Настройки успешно загружены.");
                    }
                }
                catch (Exception ex)
                {
                    _logger?.Error("SettingsService", "Не удалось загрузить настройки, используются значения по умолчанию", ex);
                    Current = new AppSettings();
                }
            }
        }

        public void Save()
        {
            lock (_sync)
            {
                SaveInternal();
            }
        }

        private void SaveInternal()
        {
            try
            {
                string dir = Path.GetDirectoryName(_settingsFilePath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                using var ms = new MemoryStream();
                var serializer = new DataContractJsonSerializer(typeof(AppSettings));
                serializer.WriteObject(ms, Current);
                File.WriteAllBytes(_settingsFilePath, ms.ToArray());
                _logger?.Info("SettingsService", "Настройки сохранены на диск.");
            }
            catch (Exception ex)
            {
                _logger?.Error("SettingsService", "Ошибка сохранения настроек", ex);
            }
        }

        public void SetDeepLApiKey(string plainKey)
        {
            Current.EncryptedDeepLApiKey = EncryptSecret(plainKey);
        }

        public string GetDeepLApiKey()
        {
            return DecryptSecret(Current.EncryptedDeepLApiKey);
        }

        public void SetOpenAiApiKey(string plainKey)
        {
            Current.EncryptedOpenAiApiKey = EncryptSecret(plainKey);
        }

        public string GetOpenAiApiKey()
        {
            return DecryptSecret(Current.EncryptedOpenAiApiKey);
        }

        public void ApplyStartupSetting(bool enable)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(StartupRegistryPath, writable: true);
                if (key == null) return;

                if (enable)
                {
                    string exePath = Assembly.GetEntryAssembly()?.Location ?? ProcessExecutablePath();
                    if (!string.IsNullOrEmpty(exePath))
                    {
                        key.SetValue(StartupValueName, $"\"{exePath}\" --minimized");
                        _logger?.Info("SettingsService", "Автозапуск с Windows включён.");
                    }
                }
                else
                {
                    if (key.GetValue(StartupValueName) != null)
                    {
                        key.DeleteValue(StartupValueName, throwOnMissingValue: false);
                        _logger?.Info("SettingsService", "Автозапуск с Windows отключён.");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.Error("SettingsService", "Не удалось обновить настройку автозапуска в реестре", ex);
            }
        }

        private static string ProcessExecutablePath()
        {
            try
            {
                return System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string EncryptSecret(string plainText)
        {
            if (string.IsNullOrWhiteSpace(plainText)) return string.Empty;
            try
            {
                byte[] plainBytes = Encoding.UTF8.GetBytes(plainText.Trim());
                byte[] cipherBytes = ProtectedData.Protect(plainBytes, Entropy, DataProtectionScope.CurrentUser);
                return Convert.ToBase64String(cipherBytes);
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string DecryptSecret(string encryptedBase64)
        {
            if (string.IsNullOrWhiteSpace(encryptedBase64)) return string.Empty;
            try
            {
                byte[] cipherBytes = Convert.FromBase64String(encryptedBase64);
                byte[] plainBytes = ProtectedData.Unprotect(cipherBytes, Entropy, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(plainBytes);
            }
            catch
            {
                return string.Empty;
            }
        }

        private static void EnsureDefaults(AppSettings s)
        {
            if (string.IsNullOrWhiteSpace(s.SourceLanguage)) s.SourceLanguage = "auto";
            if (string.IsNullOrWhiteSpace(s.TargetLanguage)) s.TargetLanguage = "ru";
            if (string.IsNullOrWhiteSpace(s.OverlayFontFamily)) s.OverlayFontFamily = "Segoe UI";
            if (s.OverlayOpacity < 0.25 || s.OverlayOpacity > 1.0) s.OverlayOpacity = 0.95;
            if (s.FixedFontSize < 8 || s.FixedFontSize > 48) s.FixedFontSize = 15.0;
            if (s.CursorCaptureWidth < 120) s.CursorCaptureWidth = 460;
            if (s.CursorCaptureHeight < 50) s.CursorCaptureHeight = 130;
            if (s.MaxCacheEntries < 10) s.MaxCacheEntries = 500;
            if (s.RequestTimeoutSeconds < 2) s.RequestTimeoutSeconds = 8;

            if (s.CursorHotkey == null || s.CursorHotkey.VirtualKey == 0)
            {
                s.CursorHotkey = new HotkeyConfig
                {
                    ActionId = "TranslateCursor",
                    DisplayName = "Перевести текст под курсором",
                    Modifiers = Win32.NativeMethods.MOD_CONTROL | Win32.NativeMethods.MOD_SHIFT,
                    VirtualKey = 'T'
                };
            }
            if (s.RegionHotkey == null || s.RegionHotkey.VirtualKey == 0)
            {
                s.RegionHotkey = new HotkeyConfig
                {
                    ActionId = "TranslateRegion",
                    DisplayName = "Выделить область для перевода",
                    Modifiers = Win32.NativeMethods.MOD_CONTROL | Win32.NativeMethods.MOD_SHIFT,
                    VirtualKey = 'A'
                };
            }
            if (s.ToggleOverlayHotkey == null || s.ToggleOverlayHotkey.VirtualKey == 0)
            {
                s.ToggleOverlayHotkey = new HotkeyConfig
                {
                    ActionId = "ToggleOverlay",
                    DisplayName = "Показать или скрыть текущий перевод",
                    Modifiers = Win32.NativeMethods.MOD_CONTROL | Win32.NativeMethods.MOD_SHIFT,
                    VirtualKey = 'H'
                };
            }
        }
    }
}
