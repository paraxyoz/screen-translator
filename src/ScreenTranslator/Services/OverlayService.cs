using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ScreenTranslator.Models;
using ScreenTranslator.UI;
using ScreenTranslator.Win32;
using WpfBrush = System.Windows.Media.Brush;
using WpfBrushes = System.Windows.Media.Brushes;
using WpfColor = System.Windows.Media.Color;
using WpfFontFamily = System.Windows.Media.FontFamily;
using WpfPoint = System.Windows.Point;
using WpfSize = System.Windows.Size;

namespace ScreenTranslator.Services
{
    public sealed class OverlayService : IDisposable
    {
        private readonly LoggingService _logger;
        private readonly SettingsService _settings;
        private TranslationOverlayWindow _overlayWindow;
        private DispatcherTimer _autoHideTimer;
        private Rectangle _lastTargetPhysicalRect;
        private OcrRecognitionResult _lastOcrResult;
        private TranslationResult _lastTranslationResult;
        private bool _isTranslationVisible = true;
        private bool _showingOriginalBitmap;
        private bool _isMoveMode;
        private bool _affinityApplied;
        private DateTime _lastShownUtc = DateTime.MinValue;

        public event Action RepeatTranslationRequested;
        public event Action SelectNewRegionRequested;
        public event Action OverlayVisibilityChanged;
        public event Action ToolbarSettingChanged;

        public bool HasActiveOverlay => _overlayWindow != null && _overlayWindow.IsLoaded;
        public bool IsOverlayVisible => HasActiveOverlay && _overlayWindow.Visibility == Visibility.Visible && _isTranslationVisible;
        public Rectangle LastTargetPhysicalRect => _lastTargetPhysicalRect;

        public OverlayService(LoggingService logger, SettingsService settings)
        {
            _logger = logger;
            _settings = settings;
        }

        public void HandleGlobalLeftMouseDown(NativeMethods.POINT pt)
        {
            if (!_settings.Current.HideOverlayOnLeftClick) return;
            if (!IsOverlayVisible) return;

            // Ignore clicks that happened immediately when the overlay appeared (e.g. mouse hotkey trigger)
            if ((DateTime.UtcNow - _lastShownUtc).TotalMilliseconds < 240)
            {
                return;
            }

            EnsureOnUiThread(() =>
            {
                if (!IsOverlayVisible || _overlayWindow == null) return;

                // Do not auto-close if user clicked a button on the overlay toolbar or is dragging in move mode
                if (_overlayWindow.IsPhysicalPointOnToolbar(pt.X, pt.Y))
                {
                    return;
                }
                if (_isMoveMode && _overlayWindow.IsPhysicalPointInsideWindow(pt.X, pt.Y))
                {
                    return;
                }

                CloseOverlay();
            });
        }

        public static (WpfColor Bg, WpfColor Fg) ComputeAutoSampleColors(SampledRegionStyle sampled, byte userAlpha)
        {
            if (sampled == null)
            {
                return (WpfColor.FromArgb(userAlpha, 24, 28, 36), WpfColor.FromRgb(245, 248, 255));
            }

            byte r = sampled.BackgroundColor.R;
            byte g = sampled.BackgroundColor.G;
            byte b = sampled.BackgroundColor.B;
            double lum = 0.2126 * r + 0.7152 * g + 0.0722 * b;

            if (sampled.IsComplexBackground)
            {
                // Preserve hue and tone while ensuring contrast for text legibility (never crush to black)
                if (lum >= 115.0 && lum <= 165.0)
                {
                    r = (byte)(r * 0.78);
                    g = (byte)(g * 0.78);
                    b = (byte)(b * 0.78);
                    lum = 0.2126 * r + 0.7152 * g + 0.0722 * b;
                }
                byte complexAlpha = (byte)Math.Max(185, Math.Min(238, (int)Math.Round(userAlpha * 0.90)));
                var fgComplex = lum < 145.0
                    ? WpfColor.FromRgb(245, 248, 255)
                    : WpfColor.FromRgb(15, 23, 42);
                return (WpfColor.FromArgb(complexAlpha, r, g, b), fgComplex);
            }

            byte solidAlpha = (byte)Math.Max(205, Math.Min(246, (int)Math.Round(userAlpha * 0.96)));
            var fg = WpfColor.FromRgb(sampled.ForegroundColor.R, sampled.ForegroundColor.G, sampled.ForegroundColor.B);
            return (WpfColor.FromArgb(solidAlpha, r, g, b), fg);
        }

        public void ShowLoadingState(Rectangle targetPhysicalRect, string statusMessage = "Распознавание и перевод...")
        {
            EnsureOnUiThread(() =>
            {
                StopAutoHideTimer();
                _lastTargetPhysicalRect = targetPhysicalRect;
                EnsureOverlayWindow();
                _overlayWindow.RenderStatusState(targetPhysicalRect, statusMessage, isError: false, showRetry: false);
                _isTranslationVisible = true;
                OverlayVisibilityChanged?.Invoke();
            });
        }

        public void ShowErrorState(Rectangle targetPhysicalRect, string errorMessage, bool allowRetry = true)
        {
            EnsureOnUiThread(() =>
            {
                StopAutoHideTimer();
                _lastTargetPhysicalRect = targetPhysicalRect;
                EnsureOverlayWindow();
                _overlayWindow.RenderStatusState(targetPhysicalRect, errorMessage, isError: true, showRetry: allowRetry);
                _isTranslationVisible = true;
                OverlayVisibilityChanged?.Invoke();
            });
        }

