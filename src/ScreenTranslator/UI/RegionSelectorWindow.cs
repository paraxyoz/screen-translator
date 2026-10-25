using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using ScreenTranslator.Win32;

namespace ScreenTranslator.UI
{
    public sealed class RegionSelectorWindow : Window
    {
        private readonly Canvas _canvas;
        private readonly Path _dimOverlayPath;
        private readonly RectangleGeometry _fullScreenGeo;
        private readonly RectangleGeometry _cutoutGeo;
        private readonly Border _selectionBorder;
        private readonly Border _hudBadge;
        private readonly TextBlock _hudText;

        private bool _isDragging;
        private NativeMethods.POINT _startPhysicalPt;
        private NativeMethods.POINT _currentPhysicalPt;
        private System.Drawing.Rectangle _virtualBounds;
        private double _dpiScale = 1.0;

        public System.Drawing.Rectangle? SelectedPhysicalRegion { get; private set; }

        public RegionSelectorWindow()
        {
            _virtualBounds = NativeMethods.GetVirtualScreenBounds();

            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            Topmost = true;
            ShowInTaskbar = false;
            Cursor = Cursors.Cross;

            Left = _virtualBounds.Left;
            Top = _virtualBounds.Top;
            Width = _virtualBounds.Width;
            Height = _virtualBounds.Height;

            _canvas = new Canvas
            {
                Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)), // Hit-testable across whole screen
                ClipToBounds = true
            };

            _fullScreenGeo = new RectangleGeometry(new Rect(0, 0, _virtualBounds.Width, _virtualBounds.Height));
            _cutoutGeo = new RectangleGeometry(new Rect(0, 0, 0, 0));

            var combined = new CombinedGeometry(GeometryCombineMode.Exclude, _fullScreenGeo, _cutoutGeo);
            _dimOverlayPath = new Path
            {
                Data = combined,
                Fill = new SolidColorBrush(Color.FromArgb(115, 10, 13, 19)),
                IsHitTestVisible = false
            };
            _canvas.Children.Add(_dimOverlayPath);

            _selectionBorder = new Border
            {
                BorderBrush = new SolidColorBrush(Color.FromRgb(59, 130, 246)),
                BorderThickness = new Thickness(1.5),
                Background = Brushes.Transparent,
                Visibility = Visibility.Collapsed,
                IsHitTestVisible = false,
                SnapsToDevicePixels = true
            };
            _canvas.Children.Add(_selectionBorder);

            _hudText = new TextBlock
            {
                Text = "Выделите область с текстом мышью  |  Esc — отмена",
                Foreground = new SolidColorBrush(Color.FromRgb(241, 245, 249)),
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 12,
                FontWeight = FontWeights.Medium
            };

