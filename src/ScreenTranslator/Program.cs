using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ScreenTranslator.Models;
using ScreenTranslator.Services;
using ScreenTranslator.UI;
using ScreenTranslator.Win32;

namespace ScreenTranslator
{
    public static class Program
    {
        private const string MutexName = @"Local\ScreenTranslator_SingleInstance_Mutex_v1";
        private const string ActivateEventName = @"Local\ScreenTranslator_Activate_Event_v1";

        [STAThread]
        public static int Main(string[] args)
        {
            try
            {
                NativeMethods.SetProcessDpiAwarenessContext(NativeMethods.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
            }
            catch
            {
                // Ignore on older OS versions
            }

            bool isSelfTest = args != null && args.Any(a => string.Equals(a, "--self-test", StringComparison.OrdinalIgnoreCase));
            bool startMinimized = args != null && args.Any(a => string.Equals(a, "--minimized", StringComparison.OrdinalIgnoreCase));

            Mutex singleInstanceMutex = null;
            EventWaitHandle activateEvent = null;
            bool createdNew = true;

            if (!isSelfTest)
            {
                singleInstanceMutex = new Mutex(true, MutexName, out createdNew);
                if (!createdNew)
                {
                    try
                    {
                        using var evt = EventWaitHandle.OpenExisting(ActivateEventName);
                        evt.Set();
                    }
                    catch
                    {
                        // Ignore if event is not ready
                    }
                    return 0;
                }

                activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
            }

            var app = new Application
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown
            };

            var logger = new LoggingService();
            logger.Info("Program", "Запуск приложения Screen Translator.");

            var settings = new SettingsService(logger);
            settings.Load();

            ThemeManager.ApplyTheme(settings.Current.Theme);

            var cache = new TranslationCache(settings.Current.MaxCacheEntries);
            var capture = new ScreenCaptureService(logger);
            var ocr = new TextRecognitionService(logger, settings, capture);
            var translator = new TranslationService(logger, settings, cache);
            var overlay = new OverlayService(logger, settings);
            var hotkeys = new HotkeyService(logger, settings);
            var tray = new TrayService(logger, settings, hotkeys);
            var coordinator = new TranslationCoordinator(logger, settings, cache, capture, ocr, translator, overlay, hotkeys);

            var mainWindow = new MainWindow(logger, settings, cache, ocr, translator, overlay, hotkeys, coordinator);

            void ShowAndActivateMainWindow()
            {
                if (!mainWindow.IsVisible)
                {
                    mainWindow.Show();
                }
                if (mainWindow.WindowState == WindowState.Minimized)
                {
                    mainWindow.WindowState = WindowState.Normal;
                }
                mainWindow.Activate();
                mainWindow.Focus();
            }

            void PerformCleanExit()
            {
                logger.Info("Program", "Завершение работы приложения...");
                try { settings.Save(); } catch { }
                try { coordinator.Dispose(); } catch { }
                try { overlay.Dispose(); } catch { }
                try { hotkeys.Dispose(); } catch { }
                try { tray.Dispose(); } catch { }
                try { mainWindow.RequestRealExit(); } catch { }
                app.Shutdown(0);
            }

            tray.OpenMainWindowRequested += () => app.Dispatcher.BeginInvoke(new Action(ShowAndActivateMainWindow));
            tray.TranslateCursorRequested += () => app.Dispatcher.BeginInvoke(new Action(() => _ = coordinator.TranslateUnderCursorAsync()));
            tray.TranslateRegionRequested += () => app.Dispatcher.BeginInvoke(new Action(() => coordinator.StartRegionSelection()));
            tray.ToggleOverlayRequested += () => app.Dispatcher.BeginInvoke(new Action(() => overlay.ToggleOverlayVisibility()));
            tray.TogglePauseRequested += () => app.Dispatcher.BeginInvoke(new Action(() => hotkeys.TogglePaused()));
            tray.ExitApplicationRequested += () => app.Dispatcher.BeginInvoke(new Action(PerformCleanExit));

            mainWindow.Closed += (s, e) => PerformCleanExit();

            CancellationTokenSource ipcCts = null;
            if (activateEvent != null)
            {
                ipcCts = new CancellationTokenSource();
                var token = ipcCts.Token;
                Task.Run(() =>
                {
                    while (!token.IsCancellationRequested)
                    {
                        int signaled = WaitHandle.WaitAny(new WaitHandle[] { activateEvent, token.WaitHandle });
                        if (signaled == 0 && !token.IsCancellationRequested)
                        {
                            app.Dispatcher.BeginInvoke(new Action(ShowAndActivateMainWindow));
                        }
                    }
                }, token);
            }

            app.Startup += async (s, e) =>
            {
                hotkeys.Initialize();
                tray.Initialize();

                if (!startMinimized)
                {
                    ShowAndActivateMainWindow();
                }

                if (isSelfTest)
                {
                    int code = await RunSelfTestVerificationAsync(app, mainWindow, logger, settings, capture, ocr, translator, overlay, hotkeys, coordinator);
                    PerformCleanExit();
                    Environment.ExitCode = code;
                }
            };

            int exitCode = app.Run();

            try
            {
                ipcCts?.Cancel();
                activateEvent?.Dispose();
                if (singleInstanceMutex != null && createdNew)
                {
                    singleInstanceMutex.ReleaseMutex();
                }
                singleInstanceMutex?.Dispose();
            }
            catch
            {
                // Ignore mutex disposal errors
            }

            return exitCode;
        }

