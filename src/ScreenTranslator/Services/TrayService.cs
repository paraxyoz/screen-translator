using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using ScreenTranslator.Models;
using ScreenTranslator.UI;
using ScreenTranslator.Win32;
using WinForms = System.Windows.Forms;
using WpfBrushes = System.Windows.Media.Brushes;

namespace ScreenTranslator.Services
{
    public sealed class TrayService : IDisposable
    {
        private readonly LoggingService _logger;
        private readonly SettingsService _settings;
        private readonly HotkeyService _hotkeys;
        private WinForms.NotifyIcon _notifyIcon;
        private IntPtr _currentIconHandle = IntPtr.Zero;
        private TrayPopupWindow _menuWindow;
        private bool _disposed;

        public event Action OpenMainWindowRequested;
        public event Action TranslateCursorRequested;
        public event Action TranslateRegionRequested;
        public event Action ToggleOverlayRequested;
        public event Action TogglePauseRequested;
        public event Action ExitApplicationRequested;

        public TrayService(LoggingService logger, SettingsService settings, HotkeyService hotkeys)
        {
            _logger = logger;
            _settings = settings;
            _hotkeys = hotkeys;
        }

        public void Initialize()
        {
            if (_notifyIcon != null) return;

            _notifyIcon = new WinForms.NotifyIcon
            {
                Visible = true,
                Text = "Screen Translator — Активен"
            };

            UpdateTrayState(_hotkeys.IsPaused);

            _notifyIcon.MouseUp += OnNotifyIconMouseUp;
            _notifyIcon.DoubleClick += (s, e) => OpenMainWindowRequested?.Invoke();

            _hotkeys.StateChanged += () =>
            {
                Application.Current?.Dispatcher.BeginInvoke(new Action(() => UpdateTrayState(_hotkeys.IsPaused)));
            };
        }

        public void UpdateTrayState(bool isPaused)
        {
            if (_notifyIcon == null) return;

            try
            {
                IntPtr oldHandle = _currentIconHandle;
                _notifyIcon.Icon = CreateVectorTrayIcon(isPaused, out _currentIconHandle);
                if (oldHandle != IntPtr.Zero)
                {
                    NativeMethods.DeleteObject(oldHandle);
                }

                string status = isPaused ? "Приостановлен" : "Активен";
                _notifyIcon.Text = $"Screen Translator — {status}";
            }
            catch (Exception ex)
            {
                _logger?.Error("TrayService", "Ошибка обновления иконки системного трея", ex);
            }
        }

        private void OnNotifyIconMouseUp(object sender, WinForms.MouseEventArgs e)
        {
            if (e.Button == WinForms.MouseButtons.Right)
            {
                ShowCustomTrayMenu();
            }
            else if (e.Button == WinForms.MouseButtons.Left)
            {
                OpenMainWindowRequested?.Invoke();
            }
        }

        private void ShowCustomTrayMenu()
        {
            if (_menuWindow != null)
            {
                try { _menuWindow.Close(); } catch { }
                _menuWindow = null;
            }

            _menuWindow = new TrayPopupWindow(
                _settings.Current,
                _hotkeys.IsPaused,
                onOpenSettings: () => OpenMainWindowRequested?.Invoke(),
                onTranslateCursor: () => TranslateCursorRequested?.Invoke(),
                onTranslateRegion: () => TranslateRegionRequested?.Invoke(),
                onToggleOverlay: () => ToggleOverlayRequested?.Invoke(),
                onTogglePause: () => TogglePauseRequested?.Invoke(),
                onExit: () => ExitApplicationRequested?.Invoke());

            _menuWindow.ShowNearCursor();
        }

        public static Icon CreateVectorTrayIcon(bool isPaused, out IntPtr hIconOut)
        {
            using var bmp = new Bitmap(32, 32);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.Clear(System.Drawing.Color.Transparent);

                var bgColor = isPaused
                    ? System.Drawing.Color.FromArgb(255, 71, 85, 105)
                    : System.Drawing.Color.FromArgb(255, 59, 130, 246);

                using (var path = CreateRoundedRectPath(new RectangleF(2, 2, 28, 28), 7f))
                using (var brush = new SolidBrush(bgColor))
                {
                    g.FillPath(brush, path);
                }

                using (var pen = new System.Drawing.Pen(System.Drawing.Color.White, 2.6f))
                {
                    pen.StartCap = LineCap.Round;
                    pen.EndCap = LineCap.Round;
                    // Draw crisp "T" + screen corner bracket motif
                    g.DrawLine(pen, 9f, 10f, 23f, 10f);
                    g.DrawLine(pen, 16f, 10f, 16f, 23f);
                }

                if (isPaused)
                {
                    using var badgeBrush = new SolidBrush(System.Drawing.Color.FromArgb(245, 158, 11));
                    g.FillEllipse(badgeBrush, 20, 20, 10, 10);
                }
            }