            _hudBadge = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(235, 19, 23, 31)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(45, 55, 72)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(10, 5, 10, 5),
                Child = _hudText,
                IsHitTestVisible = false,
                SnapsToDevicePixels = true
            };
            Canvas.SetLeft(_hudBadge, 24);
            Canvas.SetTop(_hudBadge, 24);
            _canvas.Children.Add(_hudBadge);

            Content = _canvas;

            SourceInitialized += OnSourceInitialized;
            KeyDown += OnWindowKeyDown;
            MouseLeftButtonDown += OnCanvasMouseDown;
            MouseMove += OnCanvasMouseMove;
            MouseLeftButtonUp += OnCanvasMouseUp;
            MouseRightButtonUp += (s, e) => CancelSelection();
        }

        private void OnSourceInitialized(object sender, EventArgs e)
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero)
            {
                NativeMethods.SetWindowDisplayAffinity(hwnd, NativeMethods.WDA_EXCLUDEFROMCAPTURE);
                NativeMethods.SetWindowPos(
                    hwnd,
                    NativeMethods.HWND_TOPMOST,
                    _virtualBounds.Left,
                    _virtualBounds.Top,
                    _virtualBounds.Width,
                    _virtualBounds.Height,
                    NativeMethods.SWP_SHOWWINDOW);
            }

            var source = PresentationSource.FromVisual(this);
            if (source?.CompositionTarget != null)
            {
                _dpiScale = source.CompositionTarget.TransformToDevice.M11;
                if (_dpiScale <= 0) _dpiScale = 1.0;
            }

            _fullScreenGeo.Rect = new Rect(0, 0, _virtualBounds.Width / _dpiScale, _virtualBounds.Height / _dpiScale);

            // Position initial HUD near cursor
            if (NativeMethods.GetCursorPos(out var cur))
            {
                double cx = (cur.X - _virtualBounds.Left) / _dpiScale;
                double cy = (cur.Y - _virtualBounds.Top) / _dpiScale;
                Canvas.SetLeft(_hudBadge, Math.Max(12, cx + 16));
                Canvas.SetTop(_hudBadge, Math.Max(12, cy + 16));
            }

            Activate();
            Focus();
        }

        private void OnWindowKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                CancelSelection();
            }
        }

        private void OnCanvasMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (!NativeMethods.GetCursorPos(out _startPhysicalPt)) return;
            _currentPhysicalPt = _startPhysicalPt;
            _isDragging = true;
            CaptureMouse();
            _selectionBorder.Visibility = Visibility.Visible;
            UpdateSelectionVisual();
        }

        private void OnCanvasMouseMove(object sender, MouseEventArgs e)
        {
            if (!NativeMethods.GetCursorPos(out _currentPhysicalPt)) return;

            if (!_isDragging)
            {
                double cx = (_currentPhysicalPt.X - _virtualBounds.Left) / _dpiScale;
                double cy = (_currentPhysicalPt.Y - _virtualBounds.Top) / _dpiScale;
                Canvas.SetLeft(_hudBadge, cx + 16);
                Canvas.SetTop(_hudBadge, cy + 16);
                return;
            }

            UpdateSelectionVisual();
        }

        private void OnCanvasMouseUp(object sender, MouseButtonEventArgs e)
        {
            if (!_isDragging) return;
            _isDragging = false;
            ReleaseMouseCapture();
            NativeMethods.GetCursorPos(out _currentPhysicalPt);

            int left = Math.Min(_startPhysicalPt.X, _currentPhysicalPt.X);
            int top = Math.Min(_startPhysicalPt.Y, _currentPhysicalPt.Y);
            int width = Math.Abs(_currentPhysicalPt.X - _startPhysicalPt.X);
            int height = Math.Abs(_currentPhysicalPt.Y - _startPhysicalPt.Y);

            if (width < 12 || height < 10)
            {
                CancelSelection();
                return;
            }

            SelectedPhysicalRegion = new System.Drawing.Rectangle(left, top, width, height);
            DialogResult = true;
            Close();
        }

        private void UpdateSelectionVisual()
        {
            int physLeft = Math.Min(_startPhysicalPt.X, _currentPhysicalPt.X);
            int physTop = Math.Min(_startPhysicalPt.Y, _currentPhysicalPt.Y);
            int physW = Math.Abs(_currentPhysicalPt.X - _startPhysicalPt.X);
            int physH = Math.Abs(_currentPhysicalPt.Y - _startPhysicalPt.Y);

            double dipLeft = (physLeft - _virtualBounds.Left) / _dpiScale;
            double dipTop = (physTop - _virtualBounds.Top) / _dpiScale;
            double dipW = Math.Max(1, physW / _dpiScale);
            double dipH = Math.Max(1, physH / _dpiScale);

            _cutoutGeo.Rect = new Rect(dipLeft, dipTop, dipW, dipH);

            Canvas.SetLeft(_selectionBorder, dipLeft);
            Canvas.SetTop(_selectionBorder, dipTop);
            _selectionBorder.Width = dipW;
            _selectionBorder.Height = dipH;

            _hudText.Text = $"{physW} × {physH} пикс.  |  Отпустите для перевода  |  Esc — отмена";
            double badgeTop = dipTop >= 38 ? dipTop - 34 : dipTop + dipH + 8;
            Canvas.SetLeft(_hudBadge, Math.Max(8, dipLeft));
            Canvas.SetTop(_hudBadge, Math.Max(8, badgeTop));
        }

        public void CancelSelection()
        {
            if (_isDragging)
            {
                _isDragging = false;
                ReleaseMouseCapture();
            }
            SelectedPhysicalRegion = null;
            try
            {
                DialogResult = false;
            }
            catch
            {
                // Window might not be shown as dialog
            }
            Close();
        }
    }
}