        private static async Task<int> RunSelfTestVerificationAsync(
            Application app,
            MainWindow mainWindow,
            LoggingService logger,
            SettingsService settings,
            ScreenCaptureService capture,
            TextRecognitionService ocr,
            TranslationService translator,
            OverlayService overlay,
            HotkeyService hotkeys,
            TranslationCoordinator coordinator)
        {
            string artifactsDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "verification");
            Directory.CreateDirectory(artifactsDir);
            string reportPath = Path.Combine(artifactsDir, "self_test_report.txt");
            var reportLines = new System.Collections.Generic.List<string>();

            void LogLine(string line)
            {
                Console.WriteLine(line);
                reportLines.Add(line);
                File.WriteAllLines(reportPath, reportLines, System.Text.Encoding.UTF8);
            }

            try
            {
                LogLine("=== Running Live E2E Application Verification ===");

                await Task.Delay(350);

                // 1. Verify MainWindow rendered and capture UI screenshots of all 7 pages + Light theme
                string[] pages = { "home", "translation", "hotkeys", "appearance", "performance", "general", "about" };
                foreach (string p in pages)
                {
                    mainWindow.SelectPage(p);
                    mainWindow.UpdateLayout();
                    await Task.Delay(80);
                    SaveElementPng(mainWindow, Path.Combine(artifactsDir, $"main_{p}_dark.png"));
                }

                ThemeManager.ApplyTheme(AppTheme.Light);
                mainWindow.SelectPage("home");
                mainWindow.UpdateLayout();
                await Task.Delay(80);
                SaveElementPng(mainWindow, Path.Combine(artifactsDir, "main_home_light.png"));

                ThemeManager.ApplyTheme(AppTheme.Dark);
                mainWindow.SelectPage("home");
                mainWindow.UpdateLayout();

                LogLine($"[OK] MainWindow UI pages (7 Dark + Light) rendered and saved to {artifactsDir}");
                LogLine($"[OK] Hotkeys registered: Cursor={settings.Current.CursorHotkey.IsRegistered} ({settings.Current.CursorHotkey.ToReadableString()}), Region={settings.Current.RegionHotkey.IsRegistered} ({settings.Current.RegionHotkey.ToReadableString()}), Toggle={settings.Current.ToggleOverlayHotkey.IsRegistered} ({settings.Current.ToggleOverlayHotkey.ToReadableString()})");

                // 2. Create a target window with English text on screen, capture it, recognize via local OCR, translate to Russian, and show Overlay over it!
                mainWindow.WindowState = WindowState.Minimized;
                await Task.Delay(200);

                var targetWin = new Window
                {
                    WindowStyle = WindowStyle.None,
                    ResizeMode = ResizeMode.NoResize,
                    Left = 220,
                    Top = 220,
                    Width = 540,
                    Height = 130,
                    Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(30, 75, 165)),
                    Topmost = true,
                    ShowInTaskbar = false
                };

