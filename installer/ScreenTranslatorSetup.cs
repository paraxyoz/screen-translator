using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;

namespace ScreenTranslator.Installer
{
    public static class SetupProgram
    {
        [STAThread]
        public static int Main(string[] args)
        {
            string defaultDir = Path.Combine(
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

            if (uninstall)
            {
                return PerformUninstall(defaultDir);
            }

            if (silent)
            {
                return PerformInstall(defaultDir, createDesktopShortcut: false, createStartMenuShortcut: false, launchAfter: false);
            }

            var app = new Application();
            var win = new SetupWindow(defaultDir);
            app.Run(win);
            return 0;
        }

        public static int PerformInstall(string targetDir, bool createDesktopShortcut, bool createStartMenuShortcut, bool launchAfter)
        {
            try
            {
                StopRunningProcesses();
                Directory.CreateDirectory(targetDir);

                string exePath = Path.Combine(targetDir, "ScreenTranslator.exe");
                using (Stream payload = Assembly.GetExecutingAssembly().GetManifestResourceStream("Payload.ScreenTranslator.exe"))
                {
                    if (payload == null) throw new InvalidOperationException("Встроенный образ ScreenTranslator.exe не найден.");
                    using var fs = File.Create(exePath);
                    payload.CopyTo(fs);
                }

                // Copy installer as uninstaller helper
                string currentInstaller = Process.GetCurrentProcess().MainModule?.FileName;
                if (!string.IsNullOrEmpty(currentInstaller) && File.Exists(currentInstaller))
                {
                    string uninstallerPath = Path.Combine(targetDir, "Uninstall.exe");
                    if (!string.Equals(currentInstaller, uninstallerPath, StringComparison.OrdinalIgnoreCase))
                    {
                        File.Copy(currentInstaller, uninstallerPath, overwrite: true);
                    }
                }

                if (createDesktopShortcut)
                {
                    string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                    CreateShortcut(Path.Combine(desktop, "Screen Translator.lnk"), exePath, targetDir, "Экранный переводчик для Windows");
                }

                if (createStartMenuShortcut)
                {
                    string startMenu = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Screen Translator");
                    Directory.CreateDirectory(startMenu);
                    CreateShortcut(Path.Combine(startMenu, "Screen Translator.lnk"), exePath, targetDir, "Экранный переводчик для Windows");
                }

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

                using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true))
                {
                    key?.DeleteValue("ScreenTranslator", throwOnMissingValue: false);
                }

                string desktopLnk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Screen Translator.lnk");
                if (File.Exists(desktopLnk)) File.Delete(desktopLnk);

                string startMenuDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Screen Translator");
                if (Directory.Exists(startMenuDir)) Directory.Delete(startMenuDir, recursive: true);

                string exePath = Path.Combine(targetDir, "ScreenTranslator.exe");
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
            Title = "Установка Screen Translator";
            Width = 520;
            Height = 360;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            Background = new SolidColorBrush(Color.FromRgb(17, 19, 24));
            FontFamily = new FontFamily("Segoe UI");

            var outer = new Border
            {
                BorderBrush = new SolidColorBrush(Color.FromRgb(39, 45, 58)),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(24)
            };

            var stack = new StackPanel();
            outer.MouseLeftButtonDown += (s, e) => { try { DragMove(); } catch { } };

            var header = new TextBlock
            {
                Text = "Установка Screen Translator 1.0.0",
                Foreground = new SolidColorBrush(Color.FromRgb(241, 245, 249)),
                FontSize = 18,
                FontWeight = FontWeights.SemiBold
            };
            stack.Children.Add(header);

            var sub = new TextBlock
            {
                Text = "Экранный переводчик для Windows 10/11 с локальным OCR и визуальной подменой текста.",
                Foreground = new SolidColorBrush(Color.FromRgb(148, 163, 184)),
                FontSize = 12.5,
                Margin = new Thickness(0, 6, 0, 18),
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
                FontSize = 12.5,
                Margin = new Thickness(0, 0, 0, 14)
            };
            stack.Children.Add(dirBox);

            var cbDesktop = new CheckBox
            {
                Content = "Создать ярлык на Рабочем столе",
                IsChecked = true,
                Foreground = new SolidColorBrush(Color.FromRgb(226, 232, 240)),
                FontSize = 12.5,
                Margin = new Thickness(0, 0, 0, 8)
            };
            stack.Children.Add(cbDesktop);

            var cbStartMenu = new CheckBox
            {
                Content = "Создать ярлык в меню «Пуск»",
                IsChecked = true,
                Foreground = new SolidColorBrush(Color.FromRgb(226, 232, 240)),
                FontSize = 12.5,
                Margin = new Thickness(0, 0, 0, 8)
            };
            stack.Children.Add(cbStartMenu);

            var cbLaunch = new CheckBox
            {
                Content = "Запустить Screen Translator после завершения установки",
                IsChecked = true,
                Foreground = new SolidColorBrush(Color.FromRgb(226, 232, 240)),
                FontSize = 12.5,
                Margin = new Thickness(0, 0, 0, 22)
            };
            stack.Children.Add(cbLaunch);

            var statusText = new TextBlock
            {
                Foreground = new SolidColorBrush(Color.FromRgb(16, 185, 129)),
                FontSize = 12.5,
                Margin = new Thickness(0, 0, 0, 12)
            };
            stack.Children.Add(statusText);

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
                int code = SetupProgram.PerformInstall(
                    dirBox.Text.Trim(),
                    cbDesktop.IsChecked == true,
                    cbStartMenu.IsChecked == true,
                    cbLaunch.IsChecked == true);
                if (code == 0)
                {
                    Close();
                }
                else
                {
                    statusText.Foreground = new SolidColorBrush(Color.FromRgb(239, 68, 68));
                    statusText.Text = "Ошибка установки. Проверьте права доступа к выбранной папке.";
                }
            });
            btnRow.Children.Add(installBtn);

            stack.Children.Add(btnRow);
            outer.Child = stack;
            Content = outer;
        }

        private static Border CreateButton(string text, Color bg, Action onClick)
        {
            var b = new Border
            {
                Background = new SolidColorBrush(bg),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(16, 8, 16, 8),
                Cursor = Cursors.Hand,
                Child = new TextBlock
                {
                    Text = text,
                    Foreground = Brushes.White,
                    FontSize = 13,
                    FontWeight = FontWeights.Medium
                }
            };
            b.MouseLeftButtonUp += (s, e) => onClick?.Invoke();
            return b;
        }
    }
}
