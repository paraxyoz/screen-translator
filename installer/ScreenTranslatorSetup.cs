using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Microsoft.Win32;
using IOPath = System.IO.Path;

namespace ScreenTranslator.Installer
{
    public static class SetupProgram
    {
        private const string UninstallRegistryKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\ScreenTranslator";
        private const string RunRegistryKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

        [STAThread]
        public static int Main(string[] args)
        {
            string defaultDir = IOPath.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs",
                "ScreenTranslator");

            bool silent = args != null && args.Any(a => string.Equals(a, "--silent", StringComparison.OrdinalIgnoreCase));
            bool uninstall = args != null && args.Any(a => string.Equals(a, "--uninstall", StringComparison.OrdinalIgnoreCase));
            string customDirArg = args?.FirstOrDefault(a => a.StartsWith("--dir=", StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrEmpty(customDirArg))
            {
                defaultDir = customDirArg.Substring("--dir=".Length).Trim('"');
            }

            string currentExeName = IOPath.GetFileName(Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty);
            if (string.Equals(currentExeName, "Uninstall.exe", StringComparison.OrdinalIgnoreCase))
            {
                uninstall = true;
            }

            if (uninstall)
            {
                return PerformUninstall(defaultDir);
            }

            if (silent)
            {
                return PerformInstall(defaultDir, createDesktopShortcut: false, createStartMenuShortcut: false, addAutoStart: false, launchAfter: false);
            }

            var app = new Application();
            var win = new SetupWindow(defaultDir);

            string screenshotArg = args?.FirstOrDefault(a => a.StartsWith("--save-screenshot=", StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrEmpty(screenshotArg))
            {
                string outPng = screenshotArg.Substring("--save-screenshot=".Length).Trim('"');
                win.ContentRendered += (s, e) =>
                {
                    try
                    {
                        win.UpdateLayout();
                        var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(
                            (int)win.ActualWidth,
                            (int)win.ActualHeight,
                            96,
                            96,
                            PixelFormats.Pbgra32);
                        rtb.Render(win);
                        var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                        enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
                        string dir = IOPath.GetDirectoryName(outPng);
                        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                        using (var fs = File.Create(outPng)) enc.Save(fs);
                    }
                    catch { }
                    win.Close();
                };
            }

            app.Run(win);
            return 0;
        }

        public static int PerformInstall(
            string targetDir,
            bool createDesktopShortcut,
            bool createStartMenuShortcut,
            bool addAutoStart,
            bool launchAfter)
        {
            try
            {
                StopRunningProcesses();
                Directory.CreateDirectory(targetDir);

                string exePath = IOPath.Combine(targetDir, "ScreenTranslator.exe");
                using (Stream payload = Assembly.GetExecutingAssembly().GetManifestResourceStream("Payload.ScreenTranslator.exe"))
                {
                    if (payload == null) throw new InvalidOperationException("Встроенный образ ScreenTranslator.exe не найден.");
                    using var fs = File.Create(exePath);
                    payload.CopyTo(fs);
                }

                string uninstallerPath = IOPath.Combine(targetDir, "Uninstall.exe");
                string currentInstaller = Process.GetCurrentProcess().MainModule?.FileName;
                if (!string.IsNullOrEmpty(currentInstaller) && File.Exists(currentInstaller))
                {
                    if (!string.Equals(currentInstaller, uninstallerPath, StringComparison.OrdinalIgnoreCase))
                    {
                        File.Copy(currentInstaller, uninstallerPath, overwrite: true);
                    }
                }

                if (createDesktopShortcut)
                {
                    string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                    CreateShortcut(IOPath.Combine(desktop, "Screen Translator.lnk"), exePath, targetDir, "Экранный переводчик для Windows (by paraxyoz)");
                }

                if (createStartMenuShortcut)
                {
                    string startMenu = IOPath.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Screen Translator");
                    Directory.CreateDirectory(startMenu);
                    CreateShortcut(IOPath.Combine(startMenu, "Screen Translator.lnk"), exePath, targetDir, "Экранный переводчик для Windows (by paraxyoz)");
                    if (File.Exists(uninstallerPath))
                    {
                        CreateShortcut(IOPath.Combine(startMenu, "Удалить Screen Translator.lnk"), uninstallerPath, targetDir, "Удаление Screen Translator");
                    }
                }

                try
                {
                    using var runKey = Registry.CurrentUser.OpenSubKey(RunRegistryKey, writable: true);
                    if (runKey != null)
                    {
                        if (addAutoStart)
                        {
                            runKey.SetValue("ScreenTranslator", $"\"{exePath}\" --minimized");
                        }
                        else
                        {
                            runKey.DeleteValue("ScreenTranslator", throwOnMissingValue: false);
                        }
                    }
                }
                catch { }

                try
                {
                    using var uninstKey = Registry.CurrentUser.CreateSubKey(UninstallRegistryKey);
                    if (uninstKey != null)
                    {
                        uninstKey.SetValue("DisplayName", "Screen Translator");
                        uninstKey.SetValue("DisplayVersion", "1.1.0");
                        uninstKey.SetValue("Publisher", "paraxyoz");
                        uninstKey.SetValue("URLInfoAbout", "https://github.com/paraxyoz/screen-translator");
                        uninstKey.SetValue("DisplayIcon", $"{exePath},0");
                        uninstKey.SetValue("InstallLocation", targetDir);
                        uninstKey.SetValue("UninstallString", $"\"{uninstallerPath}\" --uninstall \"--dir={targetDir}\"");
                        uninstKey.SetValue("NoModify", 1, RegistryValueKind.DWord);
                        uninstKey.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                        uninstKey.SetValue("EstimatedSize", 460, RegistryValueKind.DWord);
                    }
                }
                catch { }

                if (launchAfter && File.Exists(exePath))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = exePath,
                        WorkingDirectory = targetDir,
                        UseShellExecute = true
                    });
                }

