using System;
using System.Collections.Generic;
using System.Windows.Interop;
using ScreenTranslator.Models;
using ScreenTranslator.Win32;

namespace ScreenTranslator.Services
{
    public sealed class HotkeyService : IDisposable
    {
        private const int IdTranslateCursor = 9001;
        private const int IdTranslateRegion = 9002;
        private const int IdToggleOverlay = 9003;
        private const int IdEscapeOverlay = 9004;

        private readonly LoggingService _logger;
        private readonly SettingsService _settings;
        private HwndSource _hwndSource;
        private bool _escapeRegistered;
        private bool _disposed;

        public bool IsPaused { get; private set; }

        public event Action TranslateCursorRequested;
        public event Action TranslateRegionRequested;
        public event Action ToggleOverlayRequested;
        public event Action EscapePressed;
        public event Action<string> HotkeyConflictDetected;
        public event Action StateChanged;

        public HotkeyService(LoggingService logger, SettingsService settings)
        {
            _logger = logger;
            _settings = settings;
        }

        public void Initialize()
        {
            if (_hwndSource != null) return;

            var parameters = new HwndSourceParameters("ScreenTranslatorHotkeySink")
            {
                Width = 0,
                Height = 0,
                PositionX = 0,
                PositionY = 0,
                WindowStyle = 0
            };
            _hwndSource = new HwndSource(parameters);
            _hwndSource.AddHook(WndProc);

            RegisterAllConfiguredHotkeys();
        }

        public List<string> RegisterAllConfiguredHotkeys()
        {
            var conflicts = new List<string>();
            if (_hwndSource == null || _hwndSource.Handle == IntPtr.Zero) return conflicts;

            UnregisterMainHotkeys();

            if (IsPaused)
            {
                _settings.Current.CursorHotkey.IsRegistered = false;
                _settings.Current.CursorHotkey.StatusMessage = "Приостановлено";
                _settings.Current.RegionHotkey.IsRegistered = false;
                _settings.Current.RegionHotkey.StatusMessage = "Приостановлено";
                _settings.Current.ToggleOverlayHotkey.IsRegistered = false;
                _settings.Current.ToggleOverlayHotkey.StatusMessage = "Приостановлено";
                StateChanged?.Invoke();
                return conflicts;
            }

            RegisterSingle(IdTranslateCursor, _settings.Current.CursorHotkey, conflicts);
            RegisterSingle(IdTranslateRegion, _settings.Current.RegionHotkey, conflicts);
            RegisterSingle(IdToggleOverlay, _settings.Current.ToggleOverlayHotkey, conflicts);

            if (conflicts.Count > 0)
            {
                string msg = "Конфликт горячих клавиш: " + string.Join("; ", conflicts) + ". Измените комбинацию в разделе «Горячие клавиши».";
                _logger?.Warn("HotkeyService", msg);
                HotkeyConflictDetected?.Invoke(msg);
            }
            else
            {
                _logger?.Info("HotkeyService", "Все глобальные горячие клавиши успешно зарегистрированы.");
            }

            StateChanged?.Invoke();
            return conflicts;
        }

        private void RegisterSingle(int id, HotkeyConfig config, List<string> conflicts)
        {
            if (config == null || config.VirtualKey == 0)
            {
                if (config != null)
                {
                    config.IsRegistered = false;
                    config.StatusMessage = "Не задано";
                }
                return;
            }

            uint flags = config.Modifiers | NativeMethods.MOD_NOREPEAT;
            bool ok = NativeMethods.RegisterHotKey(_hwndSource.Handle, id, flags, config.VirtualKey);
            config.IsRegistered = ok;
            if (ok)
            {
                config.StatusMessage = "Активно";
            }
            else
            {
                config.StatusMessage = "Занято другой программой";
                conflicts.Add($"{config.ToReadableString()} ({config.DisplayName})");
            }
        }

        public void SetEscapeHookEnabled(bool enable)
        {
            if (_hwndSource == null || _hwndSource.Handle == IntPtr.Zero) return;

            if (enable && !_escapeRegistered)
            {
                _escapeRegistered = NativeMethods.RegisterHotKey(
                    _hwndSource.Handle,
                    IdEscapeOverlay,
                    NativeMethods.MOD_NOREPEAT,
                    0x1B); // VK_ESCAPE
            }
            else if (!enable && _escapeRegistered)
            {
                NativeMethods.UnregisterHotKey(_hwndSource.Handle, IdEscapeOverlay);
                _escapeRegistered = false;
            }
        }

        public void SetPaused(bool paused)
        {
            if (IsPaused == paused) return;
            IsPaused = paused;
            _logger?.Info("HotkeyService", IsPaused ? "Переводчик приостановлен." : "Переводчик возобновлён.");
            RegisterAllConfiguredHotkeys();
        }

        public void TogglePaused()
        {
            SetPaused(!IsPaused);
        }

        private void UnregisterMainHotkeys()
        {
            if (_hwndSource == null || _hwndSource.Handle == IntPtr.Zero) return;
            NativeMethods.UnregisterHotKey(_hwndSource.Handle, IdTranslateCursor);
            NativeMethods.UnregisterHotKey(_hwndSource.Handle, IdTranslateRegion);
            NativeMethods.UnregisterHotKey(_hwndSource.Handle, IdToggleOverlay);
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == NativeMethods.WM_HOTKEY)
            {
                int id = wParam.ToInt32();
                switch (id)
                {
                    case IdTranslateCursor:
                        if (!IsPaused)
                        {
                            handled = true;
                            TranslateCursorRequested?.Invoke();
                        }
                        break;
                    case IdTranslateRegion:
                        if (!IsPaused)
                        {
                            handled = true;
                            TranslateRegionRequested?.Invoke();
                        }
                        break;
                    case IdToggleOverlay:
                        if (!IsPaused)
                        {
                            handled = true;
                            ToggleOverlayRequested?.Invoke();
                        }
                        break;
                    case IdEscapeOverlay:
                        handled = true;
                        EscapePressed?.Invoke();
                        break;
                }
            }
            return IntPtr.Zero;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                SetEscapeHookEnabled(false);
                UnregisterMainHotkeys();
                if (_hwndSource != null)
                {
                    _hwndSource.RemoveHook(WndProc);
                    _hwndSource.Dispose();
                    _hwndSource = null;
                }
            }
            catch
            {
                // Ignore shutdown cleanup errors
            }
        }
    }
}