        public void ShowTranslation(OcrRecognitionResult ocr, TranslationResult translation)
        {
            if (ocr == null || translation == null) return;

            EnsureOnUiThread(() =>
            {
                StopAutoHideTimer();
                _lastOcrResult = ocr;
                _lastTranslationResult = translation;
                _lastTargetPhysicalRect = ocr.CapturedScreenRect;
                _isTranslationVisible = true;
                _showingOriginalBitmap = false;

                EnsureOverlayWindow();
                _lastShownUtc = DateTime.UtcNow;
                _overlayWindow.RenderTranslation(ocr, translation, _settings.Current, _showingOriginalBitmap, _isMoveMode);

                if (_settings.Current.AutoHideOverlaySeconds > 0)
                {
                    StartAutoHideTimer(_settings.Current.AutoHideOverlaySeconds);
                }

                OverlayVisibilityChanged?.Invoke();
            });
        }

        public void RefreshCurrentOverlayStyle()
        {
            EnsureOnUiThread(() =>
            {
                if (HasActiveOverlay && _lastOcrResult != null && _lastTranslationResult != null)
                {
                    _overlayWindow.UpdateExcludeFromCapture(_settings.Current.ExcludeOverlayFromCapture, out _affinityApplied);
                    _overlayWindow.RenderTranslation(_lastOcrResult, _lastTranslationResult, _settings.Current, _showingOriginalBitmap, _isMoveMode);
                }
            });
        }

        public void ToggleOverlayVisibility()
        {
            EnsureOnUiThread(() =>
            {
                if (!HasActiveOverlay)
                {
                    if (_lastOcrResult != null && _lastTranslationResult != null)
                    {
                        ShowTranslation(_lastOcrResult, _lastTranslationResult);
                    }
                    return;
                }

                if (_overlayWindow.Visibility == Visibility.Visible)
                {
                    _overlayWindow.Hide();
                    _isTranslationVisible = false;
                    _logger?.Info("OverlayService", "Overlay скрыт пользователем.");
                }
                else
                {
                    _overlayWindow.Show();
                    _overlayWindow.ApplyPhysicalPosition();
                    _isTranslationVisible = true;
                    _logger?.Info("OverlayService", "Overlay повторно показан.");
                }
                OverlayVisibilityChanged?.Invoke();
            });
        }

        public void ToggleOriginalView()
        {
            EnsureOnUiThread(() =>
            {
                if (!HasActiveOverlay || _lastOcrResult == null || _lastTranslationResult == null) return;
                _showingOriginalBitmap = !_showingOriginalBitmap;
                _overlayWindow.RenderTranslation(_lastOcrResult, _lastTranslationResult, _settings.Current, _showingOriginalBitmap, _isMoveMode);
            });
        }

        public void ToggleMoveMode()
        {
            EnsureOnUiThread(() =>
            {
                if (!HasActiveOverlay || _lastOcrResult == null || _lastTranslationResult == null) return;
                _isMoveMode = !_isMoveMode;
                _overlayWindow.RenderTranslation(_lastOcrResult, _lastTranslationResult, _settings.Current, _showingOriginalBitmap, _isMoveMode);
            });
        }

        public void CloseOverlay()
        {
            EnsureOnUiThread(() =>
            {
                StopAutoHideTimer();
                if (_overlayWindow != null)
                {
                    _overlayWindow.Hide();
                    _isTranslationVisible = false;
                    OverlayVisibilityChanged?.Invoke();
                }
            });
        }

        /// <summary>
        /// Защита от повторного захвата собственного окна overlay.
        /// Если WDA_EXCLUDEFROMCAPTURE активен, Windows автоматически исключает окно из BitBlt/CopyFromScreen без мигания.
        /// Если настройка выключена или драйвер не поддержал флаг — временно скрываем окно на время захвата.
        /// </summary>
        public void PrepareForScreenCapture()
        {
            EnsureOnUiThread(() =>
            {
                if (_overlayWindow != null && _overlayWindow.Visibility == Visibility.Visible)
                {
                    if (!_settings.Current.ExcludeOverlayFromCapture || !_affinityApplied)
                    {
                        _overlayWindow.Opacity = 0.0;
                    }
                }
            });
        }

        public void RestoreAfterScreenCapture()
        {
            EnsureOnUiThread(() =>
            {
                if (_overlayWindow != null && _overlayWindow.Visibility == Visibility.Visible)
                {
                    _overlayWindow.Opacity = 1.0;
                }
            });
        }

        public void DisableOverlayToolbar()
        {
            EnsureOnUiThread(() =>
            {
                _settings.Current.ShowOverlayToolbar = false;
                _settings.Save();
                RefreshCurrentOverlayStyle();
                ToolbarSettingChanged?.Invoke();
            });
        }