            hIconOut = bmp.GetHicon();
            return Icon.FromHandle(hIconOut);
        }

        private static GraphicsPath CreateRoundedRectPath(RectangleF rect, float radius)
        {
            float d = radius * 2f;
            var path = new GraphicsPath();
            path.AddArc(rect.X, rect.Y, d, d, 180, 90);
            path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
            path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
            path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try
            {
                if (_menuWindow != null)
                {
                    _menuWindow.Close();
                    _menuWindow = null;
                }
                if (_notifyIcon != null)
                {
                    _notifyIcon.Visible = false;
                    _notifyIcon.Dispose();
                    _notifyIcon = null;
                }
                if (_currentIconHandle != IntPtr.Zero)
                {
                    NativeMethods.DeleteObject(_currentIconHandle);
                    _currentIconHandle = IntPtr.Zero;
                }
            }
            catch
            {
                // Ignore cleanup exceptions
            }
        }
    }

    internal sealed class TrayPopupWindow : Window
    {
        public TrayPopupWindow(
            AppSettings settings,
            bool isPaused,
            Action onOpenSettings,
            Action onTranslateCursor,
            Action onTranslateRegion,
            Action onToggleOverlay,
            Action onTogglePause,
            Action onExit)
        {
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            AllowsTransparency = true;
            Background = WpfBrushes.Transparent;
            Topmost = true;
            ShowInTaskbar = false;
            SizeToContent = SizeToContent.WidthAndHeight;

            var outerBorder = new Border
            {
                Width = 278,
                CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(6),
                SnapsToDevicePixels = true
            };
            outerBorder.SetResourceReference(Border.BackgroundProperty, "CardBgBrush");
            outerBorder.SetResourceReference(Border.BorderBrushProperty, "BorderSubtleBrush");

            var stack = new StackPanel();

            // Header row with app name & status dot
            var headerGrid = new Grid { Margin = new Thickness(10, 6, 10, 8) };
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var titleText = new TextBlock
            {
                Text = "Screen Translator",
                FontSize = 12.5,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            };
            titleText.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
            Grid.SetColumn(titleText, 0);
            headerGrid.Children.Add(titleText);

            var statusPill = new Border
            {
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(6, 2, 6, 2)
            };
            statusPill.SetResourceReference(Border.BackgroundProperty, "InputBgBrush");
            var statusText = new TextBlock
            {
                Text = isPaused ? "Пауза" : "Активен",
                FontSize = 11,
                FontWeight = FontWeights.Medium
            };
            statusText.SetResourceReference(
                TextBlock.ForegroundProperty,
                isPaused ? "WarningBrush" : "SuccessBrush");
            statusPill.Child = statusText;
            Grid.SetColumn(statusPill, 1);
            headerGrid.Children.Add(statusPill);
            stack.Children.Add(headerGrid);

            stack.Children.Add(CreateSeparator());

            stack.Children.Add(CreateMenuItem(
                "Открыть настройки",
                VectorIcons.Settings,
                null,
                () => { Close(); onOpenSettings?.Invoke(); }));

            stack.Children.Add(CreateMenuItem(
                "Перевести текст под курсором",
                VectorIcons.CursorTarget,
                settings.CursorHotkey?.ToReadableString(),
                () => { Close(); onTranslateCursor?.Invoke(); }));

            stack.Children.Add(CreateMenuItem(
                "Выделить область",
                VectorIcons.RegionSelect,
                settings.RegionHotkey?.ToReadableString(),
                () => { Close(); onTranslateRegion?.Invoke(); }));

            stack.Children.Add(CreateMenuItem(
                "Показать / скрыть перевод",
                VectorIcons.EyeShow,
                settings.ToggleOverlayHotkey?.ToReadableString(),
                () => { Close(); onToggleOverlay?.Invoke(); }));

            stack.Children.Add(CreateSeparator());

            stack.Children.Add(CreateMenuItem(
                isPaused ? "Включить переводчик" : "Приостановить переводчик",
                isPaused ? VectorIcons.Play : VectorIcons.Pause,
                null,
                () => { Close(); onTogglePause?.Invoke(); }));

            stack.Children.Add(CreateMenuItem(
                "Выход",
                VectorIcons.Close,
                null,
                () => { Close(); onExit?.Invoke(); },
                isDanger: true));

            outerBorder.Child = stack;
            Content = outerBorder;

            Deactivated += (s, e) =>
            {
                try { Close(); } catch { }
            };
            KeyDown += (s, e) =>
            {
                if (e.Key == Key.Escape)
                {
                    try { Close(); } catch { }
                }
            };
        }

        public void ShowNearCursor()
        {
            Show();
            UpdateLayout();

            if (NativeMethods.GetCursorPos(out var pt))
            {
                double dpi = NativeMethods.GetDpiScaleForPoint(pt.X, pt.Y);
                if (dpi <= 0) dpi = 1.0;

                var screen = WinForms.Screen.FromPoint(new System.Drawing.Point(pt.X, pt.Y));
                var workArea = screen.WorkingArea;

                int winPhysW = (int)Math.Ceiling(ActualWidth * dpi);
                int winPhysH = (int)Math.Ceiling(ActualHeight * dpi);

                int targetX = Math.Max(workArea.Left + 8, Math.Min(pt.X - winPhysW / 2, workArea.Right - winPhysW - 8));
                int targetY = Math.Max(workArea.Top + 8, Math.Min(pt.Y - winPhysH - 8, workArea.Bottom - winPhysH - 8));

                IntPtr hwnd = new WindowInteropHelper(this).Handle;
                if (hwnd != IntPtr.Zero)
                {
                    NativeMethods.SetWindowPos(
                        hwnd,
                        NativeMethods.HWND_TOPMOST,
                        targetX,
                        targetY,
                        winPhysW,
                        winPhysH,
                        NativeMethods.SWP_SHOWWINDOW);
                    NativeMethods.SetForegroundWindow(hwnd);
                }
            }

            Activate();
            Focus();
        }

        private static Border CreateSeparator()
        {
            var sep = new Border
            {
                Height = 1,
                Margin = new Thickness(6, 4, 6, 4)
            };
            sep.SetResourceReference(Border.BackgroundProperty, "BorderSubtleBrush");
            return sep;
        }

        private static Border CreateMenuItem(string text, string iconData, string shortcut, Action onClick, bool isDanger = false)
        {
            var rowBorder = new Border
            {
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(10, 7, 10, 7),
                Background = WpfBrushes.Transparent,
                Cursor = Cursors.Hand
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var icon = VectorIcons.CreateIcon(iconData, 14, isDanger ? "DangerBrush" : "TextSecondaryBrush");
            icon.VerticalAlignment = VerticalAlignment.Center;
            icon.Margin = new Thickness(0, 0, 10, 0);
            Grid.SetColumn(icon, 0);
            grid.Children.Add(icon);

            var label = new TextBlock
            {
                Text = text,
                FontSize = 12.5,
                VerticalAlignment = VerticalAlignment.Center
            };
            label.SetResourceReference(TextBlock.ForegroundProperty, isDanger ? "DangerBrush" : "TextPrimaryBrush");
            Grid.SetColumn(label, 1);
            grid.Children.Add(label);

            if (!string.IsNullOrWhiteSpace(shortcut))
            {
                var scBlock = new TextBlock
                {
                    Text = shortcut.Replace(" + ", "+"),
                    FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                    FontSize = 11,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(8, 0, 0, 0)
                };
                scBlock.SetResourceReference(TextBlock.ForegroundProperty, "TextMutedBrush");
                Grid.SetColumn(scBlock, 2);
                grid.Children.Add(scBlock);
            }

            rowBorder.Child = grid;
            rowBorder.MouseEnter += (s, e) => rowBorder.SetResourceReference(Border.BackgroundProperty, "CardHoverBrush");
            rowBorder.MouseLeave += (s, e) => rowBorder.Background = WpfBrushes.Transparent;
            rowBorder.MouseLeftButtonUp += (s, e) =>
            {
                e.Handled = true;
                onClick?.Invoke();
            };

            return rowBorder;
        }
    }
}