                return 0;
            }
            catch
            {
                return 1;
            }
        }

        public static int PerformUninstall(string targetDir)
        {
            try
            {
                StopRunningProcesses();

                using (var key = Registry.CurrentUser.OpenSubKey(RunRegistryKey, writable: true))
                {
                    key?.DeleteValue("ScreenTranslator", throwOnMissingValue: false);
                }

                try
                {
                    Registry.CurrentUser.DeleteSubKeyTree(UninstallRegistryKey, throwOnMissingSubKey: false);
                }
                catch { }

                string desktopLnk = IOPath.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Screen Translator.lnk");
                if (File.Exists(desktopLnk)) File.Delete(desktopLnk);

                string startMenuDir = IOPath.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Screen Translator");
                if (Directory.Exists(startMenuDir)) Directory.Delete(startMenuDir, recursive: true);

                string exePath = IOPath.Combine(targetDir, "ScreenTranslator.exe");
                if (File.Exists(exePath)) File.Delete(exePath);

                return 0;
            }
            catch
            {
                return 1;
            }
        }

        private static void StopRunningProcesses()
        {
            try
            {
                foreach (var p in Process.GetProcessesByName("ScreenTranslator"))
                {
                    try
                    {
                        p.Kill();
                        p.WaitForExit(1500);
                    }
                    catch { }
                }
            }
            catch { }
        }

        private static void CreateShortcut(string shortcutPath, string targetExe, string workingDir, string description)
        {
            try
            {
                Type shellType = Type.GetTypeFromProgID("WScript.Shell");
                if (shellType == null) return;
                object shell = Activator.CreateInstance(shellType);
                object shortcut = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { shortcutPath });
                if (shortcut == null) return;
                Type scType = shortcut.GetType();
                scType.InvokeMember("TargetPath", BindingFlags.SetProperty, null, shortcut, new object[] { targetExe });
                scType.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, shortcut, new object[] { workingDir });
                scType.InvokeMember("Description", BindingFlags.SetProperty, null, shortcut, new object[] { description });
                scType.InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, null);
            }
            catch
            {
                // Ignore if WScript.Shell COM is restricted
            }
        }
    }

    internal sealed class SetupWindow : Window
    {
        public SetupWindow(string defaultInstallDir)
        {
            Title = "Установка Screen Translator v1.1.0";
            Width = 560;
            Height = 445;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            Background = new SolidColorBrush(Color.FromRgb(15, 17, 23));
            FontFamily = new FontFamily("Segoe UI");

            var outer = new Border
            {
                BorderBrush = new SolidColorBrush(Color.FromRgb(39, 45, 58)),
                BorderThickness = new Thickness(1)
            };

            var rootGrid = new Grid();
            rootGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(42) });
            rootGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            // 1. Custom TitleBar with working Minimize & Close buttons
            var titleBar = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(20, 23, 31)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(35, 41, 54)),
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(16, 0, 10, 0)
            };
            titleBar.MouseLeftButtonDown += (s, e) =>
            {
                if (e.Handled) return;
                try { DragMove(); } catch { }
            };

            var titleGrid = new Grid();
            titleGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            titleGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var titleText = new TextBlock
            {
                Text = "Мастер установки Screen Translator",
                Foreground = new SolidColorBrush(Color.FromRgb(226, 232, 240)),
                FontSize = 12.5,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(titleText, 0);
            titleGrid.Children.Add(titleText);

            var winCtrlRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center
            };
            var minBtn = CreateIconBtn("M 5 12 L 19 12", "Свернуть", () => WindowState = WindowState.Minimized);
            minBtn.Margin = new Thickness(0, 0, 4, 0);
            winCtrlRow.Children.Add(minBtn);

            var closeBtn = CreateIconBtn("M 6 6 L 18 18 M 18 6 L 6 18", "Закрыть", () => Close());
            winCtrlRow.Children.Add(closeBtn);

            Grid.SetColumn(winCtrlRow, 1);
            titleGrid.Children.Add(winCtrlRow);
            titleBar.Child = titleGrid;

            Grid.SetRow(titleBar, 0);
            rootGrid.Children.Add(titleBar);

            // 2. Main Installer Content
            var stack = new StackPanel { Margin = new Thickness(24, 20, 24, 20) };

            var header = new TextBlock
            {
                Text = "Установка Screen Translator 1.1.0 (x64)",
                Foreground = new SolidColorBrush(Color.FromRgb(241, 245, 249)),
                FontSize = 18,
                FontWeight = FontWeights.SemiBold
            };
            stack.Children.Add(header);

            var sub = new TextBlock
            {
                Text = "Нативный экранный переводчик для Windows 10/11 с локальным распознаванием текста (OCR) и визуальной подменой поверх оригинала.",
                Foreground = new SolidColorBrush(Color.FromRgb(148, 163, 184)),
                FontSize = 12.5,
                Margin = new Thickness(0, 6, 0, 16),
                TextWrapping = TextWrapping.Wrap
            };
            stack.Children.Add(sub);

            var dirLabel = new TextBlock
            {
                Text = "Папка установки:",
                Foreground = new SolidColorBrush(Color.FromRgb(203, 213, 225)),
                FontSize = 12.5,
                Margin = new Thickness(0, 0, 0, 6)
            };
            stack.Children.Add(dirLabel);

            var dirGrid = new Grid { Margin = new Thickness(0, 0, 0, 14) };
            dirGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            dirGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var dirBox = new TextBox
            {
                Text = defaultInstallDir,
                Height = 32,
                VerticalContentAlignment = VerticalAlignment.Center,
                Padding = new Thickness(8, 0, 8, 0),
                Background = new SolidColorBrush(Color.FromRgb(26, 30, 39)),
                Foreground = new SolidColorBrush(Color.FromRgb(241, 245, 249)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(45, 55, 72)),
                BorderThickness = new Thickness(1),
                FontSize = 12.5
            };
            Grid.SetColumn(dirBox, 0);
            dirGrid.Children.Add(dirBox);

            var browseBtn = CreateButton("Обзор...", Color.FromRgb(30, 36, 48), () =>
            {
                try
                {
                    using var fbd = new System.Windows.Forms.FolderBrowserDialog
                    {
                        Description = "Выберите папку для установки Screen Translator",
                        SelectedPath = dirBox.Text
                    };
                    if (fbd.ShowDialog() == System.Windows.Forms.DialogResult.OK && !string.IsNullOrWhiteSpace(fbd.SelectedPath))
                    {
                        dirBox.Text = fbd.SelectedPath;
                    }
                }
                catch { }
            });
            browseBtn.Margin = new Thickness(8, 0, 0, 0);
            Grid.SetColumn(browseBtn, 1);
            dirGrid.Children.Add(browseBtn);
            stack.Children.Add(dirGrid);

            var cbDesktop = CreateCheckBox("Создать ярлык на Рабочем столе", true);
            stack.Children.Add(cbDesktop);

            var cbStartMenu = CreateCheckBox("Создать ярлыки в меню «Пуск» и зарегистрировать деинсталлятор", true);
            stack.Children.Add(cbStartMenu);

            var cbAutoStart = CreateCheckBox("Запускать автоматически вместе с Windows (в системном трее)", false);
            stack.Children.Add(cbAutoStart);

            var cbLaunch = CreateCheckBox("Запустить Screen Translator сразу после установки", true);
            cbLaunch.Margin = new Thickness(0, 0, 0, 14);
            stack.Children.Add(cbLaunch);

            var progressBar = new ProgressBar
            {
                Height = 6,
                Minimum = 0,
                Maximum = 100,
                Value = 0,
                Background = new SolidColorBrush(Color.FromRgb(26, 30, 39)),
                Foreground = new SolidColorBrush(Color.FromRgb(59, 130, 246)),
                BorderThickness = new Thickness(0),
                Margin = new Thickness(0, 0, 0, 8),
                Visibility = Visibility.Collapsed
            };
            stack.Children.Add(progressBar);

            var statusText = new TextBlock
            {
                Foreground = new SolidColorBrush(Color.FromRgb(16, 185, 129)),
                FontSize = 12,
                Margin = new Thickness(0, 0, 0, 10)
            };
            stack.Children.Add(statusText);

            var bottomGrid = new Grid();
            bottomGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            bottomGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var authorLabel = new TextBlock
            {
                Text = "Автор: paraxyoz  |  github.com/paraxyoz",
                Foreground = new SolidColorBrush(Color.FromRgb(100, 116, 139)),
                FontSize = 11.5,
                VerticalAlignment = VerticalAlignment.Center,
                Cursor = Cursors.Hand
            };
            authorLabel.MouseLeftButtonDown += (s, e) =>
            {
                e.Handled = true;
                try { Process.Start(new ProcessStartInfo("https://github.com/paraxyoz") { UseShellExecute = true }); } catch { }
            };
            Grid.SetColumn(authorLabel, 0);
            bottomGrid.Children.Add(authorLabel);

            var btnRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };

            var cancelBtn = CreateButton("Отмена", Color.FromRgb(30, 36, 48), () => Close());
            cancelBtn.Margin = new Thickness(0, 0, 10, 0);
            btnRow.Children.Add(cancelBtn);

            var installBtn = CreateButton("Установить", Color.FromRgb(59, 130, 246), () =>
            {
                progressBar.Visibility = Visibility.Visible;
                progressBar.Value = 65;
                statusText.Foreground = new SolidColorBrush(Color.FromRgb(16, 185, 129));
                statusText.Text = "Распаковка файлов и регистрация в системе...";

                int code = SetupProgram.PerformInstall(
                    dirBox.Text.Trim(),
                    cbDesktop.IsChecked == true,
                    cbStartMenu.IsChecked == true,
                    cbAutoStart.IsChecked == true,
                    cbLaunch.IsChecked == true);

                if (code == 0)
                {
                    progressBar.Value = 100;
                    Close();
                }
                else
                {
                    progressBar.Visibility = Visibility.Collapsed;
                    statusText.Foreground = new SolidColorBrush(Color.FromRgb(239, 68, 68));
                    statusText.Text = "Ошибка установки. Проверьте права доступа к выбранной папке.";
                }
            });
            btnRow.Children.Add(installBtn);

            Grid.SetColumn(btnRow, 1);
            bottomGrid.Children.Add(btnRow);

            stack.Children.Add(bottomGrid);
            Grid.SetRow(stack, 1);
            rootGrid.Children.Add(stack);

            outer.Child = rootGrid;
            Content = outer;
        }

        private static CheckBox CreateCheckBox(string text, bool isChecked)
        {
            return new CheckBox
            {
                Content = text,
                IsChecked = isChecked,
                Foreground = new SolidColorBrush(Color.FromRgb(226, 232, 240)),
                FontSize = 12.5,
                Margin = new Thickness(0, 0, 0, 7),
                Cursor = Cursors.Hand
            };
        }

        private static Border CreateIconBtn(string pathData, string tooltip, Action onClick)
        {
            var b = new Border
            {
                Width = 28,
                Height = 28,
                CornerRadius = new CornerRadius(5),
                Background = Brushes.Transparent,
                Cursor = Cursors.Hand,
                ToolTip = tooltip
            };
            var p = new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse(pathData),
                Stroke = new SolidColorBrush(Color.FromRgb(148, 163, 184)),
                StrokeThickness = 1.6,
                Width = 14,
                Height = 14,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            b.Child = p;
            b.MouseEnter += (s, e) => b.Background = new SolidColorBrush(Color.FromRgb(34, 41, 54));
            b.MouseLeave += (s, e) => b.Background = Brushes.Transparent;
            b.MouseLeftButtonDown += (s, e) =>
            {
                e.Handled = true;
                onClick?.Invoke();
            };
            b.MouseLeftButtonUp += (s, e) => e.Handled = true;
            return b;
        }

        private static Border CreateButton(string text, Color bg, Action onClick)
        {
            var b = new Border
            {
                Background = new SolidColorBrush(bg),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(16, 7, 16, 7),
                Cursor = Cursors.Hand,
                Child = new TextBlock
                {
                    Text = text,
                    Foreground = Brushes.White,
                    FontSize = 12.5,
                    FontWeight = FontWeights.Medium,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };
            b.MouseLeftButtonDown += (s, e) =>
            {
                e.Handled = true;
                onClick?.Invoke();
            };
            b.MouseLeftButtonUp += (s, e) => e.Handled = true;
            return b;
        }
    }
}