        private void EnsureOverlayWindow()
        {
            if (_overlayWindow == null || !_overlayWindow.IsLoaded)
            {
                _overlayWindow = new TranslationOverlayWindow(
                    onToggleOriginal: ToggleOriginalView,
                    onRepeat: () => RepeatTranslationRequested?.Invoke(),
                    onNewRegion: () => SelectNewRegionRequested?.Invoke(),
                    onToggleMove: ToggleMoveMode,
                    onDisableToolbar: DisableOverlayToolbar,
                    onClose: CloseOverlay);

                _overlayWindow.Show();
                _overlayWindow.UpdateExcludeFromCapture(_settings.Current.ExcludeOverlayFromCapture, out _affinityApplied);
            }
            else if (_overlayWindow.Visibility != Visibility.Visible)
            {
                _overlayWindow.Show();
            }
        }

        private void StartAutoHideTimer(int seconds)
        {
            StopAutoHideTimer();
            _autoHideTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(Math.Max(2, seconds))
            };
            _autoHideTimer.Tick += (s, e) =>
            {
                StopAutoHideTimer();
                CloseOverlay();
            };
            _autoHideTimer.Start();
        }

        private void StopAutoHideTimer()
        {
            if (_autoHideTimer != null)
            {
                _autoHideTimer.Stop();
                _autoHideTimer = null;
            }
        }

        private static void EnsureOnUiThread(Action action)
        {
            var app = Application.Current;
            if (app == null || app.Dispatcher.CheckAccess())
            {
                action();
            }
            else
            {
                app.Dispatcher.Invoke(action);
            }
        }

