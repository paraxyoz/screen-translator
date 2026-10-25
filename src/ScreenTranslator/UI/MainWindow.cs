using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using ScreenTranslator.Models;
using ScreenTranslator.Services;
using ScreenTranslator.Win32;
using WpfPath = System.Windows.Shapes.Path;

namespace ScreenTranslator.UI
{
    public sealed class MainWindow : Window
    {
        private readonly LoggingService _logger;
        private readonly SettingsService _settings;
        private readonly TranslationCache _cache;
        private readonly TextRecognitionService _ocr;
        private readonly TranslationService _translator;
        private readonly OverlayService _overlay;
        private readonly HotkeyService _hotkeys;
        private readonly TranslationCoordinator _coordinator;

        private readonly List<(string Id, string Title, string Icon, Border NavButton, WpfPath NavIcon, TextBlock NavText, Border ActiveBar)> _navItems
            = new List<(string, string, string, Border, WpfPath, TextBlock, Border)>();

        private string _activePageId = "home";
        private ScrollViewer _contentScroller;
        private Border _notificationBanner;
        private TextBlock _notificationText;
        private Border _headerStatusBadge;
        private TextBlock _headerStatusText;
        private WpfPath _themeToggleIcon;
        private bool _allowRealExit;

        private static readonly IReadOnlyList<(string Value, string Label)> SourceLanguages = new List<(string, string)>
        {
            ("auto", "Автоопределение"),
            ("en", "Английский (English)"),
            ("ru", "Русский"),
            ("de", "Немецкий (Deutsch)"),
            ("fr", "Французский (Français)"),
            ("es", "Испанский (Español)"),
            ("zh-CN", "Китайский (中文)"),
            ("ja", "Японский (日本語)"),
            ("ko", "Корейский (한국어)"),
            ("uk", "Украинский (Українська)"),
            ("tr", "Турецкий (Türkçe)")
        };

        private static readonly IReadOnlyList<(string Value, string Label)> TargetLanguages = new List<(string, string)>
        {
            ("ru", "Русский"),
            ("en", "Английский (English)"),
            ("de", "Немецкий (Deutsch)"),
            ("fr", "Французский (Français)"),
            ("es", "Испанский (Español)"),
            ("zh-CN", "Китайский (中文)"),
            ("ja", "Японский (日本語)"),
            ("ko", "Корейский (한국어)"),
            ("uk", "Украинский (Українська)"),
            ("tr", "Турецкий (Türkçe)")
        };

        private static readonly IReadOnlyList<(string Value, string Label)> Providers = new List<(string, string)>
        {
            (TranslationProviderType.GoogleGtx.ToString(), "Google Translate (GTX — бесплатно, без ключа)"),
            (TranslationProviderType.MyMemory.ToString(), "MyMemory Translated API"),
            (TranslationProviderType.DeepL.ToString(), "DeepL API (Free / Pro — нужен API-ключ)"),
            (TranslationProviderType.OpenAiCompatible.ToString(), "OpenAI / Совместимый LLM (GPT / Ollama)")
        };

        private static readonly IReadOnlyList<(string Value, string Label)> OverlayBgModes = new List<(string, string)>
        {
            (OverlayBackgroundMode.AutoSample.ToString(), "Автоподбор цвета фона по изображению"),
            (OverlayBackgroundMode.DarkTranslucent.ToString(), "Полупрозрачная тёмная подложка"),
            (OverlayBackgroundMode.LightTranslucent.ToString(), "Полупрозрачная светлая подложка"),
            (OverlayBackgroundMode.SolidDark.ToString(), "Сплошная тёмная маскировка")
        };

        private static readonly IReadOnlyList<(string Value, string Label)> FontFamilies = new List<(string, string)>
        {
            ("Segoe UI", "Segoe UI (Системный)"),
            ("Calibri", "Calibri"),
            ("Arial", "Arial"),
            ("Verdana", "Verdana"),
            ("Georgia", "Georgia"),
            ("Consolas", "Consolas (Моноширинный)")
        };

        public MainWindow(
            LoggingService logger,
            SettingsService settings,
            TranslationCache cache,
            TextRecognitionService ocr,
            TranslationService translator,
            OverlayService overlay,
            HotkeyService hotkeys,
            TranslationCoordinator coordinator)
        {
            _logger = logger;
            _settings = settings;
            _cache = cache;
            _ocr = ocr;
            _translator = translator;
            _overlay = overlay;
            _hotkeys = hotkeys;
            _coordinator = coordinator;

            Title = "Screen Translator";
            Width = 920;
            Height = 680;
            MinWidth = 800;
            MinHeight = 540;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.CanResize;
            FontFamily = new FontFamily("Segoe UI");

            WindowChrome.SetWindowChrome(this, new WindowChrome
            {
                CaptionHeight = 0,
                ResizeBorderThickness = new Thickness(6),
                GlassFrameThickness = new Thickness(1),
                CornerRadius = new CornerRadius(0),
                UseAeroCaptionButtons = false
            });

            BuildWindowLayout();

            SourceInitialized += OnWindowSourceInitialized;
            Closing += OnWindowClosing;

            _coordinator.StateChanged += () => Dispatcher.BeginInvoke(new Action(OnCoordinatorStateChanged));
            _coordinator.NotificationMessage += (msg, isErr) => Dispatcher.BeginInvoke(new Action(() => ShowBanner(msg, isErr)));
            _hotkeys.StateChanged += () => Dispatcher.BeginInvoke(new Action(UpdateHeaderStatus));
        }

        private void OnWindowSourceInitialized(object sender, EventArgs e)
        {
            try
            {
                IntPtr hwnd = new WindowInteropHelper(this).Handle;
                if (hwnd != IntPtr.Zero)
                {
                    // Enable Windows 11 native rounded window corners (DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWCP_ROUND = 2)
                    int cornerPref = 2;
                    NativeMethods.DwmSetWindowAttribute(hwnd, 33, ref cornerPref, sizeof(int));
                }
            }
            catch
            {
                // Ignore on older Windows 10 builds
            }
        }

        public void RequestRealExit()
        {
            _allowRealExit = true;
            Close();
        }

        private void OnWindowClosing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (!_allowRealExit && _settings.Current.MinimizeToTrayOnClose)
            {
                e.Cancel = true;
                Hide();
                _logger?.Info("MainWindow", "Главное окно свёрнуто в системный трей.");
            }
        }