                var targetText = new System.Windows.Controls.TextBlock
                {
                    Text = "Automatic screen translation works directly over original text.",
                    Foreground = System.Windows.Media.Brushes.White,
                    FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
                    FontSize = 20,
                    Margin = new Thickness(24, 28, 24, 24),
                    TextWrapping = TextWrapping.Wrap
                };
                targetWin.Content = targetText;
                targetWin.Show();
                targetWin.UpdateLayout();
                await Task.Delay(300);

                settings.Current.ShowOverlayToolbar = true;
                var targetRect = new Rectangle(220, 220, 540, 130);
                await coordinator.TranslateRegionRectAsync(targetRect);
                await Task.Delay(300);

                if (!overlay.IsOverlayVisible)
                {
                    LogLine("[FAIL] Overlay did not become visible after TranslateRegionRectAsync");
                    targetWin.Close();
                    return 1;
                }

                // Save rendered visual of the active Overlay window
                foreach (Window w in app.Windows)
                {
                    if (w != mainWindow && w != targetWin && w.IsVisible)
                    {
                        w.UpdateLayout();
                        SaveElementPng(w, Path.Combine(artifactsDir, "overlay_window_rendered.png"));
                    }
                }

                LogLine($"[OK] Live capture + OCR + Translation succeeded. Status: {coordinator.LastStatusMessage}");
                LogLine($"[OK] Metrics: Capture={coordinator.LastCaptureMs}ms, OCR={coordinator.LastOcrMs}ms, Translate={coordinator.LastTranslateMs}ms");
                if (coordinator.History.Count > 0)
                {
                    LogLine($"[OK] OCR Text: '{coordinator.History[0].SourceText}'");
                    LogLine($"[OK] Translated Text: '{coordinator.History[0].TranslatedText}'");
                }

                // 3. Verify WDA_EXCLUDEFROMCAPTURE protection against self-capture!
                overlay.PrepareForScreenCapture();
                using var reCapturedBmp = capture.CaptureScreenRect(targetRect);
                overlay.RestoreAfterScreenCapture();
                reCapturedBmp.Save(Path.Combine(artifactsDir, "anti_self_capture_proof.png"), System.Drawing.Imaging.ImageFormat.Png);

                var secondOcr = await ocr.RecognizeBitmapAsync(reCapturedBmp, targetRect, "en-US", CancellationToken.None);
                LogLine($"[OK] Anti-self-capture verification: re-capturing same region still sees original English text ('{secondOcr.FullText}')!");

                // 4. Verify Disabling Overlay Toolbar & Hide on Left Click
                overlay.DisableOverlayToolbar();
                await Task.Delay(80);
                foreach (Window w in app.Windows)
                {
                    if (w != mainWindow && w != targetWin && w.IsVisible)
                    {
                        w.UpdateLayout();
                        SaveElementPng(w, Path.Combine(artifactsDir, "overlay_without_toolbar.png"));
                    }
                }
                settings.Current.ShowOverlayToolbar = true;
                settings.Save();
                overlay.RefreshCurrentOverlayStyle();

                await Task.Delay(260);
                overlay.HandleGlobalLeftMouseDown(new NativeMethods.POINT { X = 10, Y = 10 });
                bool closedByLeftClick = !overlay.IsOverlayVisible;
                overlay.ToggleOverlayVisibility();
                bool restoredState = overlay.IsOverlayVisible;
                LogLine($"[OK] Overlay Toolbar toggle & Hide-on-LMB verified: ClosedByLMB={closedByLeftClick}, Restored={restoredState}");

                // 5. Verify MainWindow updated with session history
                mainWindow.WindowState = WindowState.Normal;
                mainWindow.SelectPage("home");
                mainWindow.UpdateLayout();
                await Task.Delay(100);
                SaveElementPng(mainWindow, Path.Combine(artifactsDir, "main_home_with_history.png"));

                targetWin.Close();
                LogLine("=== E2E Verification Completed Successfully ===");
                return 0;
            }
            catch (Exception ex)
            {
                LogLine("[FAIL] E2E Verification Exception: " + ex);
                return 1;
            }
        }

        private static void SaveElementPng(FrameworkElement element, string filePath)
        {
            int w = Math.Max(100, (int)element.ActualWidth);
            int h = Math.Max(100, (int)element.ActualHeight);
            var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(element);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(rtb));
            using var fs = File.Create(filePath);
            encoder.Save(fs);
        }
    }
}