        public void Dispose()
        {
            EnsureOnUiThread(() =>
            {
                StopAutoHideTimer();
                if (_overlayWindow != null)
                {
                    try { _overlayWindow.Close(); } catch { }
                    _overlayWindow = null;
                }
            });
        }
    }

    internal sealed class TranslationOverlayWindow : Window
    {
        private const int ToolbarHeightDip = 32;
        private const int WM_MOUSEACTIVATE = 0x0021;
        private const int MA_NOACTIVATE = 3;

        private readonly Action _onToggleOriginal;
        private readonly Action _onRepeat;
        private readonly Action _onNewRegion;
        private readonly Action _onToggleMove;
        private readonly Action _onDisableToolbar;
        private readonly Action _onClose;

        private readonly Canvas _rootCanvas;
        private Rectangle _currentPhysicalWindowRect;
        private Rectangle _currentToolbarPhysicalRect;
        private double _dpiScale = 1.0;
        private bool _isDraggingWindow;
        private bool _currentIsMoveMode;
        private bool _hideOnLeftClick = true;
        private NativeMethods.POINT _dragStartCursor;
        private Rectangle _dragStartWindowRect;

        public TranslationOverlayWindow(
            Action onToggleOriginal,
            Action onRepeat,
            Action onNewRegion,
            Action onToggleMove,
            Action onDisableToolbar,
            Action onClose)
        {
            _onToggleOriginal = onToggleOriginal;
            _onRepeat = onRepeat;
            _onNewRegion = onNewRegion;
            _onToggleMove = onToggleMove;
            _onDisableToolbar = onDisableToolbar;
            _onClose = onClose;

            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            AllowsTransparency = true;
            Background = WpfBrushes.Transparent;
            Topmost = true;
            ShowInTaskbar = false;
            ShowActivated = false;
            Focusable = false;

            _rootCanvas = new Canvas
            {
                Background = WpfBrushes.Transparent,
                SnapsToDevicePixels = true
            };
            Content = _rootCanvas;

            SourceInitialized += OnSourceInitialized;
            MouseLeftButtonDown += OnWindowMouseDown;
            MouseMove += OnWindowMouseMove;
            MouseLeftButtonUp += OnWindowMouseUp;
        }

        private void OnSourceInitialized(object sender, EventArgs e)
        {
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero)
            {
                int exStyle = NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE);
                NativeMethods.SetWindowLong(
                    hwnd,
                    NativeMethods.GWL_EXSTYLE,
                    exStyle | NativeMethods.WS_EX_TOOLWINDOW);

                HwndSource source = HwndSource.FromHwnd(hwnd);
                source?.AddHook(WndProc);
            }
        }

        private static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_MOUSEACTIVATE)
            {
                handled = true;
                return new IntPtr(MA_NOACTIVATE);
            }
            return IntPtr.Zero;
        }

        public bool IsPhysicalPointOnToolbar(int x, int y)
        {
            if (_currentToolbarPhysicalRect.Width <= 0 || _currentToolbarPhysicalRect.Height <= 0)
            {
                return false;
            }
            return _currentToolbarPhysicalRect.Contains(x, y);
        }

        public bool IsPhysicalPointInsideWindow(int x, int y)
        {
            if (_currentPhysicalWindowRect.Width <= 0 || _currentPhysicalWindowRect.Height <= 0)
            {
                return false;
            }
            return _currentPhysicalWindowRect.Contains(x, y);
        }

        public void UpdateExcludeFromCapture(bool exclude, out bool applied)
        {
            applied = false;
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;

            uint affinity = exclude ? NativeMethods.WDA_EXCLUDEFROMCAPTURE : NativeMethods.WDA_NONE;
            applied = NativeMethods.SetWindowDisplayAffinity(hwnd, affinity);
        }

        public void ApplyPhysicalPosition()
        {
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero || _currentPhysicalWindowRect.Width <= 0) return;

            NativeMethods.SetWindowPos(
                hwnd,
                NativeMethods.HWND_TOPMOST,
                _currentPhysicalWindowRect.Left,
                _currentPhysicalWindowRect.Top,
                _currentPhysicalWindowRect.Width,
                _currentPhysicalWindowRect.Height,
                NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
        }

        public void RenderStatusState(Rectangle targetPhysicalRect, string message, bool isError, bool showRetry)
        {
            _dpiScale = NativeMethods.GetDpiScaleForPoint(targetPhysicalRect.Left, targetPhysicalRect.Top);
            if (_dpiScale <= 0) _dpiScale = 1.0;

            int minPhysW = Math.Max(targetPhysicalRect.Width, (int)(280 * _dpiScale));
            int minPhysH = Math.Max(targetPhysicalRect.Height, (int)(54 * _dpiScale));
            _currentPhysicalWindowRect = new Rectangle(targetPhysicalRect.Left, targetPhysicalRect.Top, minPhysW, minPhysH);
            _currentToolbarPhysicalRect = _currentPhysicalWindowRect;
            ApplyPhysicalPosition();

            _rootCanvas.Children.Clear();

            var badgeBorder = new Border
            {
                Background = new SolidColorBrush(WpfColor.FromArgb(242, 20, 24, 33)),
                BorderBrush = isError
                    ? new SolidColorBrush(WpfColor.FromRgb(239, 68, 68))
                    : new SolidColorBrush(WpfColor.FromRgb(59, 130, 246)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(7),
                Padding = new Thickness(12, 8, 12, 8),
                MaxWidth = Math.Max(260, minPhysW / _dpiScale),
                SnapsToDevicePixels = true
            };

            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center
            };

            var statusText = new TextBlock
            {
                Text = message,
                Foreground = new SolidColorBrush(WpfColor.FromRgb(241, 245, 249)),
                FontFamily = new WpfFontFamily("Segoe UI"),
                FontSize = 12.5,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 340,
                VerticalAlignment = VerticalAlignment.Center
            };
            row.Children.Add(statusText);

            if (showRetry)
            {
                var retryBtn = CreateToolbarPillButton("Повторить", VectorIcons.Refresh, false, () => _onRepeat?.Invoke());
                retryBtn.Margin = new Thickness(10, 0, 0, 0);
                row.Children.Add(retryBtn);
            }

            var closeBtn = CreateToolbarIconButton(VectorIcons.Close, "Закрыть (Esc)", () => _onClose?.Invoke());
            closeBtn.Margin = new Thickness(8, 0, 0, 0);
            row.Children.Add(closeBtn);

            badgeBorder.Child = row;
            Canvas.SetLeft(badgeBorder, 0);
            Canvas.SetTop(badgeBorder, 0);
            _rootCanvas.Children.Add(badgeBorder);
        }

        public void RenderTranslation(
            OcrRecognitionResult ocr,
            TranslationResult translation,
            AppSettings settings,
            bool showingOriginalBitmap,
            bool isMoveMode)
        {
            _currentIsMoveMode = isMoveMode;
            _hideOnLeftClick = settings.HideOverlayOnLeftClick;

            Rectangle captureRect = ocr.CapturedScreenRect;
            _dpiScale = NativeMethods.GetDpiScaleForPoint(captureRect.Left, captureRect.Top);
            if (_dpiScale <= 0) _dpiScale = 1.0;

            int toolbarPhysH = settings.ShowOverlayToolbar ? (int)Math.Ceiling((ToolbarHeightDip + 6) * _dpiScale) : 0;
            Rectangle virtualScreen = NativeMethods.GetVirtualScreenBounds();
            bool toolbarOnTop = (captureRect.Top - toolbarPhysH) >= virtualScreen.Top;

            int winPhysX = captureRect.Left;
            int winPhysY = toolbarOnTop ? captureRect.Top - toolbarPhysH : captureRect.Top;
            int minToolbarPhysW = settings.ShowOverlayToolbar ? (int)Math.Ceiling(415 * _dpiScale) : captureRect.Width;
            int winPhysW = Math.Max(captureRect.Width, minToolbarPhysW);
            int winPhysH = captureRect.Height + toolbarPhysH;

            _currentPhysicalWindowRect = new Rectangle(winPhysX, winPhysY, winPhysW, winPhysH);
            if (settings.ShowOverlayToolbar)
            {
                int tbPhysY = toolbarOnTop ? winPhysY : (winPhysY + captureRect.Height);
                _currentToolbarPhysicalRect = new Rectangle(winPhysX, tbPhysY, winPhysW, toolbarPhysH + (int)Math.Ceiling(6 * _dpiScale));
            }
            else
            {
                _currentToolbarPhysicalRect = Rectangle.Empty;
            }
            ApplyPhysicalPosition();

            _rootCanvas.Children.Clear();
            double contentOffsetTopDip = toolbarOnTop ? (toolbarPhysH / _dpiScale) : 0;
            double captureDipW = captureRect.Width / _dpiScale;
            double captureDipH = captureRect.Height / _dpiScale;

            // 1. Content area container positioned exactly over the original captured region
            var contentCanvas = new Canvas
            {
                Width = captureDipW,
                Height = captureDipH,
                SnapsToDevicePixels = true,
                Cursor = isMoveMode ? Cursors.SizeAll : Cursors.Arrow
            };
            Canvas.SetLeft(contentCanvas, 0);
            Canvas.SetTop(contentCanvas, contentOffsetTopDip);

            if (showingOriginalBitmap && ocr.OriginalCapture != null)
            {
                var origImage = new System.Windows.Controls.Image
                {
                    Source = ConvertBitmapToImageSource(ocr.OriginalCapture),
                    Width = captureDipW,
                    Height = captureDipH,
                    Stretch = Stretch.Fill
                };
                var origBorder = new Border
                {
                    Width = captureDipW,
                    Height = captureDipH,
                    BorderBrush = new SolidColorBrush(WpfColor.FromArgb(180, 59, 130, 246)),
                    BorderThickness = new Thickness(1),
                    Child = origImage
                };
                contentCanvas.Children.Add(origBorder);
            }
            else
            {
                RenderTranslatedLinesInPlace(contentCanvas, ocr, translation, settings, _dpiScale, isMoveMode);
            }

            _rootCanvas.Children.Add(contentCanvas);

            // 2. Micro-toolbar for Mode C (Show/Hide Original, Repeat, New Region, Copy, Move, Close)
            if (settings.ShowOverlayToolbar)
            {
                var toolbar = BuildOverlayToolbar(translation.TranslatedText, showingOriginalBitmap, isMoveMode);
                double toolbarTopDip = toolbarOnTop ? 0 : (captureDipH + 4);
                Canvas.SetLeft(toolbar, 0);
                Canvas.SetTop(toolbar, toolbarTopDip);
                _rootCanvas.Children.Add(toolbar);
            }
        }

        private void RenderTranslatedLinesInPlace(
            Canvas contentCanvas,
            OcrRecognitionResult ocr,
            TranslationResult translation,
            AppSettings settings,
            double dpiScale,
            bool isMoveMode)
        {
            string fontFamilyName = string.IsNullOrWhiteSpace(settings.OverlayFontFamily) ? "Segoe UI" : settings.OverlayFontFamily;
            var fontFamily = new WpfFontFamily(fontFamilyName);
            byte userAlpha = (byte)Math.Max(65, Math.Min(255, (int)Math.Round(settings.OverlayOpacity * 255)));

            // Check if lines form a tight multi-line paragraph or independent UI labels
            bool renderAsUnifiedParagraph = ShouldRenderAsUnifiedParagraph(ocr.Lines);

            if (renderAsUnifiedParagraph && ocr.Lines.Count > 1)
            {
                int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
                foreach (var l in ocr.Lines)
                {
                    if (l.BoundingBox.Left < minX) minX = l.BoundingBox.Left;
                    if (l.BoundingBox.Top < minY) minY = l.BoundingBox.Top;
                    if (l.BoundingBox.Right > maxX) maxX = l.BoundingBox.Right;
                    if (l.BoundingBox.Bottom > maxY) maxY = l.BoundingBox.Bottom;
                }

                var blockRectPhys = Rectangle.Inflate(
                    new Rectangle(minX, minY, Math.Max(16, maxX - minX), Math.Max(16, maxY - minY)),
                    4, 3);
                blockRectPhys = Rectangle.Intersect(blockRectPhys, new Rectangle(0, 0, ocr.CapturedScreenRect.Width, ocr.CapturedScreenRect.Height));

                double dipX = blockRectPhys.Left / dpiScale;
                double dipY = blockRectPhys.Top / dpiScale;
                double dipW = Math.Max(36, blockRectPhys.Width / dpiScale);
                double dipH = Math.Max(20, blockRectPhys.Height / dpiScale);

                var (bgBrush, fgBrush, borderBrush) = ResolveOverlayColors(ocr.OverallStyle, settings.OverlayBgMode, userAlpha, isMoveMode);

                double avgLineHeightDip = (ocr.Lines[0].BoundingBox.Height / dpiScale);
                double fontSize = settings.AutoFitFontSize
                    ? CalculateFittedBlockFontSize(translation.TranslatedText, fontFamily, dipW - 8, dipH - 4, avgLineHeightDip, dpiScale)
                    : settings.FixedFontSize;

                var blockBorder = new Border
                {
                    Width = dipW,
                    MinHeight = dipH,
                    Background = bgBrush,
                    BorderBrush = borderBrush,
                    BorderThickness = new Thickness(borderBrush != WpfBrushes.Transparent ? 1 : 0),
                    CornerRadius = new CornerRadius(3),
                    Padding = new Thickness(4, 2, 4, 2),
                    SnapsToDevicePixels = true
                };

                var tb = new TextBlock
                {
                    Text = translation.TranslatedText,
                    FontFamily = fontFamily,
                    FontSize = fontSize,
                    Foreground = fgBrush,
                    TextWrapping = TextWrapping.Wrap,
                    LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                    LineHeight = Math.Max(fontSize * 1.18, Math.Min(fontSize * 1.42, dipH / Math.Max(1, ocr.Lines.Count)))
                };

                blockBorder.Child = tb;
                Canvas.SetLeft(blockBorder, dipX);
                Canvas.SetTop(blockBorder, dipY);
                contentCanvas.Children.Add(blockBorder);
                return;
            }

            // Line-by-line exact in-place visual replacement
            for (int i = 0; i < ocr.Lines.Count; i++)
            {
                var line = ocr.Lines[i];
                string translatedLine = !string.IsNullOrWhiteSpace(line.TranslatedText)
                    ? line.TranslatedText
                    : (i == 0 ? translation.TranslatedText : string.Empty);

                if (string.IsNullOrWhiteSpace(translatedLine)) continue;

                Rectangle paddedPhys = Rectangle.Inflate(line.BoundingBox, 3, 2);
                paddedPhys = Rectangle.Intersect(paddedPhys, new Rectangle(0, 0, ocr.CapturedScreenRect.Width, ocr.CapturedScreenRect.Height));

                double dipX = paddedPhys.Left / dpiScale;
                double dipY = paddedPhys.Top / dpiScale;
                double dipW = Math.Max(24, paddedPhys.Width / dpiScale);
                double dipH = Math.Max(16, paddedPhys.Height / dpiScale);

                // Bound available horizontal width by any neighbor on the same row to prevent horizontal overlap
                double availableRightWidth = (ocr.CapturedScreenRect.Width / dpiScale) - dipX;
                double maxAllowedHeightDip = (ocr.CapturedScreenRect.Height / dpiScale) - dipY;

                for (int j = 0; j < ocr.Lines.Count; j++)
                {
                    if (j == i) continue;
                    var other = ocr.Lines[j].BoundingBox;

                    int vOverlap = Math.Min(paddedPhys.Bottom, other.Bottom) - Math.Max(paddedPhys.Top, other.Top);
                    if (vOverlap > 4 && other.Left > paddedPhys.Left)
                    {
                        double gapToRightNeighbor = (other.Left / dpiScale) - dipX - 4;
                        if (gapToRightNeighbor >= dipW && gapToRightNeighbor < availableRightWidth)
                        {
                            availableRightWidth = gapToRightNeighbor;
                        }
                    }

                    int hOverlap = Math.Min(paddedPhys.Right, other.Right) - Math.Max(paddedPhys.Left, other.Left);
                    if (hOverlap > 8 && other.Top >= paddedPhys.Bottom - 4)
                    {
                        double gapToLowerNeighbor = (other.Top / dpiScale) - dipY - 1;
                        if (gapToLowerNeighbor >= dipH && gapToLowerNeighbor < maxAllowedHeightDip)
                        {
                            maxAllowedHeightDip = gapToLowerNeighbor;
                        }
                    }
                }

                var (bgBrush, fgBrush, borderBrush) = ResolveOverlayColors(line.Style, settings.OverlayBgMode, userAlpha, isMoveMode);

                double maxTargetWidthDip = Math.Max(dipW, Math.Min(dipW * 1.35, availableRightWidth));
                double fontSize = settings.AutoFitFontSize
                    ? CalculateFittedLineFontSize(translatedLine, fontFamily, maxTargetWidthDip - 6, dipH - 2, dpiScale)
                    : settings.FixedFontSize;

                double measuredWidth = MeasureSingleLineTextWidth(translatedLine, fontFamily, fontSize, dpiScale) + 8;
                double finalWidth = Math.Max(dipW, Math.Min(measuredWidth, availableRightWidth));

                var lineBorder = new Border
                {
                    MinWidth = dipW,
                    Width = finalWidth,
                    MinHeight = dipH,
                    MaxHeight = Math.Max(dipH + 6, maxAllowedHeightDip),
                    Background = bgBrush,
                    BorderBrush = borderBrush,
                    BorderThickness = new Thickness(borderBrush != WpfBrushes.Transparent ? 1 : 0),
                    CornerRadius = new CornerRadius(2.5),
                    Padding = new Thickness(3, 1, 3, 1),
                    SnapsToDevicePixels = true
                };

                var tb = new TextBlock
                {
                    Text = translatedLine,
                    FontFamily = fontFamily,
                    FontSize = fontSize,
                    Foreground = fgBrush,
                    TextWrapping = TextWrapping.Wrap,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = VerticalAlignment.Center
                };

                lineBorder.Child = tb;
                Canvas.SetLeft(lineBorder, dipX);
                Canvas.SetTop(lineBorder, dipY);
                contentCanvas.Children.Add(lineBorder);
            }
        }

        private static bool ShouldRenderAsUnifiedParagraph(IReadOnlyList<RecognizedTextLine> lines)
        {
            if (lines == null || lines.Count <= 1) return false;
            for (int i = 1; i < lines.Count; i++)
            {
                var prev = lines[i - 1].BoundingBox;
                var curr = lines[i].BoundingBox;
                int avgH = Math.Max(12, (prev.Height + curr.Height) / 2);
                int verticalGap = curr.Top - prev.Bottom;

                // If two boxes sit on the same horizontal row, they are side-by-side columns, not a vertical paragraph
                if (verticalGap < -avgH * 0.35)
                {
                    return false;
                }

                int leftDiff = Math.Abs(curr.Left - prev.Left);
                int hOverlap = Math.Min(prev.Right, curr.Right) - Math.Max(prev.Left, curr.Left);

                // If lines are far apart vertically or don't overlap horizontally, render line-by-line
                if (verticalGap > avgH * 1.25 || leftDiff > avgH * 5 || hOverlap <= 0)
                {
                    return false;
                }
            }
            return true;
        }

        private static (WpfBrush Bg, WpfBrush Fg, WpfBrush Border) ResolveOverlayColors(
            SampledRegionStyle sampled,
            OverlayBackgroundMode mode,
            byte userAlpha,
            bool isMoveMode)
        {
            WpfBrush borderBrush = isMoveMode
                ? new SolidColorBrush(WpfColor.FromRgb(59, 130, 246))
                : WpfBrushes.Transparent;

            switch (mode)
            {
                case OverlayBackgroundMode.DarkTranslucent:
                    return (
                        new SolidColorBrush(WpfColor.FromArgb(userAlpha, 18, 22, 30)),
                        new SolidColorBrush(WpfColor.FromRgb(241, 245, 249)),
                        borderBrush != WpfBrushes.Transparent ? borderBrush : new SolidColorBrush(WpfColor.FromArgb(90, 51, 65, 85)));

                case OverlayBackgroundMode.LightTranslucent:
                    return (
                        new SolidColorBrush(WpfColor.FromArgb(userAlpha, 248, 250, 252)),
                        new SolidColorBrush(WpfColor.FromRgb(15, 23, 42)),
                        borderBrush != WpfBrushes.Transparent ? borderBrush : new SolidColorBrush(WpfColor.FromArgb(110, 203, 213, 225)));

                case OverlayBackgroundMode.SolidDark:
                    return (
                        new SolidColorBrush(WpfColor.FromRgb(17, 20, 27)),
                        new SolidColorBrush(WpfColor.FromRgb(241, 245, 249)),
                        borderBrush);

                case OverlayBackgroundMode.AutoSample:
                default:
                    var (bgColor, fgColor) = OverlayService.ComputeAutoSampleColors(sampled, userAlpha);
                    WpfBrush autoBorder = borderBrush;
                    if (autoBorder == WpfBrushes.Transparent && sampled != null && sampled.IsComplexBackground)
                    {
                        autoBorder = new SolidColorBrush(WpfColor.FromArgb(85, bgColor.R, bgColor.G, bgColor.B));
                    }
                    return (new SolidColorBrush(bgColor), new SolidColorBrush(fgColor), autoBorder);
            }
        }

        private static double CalculateFittedLineFontSize(string text, WpfFontFamily fontFamily, double targetWidthDip, double targetHeightDip, double dpiScale)
        {
            double initialSize = Math.Max(9.5, Math.Min(25.0, targetHeightDip * 0.72));
            for (double size = initialSize; size >= 8.5; size -= 0.5)
            {
                double w = MeasureSingleLineTextWidth(text, fontFamily, size, dpiScale);
                if (w <= Math.Max(24.0, targetWidthDip * 1.04))
                {
                    return size;
                }
            }
            return 8.5;
        }

        private static double CalculateFittedBlockFontSize(string text, WpfFontFamily fontFamily, double boxWidthDip, double boxHeightDip, double avgLineHeightDip, double dpiScale)
        {
            double maxFont = Math.Max(10.5, Math.Min(24.0, avgLineHeightDip * 0.74));
            var typeface = new Typeface(fontFamily, System.Windows.FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

            for (double size = maxFont; size >= 9.5; size -= 0.5)
            {
                var ft = new FormattedText(
                    text,
                    CultureInfo.CurrentUICulture,
                    FlowDirection.LeftToRight,
                    typeface,
                    size,
                    WpfBrushes.White,
                    dpiScale)
                {
                    MaxTextWidth = Math.Max(40, boxWidthDip)
                };

                if (ft.Height <= boxHeightDip * 1.12)
                {
                    return size;
                }
            }
            return 9.5;
        }

        private static double MeasureSingleLineTextWidth(string text, WpfFontFamily fontFamily, double fontSize, double dpiScale)
        {
            var typeface = new Typeface(fontFamily, System.Windows.FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
            var ft = new FormattedText(
                text ?? string.Empty,
                CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,
                typeface,
                fontSize,
                WpfBrushes.White,
                dpiScale);
            return ft.WidthIncludingTrailingWhitespace;
        }

        private Border BuildOverlayToolbar(string translatedText, bool showingOriginal, bool isMoveMode)
        {
            var bar = new Border
            {
                Background = new SolidColorBrush(WpfColor.FromArgb(242, 17, 20, 27)),
                BorderBrush = new SolidColorBrush(WpfColor.FromRgb(42, 49, 64)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(4, 3, 4, 3),
                SnapsToDevicePixels = true
            };

            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center
            };

            row.Children.Add(CreateToolbarPillButton(
                showingOriginal ? "Перевод" : "Оригинал",
                showingOriginal ? VectorIcons.EyeShow : VectorIcons.EyeHide,
                showingOriginal,
                () => _onToggleOriginal?.Invoke()));

            row.Children.Add(CreateToolbarPillButton(
                "Повторить",
                VectorIcons.Refresh,
                false,
                () => _onRepeat?.Invoke()));

            row.Children.Add(CreateToolbarPillButton(
                "Область",
                VectorIcons.RegionSelect,
                false,
                () => _onNewRegion?.Invoke()));

            var copyLabel = new TextBlock
            {
                Text = "Копировать",
                Foreground = new SolidColorBrush(WpfColor.FromRgb(226, 232, 240)),
                FontFamily = new WpfFontFamily("Segoe UI"),
                FontSize = 11.5,
                VerticalAlignment = VerticalAlignment.Center
            };
            var copyBtn = CreateToolbarPillButtonWithRef(copyLabel, VectorIcons.Copy, () =>
            {
                try
                {
                    if (!string.IsNullOrEmpty(translatedText))
                    {
                        Clipboard.SetText(translatedText);
                        copyLabel.Text = "Скопировано";
                    }
                }
                catch { }
            });
            row.Children.Add(copyBtn);

            row.Children.Add(CreateToolbarPillButton(
                "Без панели",
                VectorIcons.EyeHide,
                false,
                () => _onDisableToolbar?.Invoke()));

            row.Children.Add(CreateToolbarIconButton(
                VectorIcons.MoveDrag,
                isMoveMode ? "Зафиксировать положение" : "Переместить overlay",
                () => _onToggleMove?.Invoke(),
                isMoveMode));

            row.Children.Add(CreateToolbarIconButton(
                VectorIcons.Close,
                "Скрыть / Закрыть (Esc)",
                () => _onClose?.Invoke()));

            bar.Child = row;
            return bar;
        }

        private static Border CreateToolbarPillButton(string text, string iconData, bool active, Action onClick)
        {
            var tb = new TextBlock
            {
                Text = text,
                Foreground = new SolidColorBrush(active ? WpfColor.FromRgb(255, 255, 255) : WpfColor.FromRgb(226, 232, 240)),
                FontFamily = new WpfFontFamily("Segoe UI"),
                FontSize = 11.5,
                VerticalAlignment = VerticalAlignment.Center
            };
            return CreateToolbarPillButtonWithRef(tb, iconData, onClick, active);
        }

        private static Border CreateToolbarPillButtonWithRef(TextBlock labelBlock, string iconData, Action onClick, bool active = false)
        {
            var border = new Border
            {
                Background = active
                    ? new SolidColorBrush(WpfColor.FromRgb(59, 130, 246))
                    : WpfBrushes.Transparent,
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(7, 3, 7, 3),
                Margin = new Thickness(1, 0, 1, 0),
                Cursor = Cursors.Hand
            };

            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            var icon = VectorIcons.CreateColoredIcon(iconData, 12.5, new SolidColorBrush(WpfColor.FromRgb(226, 232, 240)));
            icon.Margin = new Thickness(0, 0, 5, 0);
            sp.Children.Add(icon);
            sp.Children.Add(labelBlock);
            border.Child = sp;

            border.MouseEnter += (s, e) =>
            {
                if (!active) border.Background = new SolidColorBrush(WpfColor.FromRgb(34, 41, 54));
            };
            border.MouseLeave += (s, e) =>
            {
                if (!active) border.Background = WpfBrushes.Transparent;
            };
            border.MouseLeftButtonDown += (s, e) =>
            {
                e.Handled = true;
                onClick?.Invoke();
            };
            border.MouseLeftButtonUp += (s, e) =>
            {
                e.Handled = true;
            };

            return border;
        }

        private static Border CreateToolbarIconButton(string iconData, string tooltip, Action onClick, bool active = false)
        {
            var border = new Border
            {
                Width = 24,
                Height = 24,
                CornerRadius = new CornerRadius(4),
                Background = active
                    ? new SolidColorBrush(WpfColor.FromRgb(59, 130, 246))
                    : WpfBrushes.Transparent,
                Margin = new Thickness(2, 0, 1, 0),
                ToolTip = tooltip,
                Cursor = Cursors.Hand
            };

            var icon = VectorIcons.CreateColoredIcon(iconData, 12.5, new SolidColorBrush(WpfColor.FromRgb(226, 232, 240)));
            icon.HorizontalAlignment = HorizontalAlignment.Center;
            icon.VerticalAlignment = VerticalAlignment.Center;
            border.Child = icon;

            border.MouseEnter += (s, e) =>
            {
                if (!active) border.Background = new SolidColorBrush(WpfColor.FromRgb(34, 41, 54));
            };
            border.MouseLeave += (s, e) =>
            {
                if (!active) border.Background = WpfBrushes.Transparent;
            };
            border.MouseLeftButtonDown += (s, e) =>
            {
                e.Handled = true;
                onClick?.Invoke();
            };
            border.MouseLeftButtonUp += (s, e) =>
            {
                e.Handled = true;
            };

            return border;
        }

        private void OnWindowMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (_currentIsMoveMode)
            {
                if (!NativeMethods.GetCursorPos(out _dragStartCursor)) return;
                _dragStartWindowRect = _currentPhysicalWindowRect;
                _isDraggingWindow = true;
                CaptureMouse();
                return;
            }

            if (_hideOnLeftClick)
            {
                _onClose?.Invoke();
            }
        }

        private void OnWindowMouseMove(object sender, MouseEventArgs e)
        {
            if (!_isDraggingWindow) return;
            if (!NativeMethods.GetCursorPos(out var cur)) return;

            int dx = cur.X - _dragStartCursor.X;
            int dy = cur.Y - _dragStartCursor.Y;
            _currentPhysicalWindowRect = new Rectangle(
                _dragStartWindowRect.Left + dx,
                _dragStartWindowRect.Top + dy,
                _dragStartWindowRect.Width,
                _dragStartWindowRect.Height);
            ApplyPhysicalPosition();
        }

        private void OnWindowMouseUp(object sender, MouseButtonEventArgs e)
        {
            if (_isDraggingWindow)
            {
                _isDraggingWindow = false;
                ReleaseMouseCapture();
            }
        }

        private static BitmapImage ConvertBitmapToImageSource(Bitmap bitmap)
        {
            using var ms = new MemoryStream();
            bitmap.Save(ms, ImageFormat.Png);
            ms.Position = 0;
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.StreamSource = ms;
            bi.EndInit();
            bi.Freeze();
            return bi;
        }
    }
}