        private void BuildWindowLayout()
        {
            var outerBorder = new Border
            {
                BorderThickness = new Thickness(1),
                SnapsToDevicePixels = true
            };
            outerBorder.SetResourceReference(Border.BackgroundProperty, "WindowBgBrush");
            outerBorder.SetResourceReference(Border.BorderBrushProperty, "BorderSubtleBrush");

            var rootGrid = new Grid();
            rootGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(46) });
            rootGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            // 1. Custom TitleBar
            var titleBar = BuildTitleBar();
            Grid.SetRow(titleBar, 0);
            rootGrid.Children.Add(titleBar);

            // 2. Main Body (Left Sidebar + Right Content Area)
            var bodyGrid = new Grid();
            bodyGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(224) });
            bodyGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var sidebar = BuildSidebar();
            Grid.SetColumn(sidebar, 0);
            bodyGrid.Children.Add(sidebar);

            var rightGrid = new Grid();
            rightGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            rightGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            _notificationBanner = BuildNotificationBanner();
            Grid.SetRow(_notificationBanner, 0);
            rightGrid.Children.Add(_notificationBanner);

            _contentScroller = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Padding = new Thickness(28, 22, 28, 24)
            };
            Grid.SetRow(_contentScroller, 1);
            rightGrid.Children.Add(_contentScroller);

            Grid.SetColumn(rightGrid, 1);
            bodyGrid.Children.Add(rightGrid);

            Grid.SetRow(bodyGrid, 1);
            rootGrid.Children.Add(bodyGrid);

            outerBorder.Child = rootGrid;
            Content = outerBorder;

            SelectPage("home");
        }

        private Border BuildTitleBar()
        {
            var bar = new Border
            {
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(16, 0, 10, 0)
            };
            bar.SetResourceReference(Border.BackgroundProperty, "SidebarBgBrush");
            bar.SetResourceReference(Border.BorderBrushProperty, "BorderSubtleBrush");

            bar.MouseLeftButtonDown += (s, e) =>
            {
                if (e.ClickCount == 2)
                {
                    WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
                }
                else
                {
                    try { DragMove(); } catch { }
                }
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // Brand mark + App title + Status pill
            var leftRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center
            };

            var brandMark = new Border
            {
                Width = 24,
                Height = 24,
                CornerRadius = new CornerRadius(6),
                Margin = new Thickness(0, 0, 10, 0)
            };
            brandMark.SetResourceReference(Border.BackgroundProperty, "AccentBrush");
            var brandIcon = VectorIcons.CreateColoredIcon(VectorIcons.Translate, 14, Brushes.White);
            brandIcon.HorizontalAlignment = HorizontalAlignment.Center;
            brandIcon.VerticalAlignment = VerticalAlignment.Center;
            brandMark.Child = brandIcon;
            leftRow.Children.Add(brandMark);

            var appTitle = new TextBlock
            {
                Text = "Screen Translator",
                FontSize = 13.5,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 12, 0)
            };
            appTitle.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
            leftRow.Children.Add(appTitle);

            _headerStatusBadge = new Border
            {
                CornerRadius = new CornerRadius(5),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(8, 2, 8, 2),
                Cursor = Cursors.Hand,
                ToolTip = "Нажмите для переключения паузы переводчика"
            };
            _headerStatusBadge.SetResourceReference(Border.BackgroundProperty, "CardBgBrush");
            _headerStatusBadge.SetResourceReference(Border.BorderBrushProperty, "BorderSubtleBrush");

            _headerStatusText = new TextBlock
            {
                FontSize = 11.5,
                FontWeight = FontWeights.Medium
            };
            _headerStatusBadge.Child = _headerStatusText;
            _headerStatusBadge.MouseLeftButtonUp += (s, e) =>
            {
                e.Handled = true;
                _hotkeys.TogglePaused();
            };
            leftRow.Children.Add(_headerStatusBadge);
            UpdateHeaderStatus();

            Grid.SetColumn(leftRow, 0);
            grid.Children.Add(leftRow);

            // Window controls (Theme toggle, Minimize, Close)
            var rightRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center
            };

            var themeBtn = new Border
            {
                Width = 30,
                Height = 30,
                CornerRadius = new CornerRadius(6),
                Background = Brushes.Transparent,
                Cursor = Cursors.Hand,
                ToolTip = "Переключить тему оформления (Тёмная / Светлая)",
                Margin = new Thickness(0, 0, 4, 0)
            };
            _themeToggleIcon = VectorIcons.CreateIcon(
                _settings.Current.Theme == AppTheme.Dark ? VectorIcons.Sun : VectorIcons.Moon,
                15,
                "TextSecondaryBrush");
            _themeToggleIcon.HorizontalAlignment = HorizontalAlignment.Center;
            _themeToggleIcon.VerticalAlignment = VerticalAlignment.Center;
            themeBtn.Child = _themeToggleIcon;
            themeBtn.MouseEnter += (s, e) => themeBtn.SetResourceReference(Border.BackgroundProperty, "CardHoverBrush");
            themeBtn.MouseLeave += (s, e) => themeBtn.Background = Brushes.Transparent;
            themeBtn.MouseLeftButtonUp += (s, e) =>
            {
                e.Handled = true;
                var nextTheme = _settings.Current.Theme == AppTheme.Dark ? AppTheme.Light : AppTheme.Dark;
                _settings.Current.Theme = nextTheme;
                _settings.Save();
                ThemeManager.ApplyTheme(nextTheme);
                _themeToggleIcon.Data = Geometry.Parse(nextTheme == AppTheme.Dark ? VectorIcons.Sun : VectorIcons.Moon);
                if (_activePageId == "appearance") SelectPage("appearance");
            };
            rightRow.Children.Add(themeBtn);

            var minBtn = UiKit.CreateIconButton(VectorIcons.Minimize, "Свернуть окно", () => WindowState = WindowState.Minimized);
            minBtn.Margin = new Thickness(0, 0, 4, 0);
            rightRow.Children.Add(minBtn);

            var closeBtn = UiKit.CreateIconButton(
                VectorIcons.Close,
                _settings.Current.MinimizeToTrayOnClose ? "Свернуть в системный трей" : "Закрыть приложение",
                () => Close());
            rightRow.Children.Add(closeBtn);

            Grid.SetColumn(rightRow, 2);
            grid.Children.Add(rightRow);

            bar.Child = grid;
            return bar;
        }

        private void UpdateHeaderStatus()
        {
            if (_headerStatusText == null) return;
            bool paused = _hotkeys.IsPaused;
            _headerStatusText.Text = paused ? "Приостановлен" : "Активен";
            _headerStatusText.SetResourceReference(TextBlock.ForegroundProperty, paused ? "WarningBrush" : "SuccessBrush");
        }

        private Border BuildSidebar()
        {
            var sidebarBorder = new Border
            {
                BorderThickness = new Thickness(0, 0, 1, 0),
                Padding = new Thickness(10, 16, 10, 14)
            };
            sidebarBorder.SetResourceReference(Border.BackgroundProperty, "SidebarBgBrush");
            sidebarBorder.SetResourceReference(Border.BorderBrushProperty, "BorderSubtleBrush");

            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var navStack = new StackPanel();
            _navItems.Clear();

            AddNavItem(navStack, "home", "Главная", VectorIcons.Home);
            AddNavItem(navStack, "translation", "Перевод", VectorIcons.Translate);
            AddNavItem(navStack, "hotkeys", "Горячие клавиши", VectorIcons.Hotkeys);
            AddNavItem(navStack, "appearance", "Внешний вид", VectorIcons.Appearance);
            AddNavItem(navStack, "performance", "Производительность", VectorIcons.Performance);
            AddNavItem(navStack, "general", "Общие настройки", VectorIcons.Settings);
            AddNavItem(navStack, "about", "О приложении", VectorIcons.Info);

            Grid.SetRow(navStack, 0);
            grid.Children.Add(navStack);

            // Footer card with quick shortcut hint
            var footerCard = new Border
            {
                CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(10, 8, 10, 8)
            };
            footerCard.SetResourceReference(Border.BackgroundProperty, "CardBgBrush");
            footerCard.SetResourceReference(Border.BorderBrushProperty, "BorderSubtleBrush");

            var footerStack = new StackPanel();
            var footerTitle = new TextBlock
            {
                Text = "Режим работы",
                FontSize = 11,
                FontWeight = FontWeights.SemiBold
            };
            footerTitle.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
            footerStack.Children.Add(footerTitle);

            var footerSub = new TextBlock
            {
                Text = "Локальный Windows OCR\nФоновый режим в трее",
                FontSize = 11,
                Margin = new Thickness(0, 3, 0, 0),
                LineHeight = 15
            };
            footerSub.SetResourceReference(TextBlock.ForegroundProperty, "TextMutedBrush");
            footerStack.Children.Add(footerSub);

            footerCard.Child = footerStack;
            Grid.SetRow(footerCard, 1);
            grid.Children.Add(footerCard);

            sidebarBorder.Child = grid;
            return sidebarBorder;
        }

        private void AddNavItem(StackPanel parent, string id, string title, string iconData)
        {
            var btnBorder = new Border
            {
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(10, 9, 10, 9),
                Margin = new Thickness(0, 0, 0, 3),
                Cursor = Cursors.Hand,
                Background = Brushes.Transparent
            };

            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var activeBar = new Border
            {
                Width = 3,
                Height = 16,
                CornerRadius = new CornerRadius(1.5),
                Margin = new Thickness(0, 0, 8, 0),
                Visibility = Visibility.Hidden
            };
            activeBar.SetResourceReference(Border.BackgroundProperty, "AccentBrush");
            Grid.SetColumn(activeBar, 0);
            row.Children.Add(activeBar);

            var icon = VectorIcons.CreateIcon(iconData, 15, "TextSecondaryBrush");
            icon.Margin = new Thickness(0, 0, 10, 0);
            icon.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(icon, 1);
            row.Children.Add(icon);

            var txt = new TextBlock
            {
                Text = title,
                FontSize = 13,
                FontWeight = FontWeights.Medium,
                VerticalAlignment = VerticalAlignment.Center
            };
            txt.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
            Grid.SetColumn(txt, 2);
            row.Children.Add(txt);

            btnBorder.Child = row;

            string capturedId = id;
            btnBorder.MouseEnter += (s, e) =>
            {
                if (_activePageId != capturedId)
                {
                    btnBorder.SetResourceReference(Border.BackgroundProperty, "CardHoverBrush");
                }
            };
            btnBorder.MouseLeave += (s, e) =>
            {
                if (_activePageId != capturedId)
                {
                    btnBorder.Background = Brushes.Transparent;
                }
            };
            btnBorder.MouseLeftButtonUp += (s, e) =>
            {
                e.Handled = true;
                SelectPage(capturedId);
            };

            parent.Children.Add(btnBorder);
            _navItems.Add((id, title, iconData, btnBorder, icon, txt, activeBar));
        }

        private Border BuildNotificationBanner()
        {
            var banner = new Border
            {
                Margin = new Thickness(28, 14, 28, 0),
                Padding = new Thickness(12, 8, 12, 8),
                CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(1),
                Visibility = Visibility.Collapsed
            };
            banner.SetResourceReference(Border.BackgroundProperty, "CardBgBrush");
            banner.SetResourceReference(Border.BorderBrushProperty, "WarningBrush");

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            _notificationText = new TextBlock
            {
                FontSize = 12.5,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center
            };
            _notificationText.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
            Grid.SetColumn(_notificationText, 0);
            grid.Children.Add(_notificationText);

            var closeBtn = UiKit.CreateIconButton(VectorIcons.Close, "Скрыть уведомление", () => banner.Visibility = Visibility.Collapsed, 24);
            Grid.SetColumn(closeBtn, 1);
            grid.Children.Add(closeBtn);

            banner.Child = grid;
            return banner;
        }

        public void ShowBanner(string message, bool isErrorOrWarning = false)
        {
            if (string.IsNullOrWhiteSpace(message)) return;
            _notificationText.Text = message;
            _notificationBanner.SetResourceReference(
                Border.BorderBrushProperty,
                isErrorOrWarning ? "WarningBrush" : "SuccessBrush");
            _notificationBanner.Visibility = Visibility.Visible;
        }

        private void OnCoordinatorStateChanged()
        {
            UpdateHeaderStatus();
            if (_activePageId == "home")
            {
                _contentScroller.Content = BuildHomePage();
            }
            else if (_activePageId == "performance")
            {
                _contentScroller.Content = BuildPerformancePage();
            }
        }

        public void SelectPage(string pageId)
        {
            _activePageId = pageId;
            foreach (var item in _navItems)
            {
                bool isSelected = item.Id == pageId;
                if (isSelected)
                {
                    item.NavButton.SetResourceReference(Border.BackgroundProperty, "CardBgBrush");
                    item.NavText.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
                    item.NavText.FontWeight = FontWeights.SemiBold;
                    item.NavIcon.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "AccentBrush");
                    item.ActiveBar.Visibility = Visibility.Visible;
                }
                else
                {
                    item.NavButton.Background = Brushes.Transparent;
                    item.NavText.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
                    item.NavText.FontWeight = FontWeights.Medium;
                    item.NavIcon.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "TextSecondaryBrush");
                    item.ActiveBar.Visibility = Visibility.Hidden;
                }
            }

            switch (pageId)
            {
                case "home":
                    _contentScroller.Content = BuildHomePage();
                    break;
                case "translation":
                    _contentScroller.Content = BuildTranslationPage();
                    break;
                case "hotkeys":
                    _contentScroller.Content = BuildHotkeysPage();
                    break;
                case "appearance":
                    _contentScroller.Content = BuildAppearancePage();
                    break;
                case "performance":
                    _contentScroller.Content = BuildPerformancePage();
                    break;
                case "general":
                    _contentScroller.Content = BuildGeneralPage();
                    break;
                case "about":
                    _contentScroller.Content = BuildAboutPage();
                    break;
            }
        }

        // =========================================================================
        // 1. ГЛАВНАЯ
        // =========================================================================
        private UIElement BuildHomePage()
        {
            var page = new StackPanel();
            page.Children.Add(UiKit.CreateSectionHeader(
                "Панель управления переводчиком",
                "Мгновенный захват, локальное распознавание текста и визуальная подмена поверх оригинала."));

            // Status & Quick Language Bar Card
            var statusStack = new StackPanel();
            var topStatusGrid = new Grid();
            topStatusGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            topStatusGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var statusLeft = new StackPanel();
            var stateTitle = new TextBlock
            {
                Text = _hotkeys.IsPaused ? "Переводчик приостановлен" : _coordinator.LastStatusMessage,
                FontSize = 15,
                FontWeight = FontWeights.SemiBold
            };
            stateTitle.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
            statusLeft.Children.Add(stateTitle);

            var telemetrySub = new TextBlock
            {
                Text = $"Провайдер: {_settings.Current.Provider}  |  Захват: {_coordinator.LastCaptureMs} мс  |  OCR: {_coordinator.LastOcrMs} мс  |  Перевод: {_coordinator.LastTranslateMs} мс",
                FontSize = 12,
                Margin = new Thickness(0, 4, 0, 0)
            };
            telemetrySub.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
            statusLeft.Children.Add(telemetrySub);
            Grid.SetColumn(statusLeft, 0);
            topStatusGrid.Children.Add(statusLeft);

            var pauseBtn = UiKit.CreateSecondaryButton(
                _hotkeys.IsPaused ? "Возобновить работу" : "Приостановить",
                _hotkeys.IsPaused ? VectorIcons.Play : VectorIcons.Pause,
                () =>
                {
                    _hotkeys.TogglePaused();
                    SelectPage("home");
                });
            Grid.SetColumn(pauseBtn, 1);
            topStatusGrid.Children.Add(pauseBtn);
            statusStack.Children.Add(topStatusGrid);

            // Language pair quick bar
            var langBarGrid = new Grid { Margin = new Thickness(0, 14, 0, 0) };
            langBarGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            langBarGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            langBarGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var srcDrop = UiKit.CreateSelectDropdown(SourceLanguages, _settings.Current.SourceLanguage, val =>
            {
                _settings.Current.SourceLanguage = val;
                _settings.Save();
            });
            Grid.SetColumn(srcDrop, 0);
            langBarGrid.Children.Add(srcDrop);

            var swapBtn = UiKit.CreateIconButton(VectorIcons.Swap, "Поменять языки местами", () =>
            {
                string oldSrc = _settings.Current.SourceLanguage;
                string oldTgt = _settings.Current.TargetLanguage;
                _settings.Current.SourceLanguage = oldTgt;
                _settings.Current.TargetLanguage = string.Equals(oldSrc, "auto", StringComparison.OrdinalIgnoreCase) ? "en" : oldSrc;
                _settings.Save();
                SelectPage("home");
            }, 34);
            swapBtn.Margin = new Thickness(10, 0, 10, 0);
            Grid.SetColumn(swapBtn, 1);
            langBarGrid.Children.Add(swapBtn);

            var tgtDrop = UiKit.CreateSelectDropdown(TargetLanguages, _settings.Current.TargetLanguage, val =>
            {
                _settings.Current.TargetLanguage = val;
                _settings.Save();
            });
            Grid.SetColumn(tgtDrop, 2);
            langBarGrid.Children.Add(tgtDrop);

            statusStack.Children.Add(langBarGrid);
            page.Children.Add(UiKit.CreateCard(statusStack));

            // 3 Action Cards (Mode A, Mode B, Mode C)
            var actionsGrid = new Grid { Margin = new Thickness(0, 0, 0, 12) };
            actionsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            actionsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
            actionsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            actionsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
            actionsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var cardCursor = BuildQuickActionCard(
                "Перевод под курсором",
                "Захватывает и автоматически расширяет область текста под указателем мыши.",
                _settings.Current.CursorHotkey.ToReadableString(),
                VectorIcons.CursorTarget,
                "Запустить перевод",
                async () =>
                {
                    WindowState = WindowState.Minimized;
                    await System.Threading.Tasks.Task.Delay(180);
                    await _coordinator.TranslateUnderCursorAsync();
                });
            Grid.SetColumn(cardCursor, 0);
            actionsGrid.Children.Add(cardCursor);

            var cardRegion = BuildQuickActionCard(
                "Выделить область",
                "Обведите любую часть экрана прямоугольником (игры, PDF, изображения).",
                _settings.Current.RegionHotkey.ToReadableString(),
                VectorIcons.RegionSelect,
                "Выделить область",
                () =>
                {
                    Hide();
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        _coordinator.StartRegionSelection();
                    }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                });
            Grid.SetColumn(cardRegion, 2);
            actionsGrid.Children.Add(cardRegion);

            var cardToggle = BuildQuickActionCard(
                "Управление Overlay",
                "Мгновенно скрыть или вернуть последний перевод поверх оригинала.",
                _settings.Current.ToggleOverlayHotkey.ToReadableString(),
                VectorIcons.EyeShow,
                _overlay.IsOverlayVisible ? "Скрыть overlay" : "Показать overlay",
                () => _overlay.ToggleOverlayVisibility());
            Grid.SetColumn(cardToggle, 4);
            actionsGrid.Children.Add(cardToggle);

            page.Children.Add(actionsGrid);

            // Quick Overlay Behavior Controls (Toolbar & Hide on Left Click)
            var quickOverlayStack = new StackPanel();
            quickOverlayStack.Children.Add(UiKit.CreateSettingRow(
                "Панель управления над переводом (крестик, копирование, область)",
                "Отключите, чтобы перевод отображался чистым текстом без верхней панели кнопок.",
                UiKit.CreateToggleSwitch(_settings.Current.ShowOverlayToolbar, val =>
                {
                    _settings.Current.ShowOverlayToolbar = val;
                    _settings.Save();
                    _overlay.RefreshCurrentOverlayStyle();
                })));

            quickOverlayStack.Children.Add(UiKit.CreateSettingRow(
                "Скрытие перевода на ЛКМ",
                "Автоматически скрывать активный перевод при нажатии левой кнопки мыши в любом месте экрана.",
                UiKit.CreateToggleSwitch(_settings.Current.HideOverlayOnLeftClick, val =>
                {
                    _settings.Current.HideOverlayOnLeftClick = val;
                    _settings.Save();
                    _overlay.RefreshCurrentOverlayStyle();
                })));

            page.Children.Add(UiKit.CreateCard(quickOverlayStack));

            // Recent Translations History
            var histHeaderGrid = new Grid { Margin = new Thickness(0, 6, 0, 8) };
            histHeaderGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            histHeaderGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var histTitle = new TextBlock
            {
                Text = "Последние переводы в текущей сессии",
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            };
            histTitle.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
            Grid.SetColumn(histTitle, 0);
            histHeaderGrid.Children.Add(histTitle);

            if (_coordinator.History.Count > 0)
            {
                var clearBtn = UiKit.CreateSecondaryButton("Очистить список", VectorIcons.Trash, () => _coordinator.ClearHistory());
                Grid.SetColumn(clearBtn, 1);
                histHeaderGrid.Children.Add(clearBtn);
            }
            page.Children.Add(histHeaderGrid);

            if (!_settings.Current.SaveSessionHistory)
            {
                var privacyNote = new TextBlock
                {
                    Text = "Сохранение истории переводов отключено в разделе «Общие настройки» для повышенной конфиденциальности.",
                    FontSize = 12.5
                };
                privacyNote.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
                page.Children.Add(UiKit.CreateCard(privacyNote));
            }
            else if (_coordinator.History.Count == 0)
            {
                var emptyText = new TextBlock
                {
                    Text = $"Пока нет выполненных переводов. Наведите курсор на любой текст и нажмите {_settings.Current.CursorHotkey.ToReadableString()} или выделите область экрана с помощью {_settings.Current.RegionHotkey.ToReadableString()}.",
                    FontSize = 12.5,
                    TextWrapping = TextWrapping.Wrap
                };
                emptyText.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
                page.Children.Add(UiKit.CreateCard(emptyText));
            }
            else
            {
                foreach (var item in _coordinator.History.Take(8))
                {
                    page.Children.Add(BuildHistoryItemCard(item));
                }
            }

            return page;
        }

        private Border BuildQuickActionCard(string title, string desc, string hotkey, string iconData, string actionText, Action onAction)
        {
            var stack = new StackPanel();

            var topRow = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var iconBox = new Border
            {
                Width = 30,
                Height = 30,
                CornerRadius = new CornerRadius(6)
            };
            iconBox.SetResourceReference(Border.BackgroundProperty, "InputBgBrush");
            var icon = VectorIcons.CreateIcon(iconData, 15, "AccentBrush");
            icon.HorizontalAlignment = HorizontalAlignment.Center;
            icon.VerticalAlignment = VerticalAlignment.Center;
            iconBox.Child = icon;
            Grid.SetColumn(iconBox, 0);
            topRow.Children.Add(iconBox);

            var hkBadge = new Border
            {
                CornerRadius = new CornerRadius(4),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(6, 2, 6, 2),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center
            };
            hkBadge.SetResourceReference(Border.BackgroundProperty, "InputBgBrush");
            hkBadge.SetResourceReference(Border.BorderBrushProperty, "BorderSubtleBrush");
            var hkText = new TextBlock
            {
                Text = hotkey,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 11
            };
            hkText.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
            hkBadge.Child = hkText;
            Grid.SetColumn(hkBadge, 1);
            topRow.Children.Add(hkBadge);
            stack.Children.Add(topRow);

            var titleBlock = new TextBlock
            {
                Text = title,
                FontSize = 13.5,
                FontWeight = FontWeights.SemiBold
            };
            titleBlock.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
            stack.Children.Add(titleBlock);

            var descBlock = new TextBlock
            {
                Text = desc,
                FontSize = 11.5,
                Margin = new Thickness(0, 4, 0, 12),
                TextWrapping = TextWrapping.Wrap,
                MinHeight = 34
            };
            descBlock.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
            stack.Children.Add(descBlock);

            var btn = UiKit.CreateSecondaryButton(actionText, null, onAction);
            stack.Children.Add(btn);

            return UiKit.CreateCard(stack, new Thickness(14), new Thickness(0));
        }

        private Border BuildHistoryItemCard(TranslationHistoryEntry item)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var stack = new StackPanel { Margin = new Thickness(0, 0, 12, 0) };

            var metaText = new TextBlock
            {
                Text = $"{item.Timestamp:HH:mm:ss}  |  {item.SourceLanguage} → {item.TargetLanguage}  |  {item.Provider}  |  OCR: {item.OcrMs} мс, Перевод: {(item.FromCache ? "кэш" : item.TranslateMs + " мс")}",
                FontSize = 11
            };
            metaText.SetResourceReference(TextBlock.ForegroundProperty, "TextMutedBrush");
            stack.Children.Add(metaText);

            var transBlock = new TextBlock
            {
                Text = item.TranslatedText,
                FontSize = 13.5,
                FontWeight = FontWeights.Medium,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 2)
            };
            transBlock.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
            stack.Children.Add(transBlock);

            var srcBlock = new TextBlock
            {
                Text = item.SourceText,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap
            };
            srcBlock.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
            stack.Children.Add(srcBlock);

            Grid.SetColumn(stack, 0);
            grid.Children.Add(stack);

            string capturedTranslated = item.TranslatedText;
            var copyBtn = UiKit.CreateIconButton(VectorIcons.Copy, "Скопировать перевод", () =>
            {
                try { Clipboard.SetText(capturedTranslated); } catch { }
            });
            copyBtn.VerticalAlignment = VerticalAlignment.Top;
            Grid.SetColumn(copyBtn, 1);
            grid.Children.Add(copyBtn);

            return UiKit.CreateCard(grid, new Thickness(14, 10, 14, 10), new Thickness(0, 0, 0, 8));
        }

        // =========================================================================
        // 2. ПЕРЕВОД
        // =========================================================================
        private UIElement BuildTranslationPage()
        {
            var page = new StackPanel();
            page.Children.Add(UiKit.CreateSectionHeader(
                "Настройки перевода и языковых провайдеров",
                "Выберите направление перевода, подключите облачный или бесплатный сервис и проверьте соединение."));

            var langCardStack = new StackPanel();
            langCardStack.Children.Add(UiKit.CreateSettingRow(
                "Исходный язык",
                "Язык распознаваемого текста на экране (рекомендуется Автоопределение).",
                UiKit.CreateSelectDropdown(SourceLanguages, _settings.Current.SourceLanguage, val =>
                {
                    _settings.Current.SourceLanguage = val;
                    _settings.Save();
                }, 250)));

            langCardStack.Children.Add(UiKit.CreateSettingRow(
                "Целевой язык перевода",
                "Язык, на который переводится распознанный текст (по умолчанию Русский).",
                UiKit.CreateSelectDropdown(TargetLanguages, _settings.Current.TargetLanguage, val =>
                {
                    _settings.Current.TargetLanguage = val;
                    _settings.Save();
                }, 250)));

            langCardStack.Children.Add(UiKit.CreateSettingRow(
                "Провайдер перевода",
                "Google GTX работает сразу без ключей. Для DeepL и OpenAI поддерживаются пользовательские API-ключи.",
                UiKit.CreateSelectDropdown(Providers, _settings.Current.ProviderName, val =>
                {
                    _settings.Current.ProviderName = val;
                    _settings.Save();
                    SelectPage("translation");
                }, 290)));

            page.Children.Add(UiKit.CreateCard(langCardStack));

            // Provider-specific credentials card (DPAPI encrypted)
            var credStack = new StackPanel();
            var credTitle = new TextBlock
            {
                Text = "Параметры подключения и защита API-ключей (DPAPI)",
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 6)
            };
            credTitle.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
            credStack.Children.Add(credTitle);

            var credSub = new TextBlock
            {
                Text = "Ключи шифруются средствами Windows DPAPI для текущего пользователя и никогда не сохраняются открытым текстом.",
                FontSize = 11.5,
                Margin = new Thickness(0, 0, 0, 12),
                TextWrapping = TextWrapping.Wrap
            };
            credSub.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
            credStack.Children.Add(credSub);

            credStack.Children.Add(UiKit.CreateSettingRow(
                "API-ключ DeepL",
                "Используется при выборе провайдера DeepL API.",
                UiKit.CreateTextInput(_settings.GetDeepLApiKey(), val =>
                {
                    _settings.SetDeepLApiKey(val);
                    _settings.Save();
                }, 270, isSecret: true)));

            credStack.Children.Add(UiKit.CreateSettingRow(
                "Использовать бесплатный шлюз DeepL (api-free.deepl.com)",
                "Включите для ключей с суффиксом :fx (DeepL API Free).",
                UiKit.CreateToggleSwitch(_settings.Current.DeepLUseFreeEndpoint, val =>
                {
                    _settings.Current.DeepLUseFreeEndpoint = val;
                    _settings.Save();
                })));

            credStack.Children.Add(UiKit.CreateSettingRow(
                "API-ключ OpenAI / совместимого LLM",
                "Необязателен при использовании локального сервера Ollama (localhost).",
                UiKit.CreateTextInput(_settings.GetOpenAiApiKey(), val =>
                {
                    _settings.SetOpenAiApiKey(val);
                    _settings.Save();
                }, 270, isSecret: true)));

            credStack.Children.Add(UiKit.CreateSettingRow(
                "Адрес API (Endpoint) совместимого LLM",
                "Например: https://api.openai.com/v1/chat/completions или http://localhost:11434/v1/chat/completions",
                UiKit.CreateTextInput(_settings.Current.OpenAiEndpoint, val =>
                {
                    _settings.Current.OpenAiEndpoint = val;
                    _settings.Save();
                }, 270)));

            credStack.Children.Add(UiKit.CreateSettingRow(
                "Модель LLM",
                "Например: gpt-4o-mini, deepseek-chat или llama3.2",
                UiKit.CreateTextInput(_settings.Current.OpenAiModel, val =>
                {
                    _settings.Current.OpenAiModel = val;
                    _settings.Save();
                }, 270)));

            credStack.Children.Add(UiKit.CreateSettingRow(
                "Контактный Email для MyMemory (опционально)",
                "Увеличивает дневной лимит бесплатных переводов в сервисе MyMemory.",
                UiKit.CreateTextInput(_settings.Current.MyMemoryEmail, val =>
                {
                    _settings.Current.MyMemoryEmail = val;
                    _settings.Save();
                }, 270)));

            var testRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 12, 0, 0)
            };
            var testResultText = new TextBlock
            {
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(14, 0, 0, 0),
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 380
            };
            testResultText.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");

            var testBtn = UiKit.CreatePrimaryButton("Проверить перевод", VectorIcons.Check, async () =>
            {
                testResultText.Text = "Выполняется тестовый запрос...";
                var res = await _translator.TranslateAsync("Visual screen translation works accurately.", "en", _settings.Current.TargetLanguage, CancellationToken.None);
                if (res.Success)
                {
                    testResultText.SetResourceReference(TextBlock.ForegroundProperty, "SuccessBrush");
                    testResultText.Text = $"Успешно ({res.ElapsedMs} мс): «{res.TranslatedText}»";
                }
                else
                {
                    testResultText.SetResourceReference(TextBlock.ForegroundProperty, "DangerBrush");
                    testResultText.Text = res.ErrorMessage;
                }
            });
            testRow.Children.Add(testBtn);
            testRow.Children.Add(testResultText);
            credStack.Children.Add(testRow);

            page.Children.Add(UiKit.CreateCard(credStack));

            // Post-translation behavior
            var behaviorStack = new StackPanel();
            behaviorStack.Children.Add(UiKit.CreateSettingRow(
                "Скрытие перевода на ЛКМ",
                "Автоматически скрывать перевод после нажатия левой кнопки мыши (ЛКМ).",
                UiKit.CreateToggleSwitch(_settings.Current.HideOverlayOnLeftClick, val =>
                {
                    _settings.Current.HideOverlayOnLeftClick = val;
                    _settings.Save();
                    _overlay.RefreshCurrentOverlayStyle();
                })));

            behaviorStack.Children.Add(UiKit.CreateSettingRow(
                "Панель управления над переводом (крестик, выбор области, копирование)",
                "Показывать компактную панель кнопок над переведённым текстом. Отключите для показа только перевода.",
                UiKit.CreateToggleSwitch(_settings.Current.ShowOverlayToolbar, val =>
                {
                    _settings.Current.ShowOverlayToolbar = val;
                    _settings.Save();
                    _overlay.RefreshCurrentOverlayStyle();
                })));

            behaviorStack.Children.Add(UiKit.CreateSettingRow(
                "Автоматически копировать перевод в буфер обмена",
                "После каждого успешного перевода текст сразу доступен для вставки (Ctrl+V).",
                UiKit.CreateToggleSwitch(_settings.Current.AutoCopyTranslation, val =>
                {
                    _settings.Current.AutoCopyTranslation = val;
                    _settings.Save();
                })));

            var autoHideOptions = new List<(string, string)>
            {
                ("0", "Не скрывать автоматически"),
                ("5", "Через 5 секунд"),
                ("10", "Через 10 секунд"),
                ("20", "Через 20 секунд")
            };
            behaviorStack.Children.Add(UiKit.CreateSettingRow(
                "Автоматическое скрытие временного overlay",
                "Через сколько секунд скрывать перевод, если пользователь не закрыл его вручную.",
                UiKit.CreateSelectDropdown(autoHideOptions, _settings.Current.AutoHideOverlaySeconds.ToString(), val =>
                {
                    if (int.TryParse(val, out int sec))
                    {
                        _settings.Current.AutoHideOverlaySeconds = sec;
                        _settings.Save();
                    }
                }, 220)));

            page.Children.Add(UiKit.CreateCard(behaviorStack));
            return page;
        }

        // =========================================================================
        // 3. ГОРЯЧИЕ КЛАВИШИ
        // =========================================================================
        private UIElement BuildHotkeysPage()
        {
            var page = new StackPanel();
            page.Children.Add(UiKit.CreateSectionHeader(
                "Глобальные горячие клавиши и кнопки мыши",
                "Поддерживаются сочетания клавиатуры, а также боковые кнопки мыши (Mouse4, Mouse5), колёсико (СКМ) и кнопки мыши с модификаторами."));

            var hkStack = new StackPanel();
            hkStack.Children.Add(BuildHotkeyRecorderRow(_settings.Current.CursorHotkey));
            hkStack.Children.Add(BuildHotkeyRecorderRow(_settings.Current.RegionHotkey));
            hkStack.Children.Add(BuildHotkeyRecorderRow(_settings.Current.ToggleOverlayHotkey));

            var escBadge = new Border
            {
                CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(12, 6, 12, 6)
            };
            escBadge.SetResourceReference(Border.BackgroundProperty, "InputBgBrush");
            escBadge.SetResourceReference(Border.BorderBrushProperty, "BorderSubtleBrush");
            var escText = new TextBlock
            {
                Text = "Esc (Активно во время перевода)",
                FontFamily = new FontFamily("Consolas"),
                FontSize = 12
            };
            escText.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
            escBadge.Child = escText;

            hkStack.Children.Add(UiKit.CreateSettingRow(
                "Отменить выделение или закрыть текущий overlay",
                "Перехватывается только тогда, когда открыто окно выделения области или активен overlay.",
                escBadge));

            hkStack.Children.Add(UiKit.CreateSettingRow(
                "Скрытие перевода на ЛКМ",
                "Скрывать активный перевод одним нажатием левой кнопки мыши (ЛКМ).",
                UiKit.CreateToggleSwitch(_settings.Current.HideOverlayOnLeftClick, val =>
                {
                    _settings.Current.HideOverlayOnLeftClick = val;
                    _settings.Save();
                    _overlay.RefreshCurrentOverlayStyle();
                })));

            var resetRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 14, 0, 0)
            };
            resetRow.Children.Add(UiKit.CreateSecondaryButton("Восстановить сочетания по умолчанию", VectorIcons.Refresh, () =>
            {
                _settings.Current.CursorHotkey.Modifiers = NativeMethods.MOD_CONTROL | NativeMethods.MOD_SHIFT;
                _settings.Current.CursorHotkey.VirtualKey = 'T';
                _settings.Current.RegionHotkey.Modifiers = NativeMethods.MOD_CONTROL | NativeMethods.MOD_SHIFT;
                _settings.Current.RegionHotkey.VirtualKey = 'A';
                _settings.Current.ToggleOverlayHotkey.Modifiers = NativeMethods.MOD_CONTROL | NativeMethods.MOD_SHIFT;
                _settings.Current.ToggleOverlayHotkey.VirtualKey = 'H';
                _settings.Save();
                _hotkeys.RegisterAllConfiguredHotkeys();
                SelectPage("hotkeys");
            }));
            hkStack.Children.Add(resetRow);

            page.Children.Add(UiKit.CreateCard(hkStack));
            return page;
        }

        private Grid BuildHotkeyRecorderRow(HotkeyConfig config)
        {
            var rightStack = new StackPanel { Orientation = Orientation.Horizontal };

            var statusLabel = new TextBlock
            {
                Text = config.StatusMessage,
                FontSize = 11.5,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0)
            };
            statusLabel.SetResourceReference(
                TextBlock.ForegroundProperty,
                config.IsRegistered ? "SuccessBrush" : "WarningBrush");
            rightStack.Children.Add(statusLabel);

            var recorderBorder = new Border
            {
                MinWidth = 175,
                Height = 34,
                CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(12, 0, 12, 0),
                Cursor = Cursors.Hand,
                Focusable = true
            };
            recorderBorder.SetResourceReference(Border.BackgroundProperty, "InputBgBrush");
            recorderBorder.SetResourceReference(Border.BorderBrushProperty, "BorderSubtleBrush");

            var keyLabel = new TextBlock
            {
                Text = config.ToReadableString(),
                FontFamily = new FontFamily("Consolas"),
                FontSize = 12.5,
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            keyLabel.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
            recorderBorder.Child = keyLabel;

            bool isRecording = false;

            void ApplyNewBinding(uint mods, uint vk)
            {
                config.Modifiers = mods;
                config.VirtualKey = vk;
                isRecording = false;
                recorderBorder.SetResourceReference(Border.BorderBrushProperty, "BorderSubtleBrush");

                _settings.Save();
                _hotkeys.RegisterAllConfiguredHotkeys();
                keyLabel.Text = config.ToReadableString();
                statusLabel.Text = config.StatusMessage;
                statusLabel.SetResourceReference(
                    TextBlock.ForegroundProperty,
                    config.IsRegistered ? "SuccessBrush" : "WarningBrush");
            }

            recorderBorder.PreviewMouseDown += (s, e) =>
            {
                uint mods = 0;
                if ((Keyboard.Modifiers & ModifierKeys.Control) != 0) mods |= NativeMethods.MOD_CONTROL;
                if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0) mods |= NativeMethods.MOD_SHIFT;
                if ((Keyboard.Modifiers & ModifierKeys.Alt) != 0) mods |= NativeMethods.MOD_ALT;
                if ((Keyboard.Modifiers & ModifierKeys.Windows) != 0) mods |= NativeMethods.MOD_WIN;

                if (!isRecording)
                {
                    if (e.ChangedButton == MouseButton.Left && mods == 0)
                    {
                        e.Handled = true;
                        isRecording = true;
                        recorderBorder.SetResourceReference(Border.BorderBrushProperty, "BorderFocusBrush");
                        keyLabel.Text = "Клавиша или кнопка мыши...";
                        recorderBorder.Focus();
                        return;
                    }
                    // Allow direct side-button click on the recorder box even before entering record mode
                    if (e.ChangedButton == MouseButton.XButton1)
                    {
                        e.Handled = true;
                        ApplyNewBinding(mods, NativeMethods.VK_XBUTTON1);
                        return;
                    }
                    if (e.ChangedButton == MouseButton.XButton2)
                    {
                        e.Handled = true;
                        ApplyNewBinding(mods, NativeMethods.VK_XBUTTON2);
                        return;
                    }
                }
                else
                {
                    e.Handled = true;
                    if (e.ChangedButton == MouseButton.XButton1)
                    {
                        ApplyNewBinding(mods, NativeMethods.VK_XBUTTON1);
                        return;
                    }
                    if (e.ChangedButton == MouseButton.XButton2)
                    {
                        ApplyNewBinding(mods, NativeMethods.VK_XBUTTON2);
                        return;
                    }
                    if (e.ChangedButton == MouseButton.Middle)
                    {
                        ApplyNewBinding(mods, NativeMethods.VK_MBUTTON);
                        return;
                    }
                    if (e.ChangedButton == MouseButton.Right)
                    {
                        if (mods == 0)
                        {
                            ShowBanner("Для правой кнопки мыши (ПКМ) удерживайте Ctrl, Shift или Alt, либо используйте боковые кнопки Mouse4 / Mouse5.", true);
                            return;
                        }
                        ApplyNewBinding(mods, NativeMethods.VK_RBUTTON);
                        return;
                    }
                    if (e.ChangedButton == MouseButton.Left)
                    {
                        if (mods != 0)
                        {
                            ApplyNewBinding(mods, NativeMethods.VK_LBUTTON);
                        }
                        else
                        {
                            isRecording = false;
                            recorderBorder.SetResourceReference(Border.BorderBrushProperty, "BorderSubtleBrush");
                            keyLabel.Text = config.ToReadableString();
                        }
                        return;
                    }
                }
            };

            recorderBorder.LostFocus += (s, e) =>
            {
                if (isRecording)
                {
                    isRecording = false;
                    recorderBorder.SetResourceReference(Border.BorderBrushProperty, "BorderSubtleBrush");
                    keyLabel.Text = config.ToReadableString();
                }
            };

            recorderBorder.PreviewKeyDown += (s, e) =>
            {
                if (!isRecording) return;
                e.Handled = true;

                Key key = e.Key == Key.System ? e.SystemKey : e.Key;
                if (key == Key.Escape)
                {
                    isRecording = false;
                    recorderBorder.SetResourceReference(Border.BorderBrushProperty, "BorderSubtleBrush");
                    keyLabel.Text = config.ToReadableString();
                    return;
                }

                if (key == Key.LeftCtrl || key == Key.RightCtrl ||
                    key == Key.LeftShift || key == Key.RightShift ||
                    key == Key.LeftAlt || key == Key.RightAlt ||
                    key == Key.LWin || key == Key.RWin)
                {
                    return;
                }

                uint mods = 0;
                if ((Keyboard.Modifiers & ModifierKeys.Control) != 0) mods |= NativeMethods.MOD_CONTROL;
                if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0) mods |= NativeMethods.MOD_SHIFT;
                if ((Keyboard.Modifiers & ModifierKeys.Alt) != 0) mods |= NativeMethods.MOD_ALT;
                if ((Keyboard.Modifiers & ModifierKeys.Windows) != 0) mods |= NativeMethods.MOD_WIN;

                bool isFunctionKey = key >= Key.F1 && key <= Key.F12;
                if (mods == 0 && !isFunctionKey)
                {
                    ShowBanner("Используйте хотя бы одну клавишу-модификатор (Ctrl, Shift или Alt), клавишу F1–F12 или кнопку мыши (Mouse4/Mouse5/СКМ).", true);
                    return;
                }

                int vk = KeyInterop.VirtualKeyFromKey(key);
                if (vk <= 0) return;

                ApplyNewBinding(mods, (uint)vk);
            };

            rightStack.Children.Add(recorderBorder);

            var m4Btn = UiKit.CreateSecondaryButton("Mouse4", null, () => ApplyNewBinding(0, NativeMethods.VK_XBUTTON1));
            m4Btn.Margin = new Thickness(6, 0, 0, 0);
            m4Btn.ToolTip = "Назначить боковую кнопку мыши Mouse4 (XButton1)";
            rightStack.Children.Add(m4Btn);

            var m5Btn = UiKit.CreateSecondaryButton("Mouse5", null, () => ApplyNewBinding(0, NativeMethods.VK_XBUTTON2));
            m5Btn.Margin = new Thickness(4, 0, 0, 0);
            m5Btn.ToolTip = "Назначить боковую кнопку мыши Mouse5 (XButton2)";
            rightStack.Children.Add(m5Btn);

            return UiKit.CreateSettingRow(
                config.DisplayName,
                "Нажмите на поле и введите сочетание клавиш или нажмите кнопку мыши (Mouse4, Mouse5, СКМ).",
                rightStack);
        }

        // =========================================================================
        // 4. ВНЕШНИЙ ВИД
        // =========================================================================
        private UIElement BuildAppearancePage()
        {
            var page = new StackPanel();
            page.Children.Add(UiKit.CreateSectionHeader(
                "Внешний вид приложения и Overlay-слоя",
                "Настройте тему интерфейса, способ маскировки оригинального текста, шрифт и непрозрачность."));

            var themeCardStack = new StackPanel();
            var themeOptions = new List<(string, string)>
            {
                (AppTheme.Dark.ToString(), "Тёмная тема (Obsidian Slate — по умолчанию)"),
                (AppTheme.Light.ToString(), "Светлая тема (Paper & Slate)")
            };
            themeCardStack.Children.Add(UiKit.CreateSettingRow(
                "Тема главного окна и меню трея",
                "Переключается мгновенно без перезапуска приложения.",
                UiKit.CreateSelectDropdown(themeOptions, _settings.Current.ThemeName, val =>
                {
                    _settings.Current.ThemeName = val;
                    _settings.Save();
                    ThemeManager.ApplyTheme(_settings.Current.Theme);
                    _themeToggleIcon.Data = Geometry.Parse(_settings.Current.Theme == AppTheme.Dark ? VectorIcons.Sun : VectorIcons.Moon);
                    SelectPage("appearance");
                }, 280)));

            page.Children.Add(UiKit.CreateCard(themeCardStack));

            // Live Interactive Preview Box
            var previewBorder = new Border
            {
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(16),
                Margin = new Thickness(0, 8, 0, 0),
                Background = new SolidColorBrush(Color.FromRgb(24, 28, 38)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(45, 53, 68)),
                BorderThickness = new Thickness(1)
            };
            var previewOverlayBox = new Border
            {
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(8, 4, 8, 4)
            };
            var previewText = new TextBlock
            {
                Text = "Пример визуальной замены текста непосредственно поверх оригинала на экране.",
                TextWrapping = TextWrapping.Wrap
            };
            previewOverlayBox.Child = previewText;
            previewBorder.Child = previewOverlayBox;

            void UpdateLivePreview()
            {
                byte alpha = (byte)Math.Max(65, Math.Min(255, (int)Math.Round(_settings.Current.OverlayOpacity * 255)));
                if (_settings.Current.OverlayBgMode == OverlayBackgroundMode.LightTranslucent)
                {
                    previewOverlayBox.Background = new SolidColorBrush(Color.FromArgb(alpha, 248, 250, 252));
                    previewText.Foreground = new SolidColorBrush(Color.FromRgb(15, 23, 42));
                }
                else
                {
                    previewOverlayBox.Background = new SolidColorBrush(Color.FromArgb(alpha, 22, 26, 35));
                    previewText.Foreground = new SolidColorBrush(Color.FromRgb(241, 245, 249));
                }
                previewText.FontFamily = new FontFamily(_settings.Current.OverlayFontFamily);
                previewText.FontSize = _settings.Current.AutoFitFontSize ? 14.5 : _settings.Current.FixedFontSize;
                _overlay.RefreshCurrentOverlayStyle();
            }
            UpdateLivePreview();

            var overlayStack = new StackPanel();
            overlayStack.Children.Add(UiKit.CreateSettingRow(
                "Панель управления над переводом (крестик, выбор области, копирование)",
                "Кнопки «Оригинал», «Повторить», «Область», «Копировать», «Без панели», «Переместить» и «Закрыть». Отключите, чтобы перевод показывался без панели.",
                UiKit.CreateToggleSwitch(_settings.Current.ShowOverlayToolbar, val =>
                {
                    _settings.Current.ShowOverlayToolbar = val;
                    _settings.Save();
                    _overlay.RefreshCurrentOverlayStyle();
                })));

            overlayStack.Children.Add(UiKit.CreateSettingRow(
                "Скрытие перевода на ЛКМ",
                "Автоматически скрывать активный перевод при нажатии левой кнопки мыши (ЛКМ).",
                UiKit.CreateToggleSwitch(_settings.Current.HideOverlayOnLeftClick, val =>
                {
                    _settings.Current.HideOverlayOnLeftClick = val;
                    _settings.Save();
                    _overlay.RefreshCurrentOverlayStyle();
                })));

            overlayStack.Children.Add(UiKit.CreateSettingRow(
                "Режим маскировки фона Overlay",
                "Автоподбор анализирует пиксели вокруг строки и подбирает точный цвет фона или полупрозрачную подложку.",
                UiKit.CreateSelectDropdown(OverlayBgModes, _settings.Current.OverlayBgModeName, val =>
                {
                    _settings.Current.OverlayBgModeName = val;
                    _settings.Save();
                    UpdateLivePreview();
                }, 280)));

            // Opacity Slider
            var opacityPanel = new StackPanel { Orientation = Orientation.Horizontal };
            var opacityLabel = new TextBlock
            {
                Text = $"{(int)Math.Round(_settings.Current.OverlayOpacity * 100)}%",
                Width = 44,
                FontSize = 12.5,
                VerticalAlignment = VerticalAlignment.Center
            };
            opacityLabel.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
            var opacitySlider = new Slider
            {
                Minimum = 0.40,
                Maximum = 1.00,
                Value = _settings.Current.OverlayOpacity,
                Width = 180,
                VerticalAlignment = VerticalAlignment.Center
            };
            opacitySlider.ValueChanged += (s, e) =>
            {
                _settings.Current.OverlayOpacity = Math.Round(e.NewValue, 2);
                opacityLabel.Text = $"{(int)Math.Round(_settings.Current.OverlayOpacity * 100)}%";
                _settings.Save();
                UpdateLivePreview();
            };
            opacityPanel.Children.Add(opacityLabel);
            opacityPanel.Children.Add(opacitySlider);

            overlayStack.Children.Add(UiKit.CreateSettingRow(
                "Непрозрачность подложки Overlay",
                "Регулирует плотность перекрытия оригинального текста.",
                opacityPanel));

            overlayStack.Children.Add(UiKit.CreateSettingRow(
                "Шрифт текста перевода",
                "Гарнитура, используемая для отрисовки переведённых строк.",
                UiKit.CreateSelectDropdown(FontFamilies, _settings.Current.OverlayFontFamily, val =>
                {
                    _settings.Current.OverlayFontFamily = val;
                    _settings.Save();
                    UpdateLivePreview();
                }, 220)));

            overlayStack.Children.Add(UiKit.CreateSettingRow(
                "Автоматический подбор размера шрифта под область",
                "Масштабирует кегль и межстрочный интервал под высоту и ширину распознанных строк оригинала.",
                UiKit.CreateToggleSwitch(_settings.Current.AutoFitFontSize, val =>
                {
                    _settings.Current.AutoFitFontSize = val;
                    _settings.Save();
                    UpdateLivePreview();
                })));

            var fontSizePanel = new StackPanel { Orientation = Orientation.Horizontal };
            var fontSizeLabel = new TextBlock
            {
                Text = $"{_settings.Current.FixedFontSize:F0} пт",
                Width = 44,
                FontSize = 12.5,
                VerticalAlignment = VerticalAlignment.Center
            };
            fontSizeLabel.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
            var fontSizeSlider = new Slider
            {
                Minimum = 10,
                Maximum = 28,
                Value = _settings.Current.FixedFontSize,
                Width = 180,
                VerticalAlignment = VerticalAlignment.Center
            };
            fontSizeSlider.ValueChanged += (s, e) =>
            {
                _settings.Current.FixedFontSize = Math.Round(e.NewValue);
                fontSizeLabel.Text = $"{_settings.Current.FixedFontSize:F0} пт";
                _settings.Save();
                UpdateLivePreview();
            };
            fontSizePanel.Children.Add(fontSizeLabel);
            fontSizePanel.Children.Add(fontSizeSlider);

            overlayStack.Children.Add(UiKit.CreateSettingRow(
                "Фиксированный размер шрифта (при отключённом автоподборе)",
                "Используется, если автоматическое масштабирование выключено.",
                fontSizePanel));

            overlayStack.Children.Add(previewBorder);
            page.Children.Add(UiKit.CreateCard(overlayStack));
            return page;
        }

        // =========================================================================
        // 5. ПРОИЗВОДИТЕЛЬНОСТЬ
        // =========================================================================
        private UIElement BuildPerformancePage()
        {
            var page = new StackPanel();
            page.Children.Add(UiKit.CreateSectionHeader(
                "Производительность, локальный OCR и кэширование",
                "Все операции OCR выполняются локально средствами Windows без отправки скриншотов в интернет."));

            var ocrStack = new StackPanel();
            var installedLangs = _ocr.GetAvailableOcrLanguages();
            var ocrOptions = new List<(string, string)>
            {
                ("auto", "Автоматически (умный выбор en-US / ru)")
            };
            foreach (var tag in installedLangs)
            {
                ocrOptions.Add((tag, $"Языковой пакет Windows OCR: {tag}"));
            }

            ocrStack.Children.Add(UiKit.CreateSettingRow(
                "Движок распознавания текста (Локальный Windows OCR)",
                $"Установленные в системе языки OCR: {string.Join(", ", installedLangs)}",
                UiKit.CreateSelectDropdown(ocrOptions, _settings.Current.PreferredOcrLanguage, val =>
                {
                    _settings.Current.PreferredOcrLanguage = val;
                    _settings.Save();
                }, 260)));

            ocrStack.Children.Add(UiKit.CreateSettingRow(
                "Адаптивное 2x увеличение для мелкого текста интерфейсов",
                "Повышает точность распознавания мелких шрифтов с помощью бикубической интерполяции.",
                UiKit.CreateToggleSwitch(_settings.Current.UpscaleSmallRegions, val =>
                {
                    _settings.Current.UpscaleSmallRegions = val;
                    _settings.Save();
                })));

            ocrStack.Children.Add(UiKit.CreateSettingRow(
                "Автоматически расширять область поиска под курсором",
                "Если строка или абзац выходят за края первичного захвата вокруг курсора, область расширяется автоматически.",
                UiKit.CreateToggleSwitch(_settings.Current.AutoExpandCursorCapture, val =>
                {
                    _settings.Current.AutoExpandCursorCapture = val;
                    _settings.Save();
                })));

            var sizeOptions = new List<(string, string)>
            {
                ("360x100", "Компактная (360 × 100 пикс.)"),
                ("460x130", "Стандартная (460 × 130 пикс.)"),
                ("620x170", "Широкая (620 × 170 пикс.)")
            };
            string currentSizeKey = $"{_settings.Current.CursorCaptureWidth}x{_settings.Current.CursorCaptureHeight}";
            ocrStack.Children.Add(UiKit.CreateSettingRow(
                "Базовый размер захвата вокруг курсора (Режим A)",
                "Первичная зона вокруг указателя мыши при нажатии горячей клавиши перевода под курсором.",
                UiKit.CreateSelectDropdown(sizeOptions, currentSizeKey, val =>
                {
                    var parts = val.Split('x');
                    if (parts.Length == 2 && int.TryParse(parts[0], out int w) && int.TryParse(parts[1], out int h))
                    {
                        _settings.Current.CursorCaptureWidth = w;
                        _settings.Current.CursorCaptureHeight = h;
                        _settings.Save();
                    }
                }, 240)));

            page.Children.Add(UiKit.CreateCard(ocrStack));

            // Cache & Network timeouts
            var cacheStack = new StackPanel();
            cacheStack.Children.Add(UiKit.CreateSettingRow(
                "Кэширование повторных переводов в памяти (LRU)",
                $"Записей в кэше: {_cache.Count} из {_settings.Current.MaxCacheEntries}  |  Попаданий: {_cache.HitCount}  |  Промахов: {_cache.MissCount}",
                UiKit.CreateToggleSwitch(_settings.Current.EnableTranslationCache, val =>
                {
                    _settings.Current.EnableTranslationCache = val;
                    _settings.Save();
                })));

            var timeoutOptions = new List<(string, string)>
            {
                ("4", "4 секунды"),
                ("8", "8 секунд (рекомендуется)"),
                ("15", "15 секунд")
            };
            cacheStack.Children.Add(UiKit.CreateSettingRow(
                "Тайм-аут сетевого запроса перевода",
                "Максимальное время ожидания ответа от облачного провайдера перевода.",
                UiKit.CreateSelectDropdown(timeoutOptions, _settings.Current.RequestTimeoutSeconds.ToString(), val =>
                {
                    if (int.TryParse(val, out int sec))
                    {
                        _settings.Current.RequestTimeoutSeconds = sec;
                        _settings.Save();
                    }
                }, 220)));

            var clearCacheRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 10, 0, 0)
            };
            clearCacheRow.Children.Add(UiKit.CreateSecondaryButton("Очистить кэш переводов", VectorIcons.Trash, () =>
            {
                _cache.Clear();
                SelectPage("performance");
            }));
            cacheStack.Children.Add(clearCacheRow);

            page.Children.Add(UiKit.CreateCard(cacheStack));
            return page;
        }

        // =========================================================================
        // 6. ОБЩИЕ НАСТРОЙКИ
        // =========================================================================
        private UIElement BuildGeneralPage()
        {
            var page = new StackPanel();
            page.Children.Add(UiKit.CreateSectionHeader(
                "Общие настройки, конфиденциальность и диагностика",
                "Управление интеграцией с Windows, защитой экранных данных и системным журналом."));

            var sysStack = new StackPanel();
            sysStack.Children.Add(UiKit.CreateSettingRow(
                "Запускать вместе с Windows",
                "Автоматически запускать приложение в свёрнутом режиме в системном трее при входе в систему.",
                UiKit.CreateToggleSwitch(_settings.Current.LaunchOnStartup, val =>
                {
                    _settings.Current.LaunchOnStartup = val;
                    _settings.Save();
                    _settings.ApplyStartupSetting(val);
                })));

            sysStack.Children.Add(UiKit.CreateSettingRow(
                "Сворачивать в системный трей при закрытии главного окна",
                "При нажатии на крестик приложение продолжит работать в фоне и реагировать на горячие клавиши.",
                UiKit.CreateToggleSwitch(_settings.Current.MinimizeToTrayOnClose, val =>
                {
                    _settings.Current.MinimizeToTrayOnClose = val;
                    _settings.Save();
                })));

            page.Children.Add(UiKit.CreateCard(sysStack));

            var privStack = new StackPanel();
            privStack.Children.Add(UiKit.CreateSettingRow(
                "Исключать окно Overlay из захвата экрана (WDA_EXCLUDEFROMCAPTURE)",
                "Предотвращает повторное распознавание собственного перевода при повторном нажатии горячей клавиши на том же месте.",
                UiKit.CreateToggleSwitch(_settings.Current.ExcludeOverlayFromCapture, val =>
                {
                    _settings.Current.ExcludeOverlayFromCapture = val;
                    _settings.Save();
                    _overlay.RefreshCurrentOverlayStyle();
                })));

            privStack.Children.Add(UiKit.CreateSettingRow(
                "Сохранять историю переводов в текущей сессии",
                "История хранится только в оперативной памяти до выхода из программы и никогда не записывается на диск.",
                UiKit.CreateToggleSwitch(_settings.Current.SaveSessionHistory, val =>
                {
                    _settings.Current.SaveSessionHistory = val;
                    _settings.Save();
                    if (!val) _coordinator.ClearHistory();
                })));

            page.Children.Add(UiKit.CreateCard(privStack));

            // Diagnostics Log Box
            var logStack = new StackPanel();
            var logTitle = new TextBlock
            {
                Text = "Журнал диагностики (без персональных данных и текста экрана)",
                FontSize = 13.5,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 8)
            };
            logTitle.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
            logStack.Children.Add(logTitle);

            var logBoxBorder = new Border
            {
                Height = 140,
                CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(8)
            };
            logBoxBorder.SetResourceReference(Border.BackgroundProperty, "InputBgBrush");
            logBoxBorder.SetResourceReference(Border.BorderBrushProperty, "BorderSubtleBrush");

            var logTextBox = new TextBox
            {
                Text = string.Join(Environment.NewLine, _logger.GetRecentLogs()),
                IsReadOnly = true,
                TextWrapping = TextWrapping.NoWrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                FontFamily = new FontFamily("Consolas"),
                FontSize = 11.5
            };
            logTextBox.SetResourceReference(Control.ForegroundProperty, "TextSecondaryBrush");
            logTextBox.ScrollToEnd();
            logBoxBorder.Child = logTextBox;
            logStack.Children.Add(logBoxBorder);

            var logBtns = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 10, 0, 0)
            };
            var refreshLogsBtn = UiKit.CreateSecondaryButton("Обновить журнал", VectorIcons.Refresh, () =>
            {
                logTextBox.Text = string.Join(Environment.NewLine, _logger.GetRecentLogs());
                logTextBox.ScrollToEnd();
            });
            refreshLogsBtn.Margin = new Thickness(0, 0, 10, 0);
            logBtns.Children.Add(refreshLogsBtn);

            var openFolderBtn = UiKit.CreateSecondaryButton("Открыть папку конфигурации", null, () =>
            {
                try
                {
                    string dir = Path.GetDirectoryName(_settings.SettingsFilePath);
                    if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                    {
                        Process.Start("explorer.exe", dir);
                    }
                }
                catch { }
            });
            logBtns.Children.Add(openFolderBtn);
            logStack.Children.Add(logBtns);

            page.Children.Add(UiKit.CreateCard(logStack));
            return page;
        }

        // =========================================================================
        // 7. О ПРИЛОЖЕНИИ
        // =========================================================================
        private UIElement BuildAboutPage()
        {
            var page = new StackPanel();
            page.Children.Add(UiKit.CreateSectionHeader(
                "О приложении Screen Translator",
                "Нативный экранный переводчик для Windows 10 и Windows 11."));

            var infoStack = new StackPanel();
            var title = new TextBlock
            {
                Text = "Screen Translator для Windows — Версия 1.0.0 (x64)",
                FontSize = 15,
                FontWeight = FontWeights.SemiBold
            };
            title.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
            infoStack.Children.Add(title);

            var techText = new TextBlock
            {
                Text = "Технологический стек:\n" +
                       "• Платформа: C# / .NET Framework 4.8 + WPF (нативный однофайловый исполняемый модуль без тяжёлых внешних рантаймов).\n" +
                       "• Распознавание текста: локальный системный движок Windows.Media.Ocr (WinRT) с пословными и построчными координатами BoundingRect.\n" +
                       "• Захват и позиционирование: Win32 GDI/User32 с поддержкой Per-Monitor DPI v2, отрицательных координат мультимониторных конфигураций и флага WDA_EXCLUDEFROMCAPTURE.\n" +
                       "• Безопасность: шифрование ключей API через Windows DPAPI (ProtectedData) и фильтрация секретов в логах.",
                FontSize = 12.5,
                LineHeight = 20,
                Margin = new Thickness(0, 10, 0, 0),
                TextWrapping = TextWrapping.Wrap
            };
            techText.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
            infoStack.Children.Add(techText);

            page.Children.Add(UiKit.CreateCard(infoStack));

            var limitStack = new StackPanel();
            var limitTitle = new TextBlock
            {
                Text = "Технические особенности и честные ограничения",
                FontSize = 14,
                FontWeight = FontWeights.SemiBold
            };
            limitTitle.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
            limitStack.Children.Add(limitTitle);

            var limitBody = new TextBlock
            {
                Text = "1. Визуальная подмена вместо модификации памяти: приложение создаёт прозрачный экранный слой (Overlay) поверх исходного окна и не вмешивается в чужие процессы.\n" +
                       "2. Маскировка фона: на однородных фонах (документы, браузер, стандартные интерфейсы) цвет подложки автоматически подбирается по пикселям оригинала. На сложных неоднородных фонах (3D-игры, фотографии) используется контрастная полупрозрачная подложка для сохранения читаемости.\n" +
                       "3. Полноэкранные приложения и DRM: приложения в режиме эксклюзивного полноэкранного вывода DirectX/Vulkan, античит-системы и окна с защитой DRM (WDA_MONITOR) могут блокировать захват изображения или отображение поверх них на уровне ОС Windows.",
                FontSize = 12.5,
                LineHeight = 19,
                Margin = new Thickness(0, 8, 0, 0),
                TextWrapping = TextWrapping.Wrap
            };
            limitBody.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
            limitStack.Children.Add(limitBody);

            page.Children.Add(UiKit.CreateCard(limitStack));
            return page;
        }
    }
}
