using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.Serialization;

namespace ScreenTranslator.Models
{
    public enum TranslationMode
    {
        UnderCursor,
        RegionSelection,
        RepeatLast
    }

    public enum TranslationProviderType
    {
        GoogleGtx,
        MyMemory,
        DeepL,
        OpenAiCompatible
    }

    public enum OverlayBackgroundMode
    {
        AutoSample,
        DarkTranslucent,
        LightTranslucent,
        SolidDark
    }

    public enum AppTheme
    {
        Dark,
        Light
    }

    public sealed class SampledRegionStyle
    {
        public Color BackgroundColor { get; set; } = Color.FromArgb(24, 27, 34);
        public Color ForegroundColor { get; set; } = Color.FromArgb(241, 245, 249);
        public double BackgroundVariance { get; set; }
        public bool IsComplexBackground => BackgroundVariance > 28.0;
    }

    public sealed class RecognizedWordRect
    {
        public string Text { get; set; } = string.Empty;
        public Rectangle BoundingBox { get; set; }
    }

    public sealed class RecognizedTextLine
    {
        public string Text { get; set; } = string.Empty;
        public string TranslatedText { get; set; } = string.Empty;
        public Rectangle BoundingBox { get; set; }
        public Rectangle ScreenBoundingBox { get; set; }
        public SampledRegionStyle Style { get; set; } = new SampledRegionStyle();
        public List<RecognizedWordRect> Words { get; set; } = new List<RecognizedWordRect>();
    }

    public sealed class OcrRecognitionResult
    {
        public bool Success { get; set; }
        public string FullText { get; set; } = string.Empty;
        public string LanguageUsed { get; set; } = string.Empty;
        public Rectangle CapturedScreenRect { get; set; }
        public List<RecognizedTextLine> Lines { get; set; } = new List<RecognizedTextLine>();
        public Bitmap OriginalCapture { get; set; }
        public SampledRegionStyle OverallStyle { get; set; } = new SampledRegionStyle();
        public long CaptureElapsedMs { get; set; }
        public long OcrElapsedMs { get; set; }
        public bool WasExpanded { get; set; }
        public string ErrorMessage { get; set; } = string.Empty;
    }

    public sealed class TranslationResult
    {
        public bool Success { get; set; }
        public string SourceText { get; set; } = string.Empty;
        public string TranslatedText { get; set; } = string.Empty;
        public List<string> TranslatedLines { get; set; } = new List<string>();
        public string DetectedSourceLanguage { get; set; } = string.Empty;
        public string TargetLanguage { get; set; } = string.Empty;
        public TranslationProviderType Provider { get; set; }
        public bool FromCache { get; set; }
        public long ElapsedMs { get; set; }
        public string ErrorMessage { get; set; } = string.Empty;
    }

    public sealed class TranslationHistoryEntry
    {
        public DateTime Timestamp { get; set; } = DateTime.Now;
        public string SourceLanguage { get; set; } = "auto";
        public string TargetLanguage { get; set; } = "ru";
        public string SourceText { get; set; } = string.Empty;
        public string TranslatedText { get; set; } = string.Empty;
        public TranslationProviderType Provider { get; set; }
        public long CaptureMs { get; set; }
        public long OcrMs { get; set; }
        public long TranslateMs { get; set; }
        public bool FromCache { get; set; }
    }

    [DataContract]
    public sealed class HotkeyConfig
    {
        [DataMember] public string ActionId { get; set; } = string.Empty;
        [DataMember] public string DisplayName { get; set; } = string.Empty;
        [DataMember] public uint Modifiers { get; set; }
        [DataMember] public uint VirtualKey { get; set; }

        [IgnoreDataMember] public bool IsRegistered { get; set; }
        [IgnoreDataMember] public string StatusMessage { get; set; } = "Не зарегистрировано";

        public string ToReadableString()
        {
            var parts = new List<string>();
            if ((Modifiers & Win32.NativeMethods.MOD_CONTROL) != 0) parts.Add("Ctrl");
            if ((Modifiers & Win32.NativeMethods.MOD_SHIFT) != 0) parts.Add("Shift");
            if ((Modifiers & Win32.NativeMethods.MOD_ALT) != 0) parts.Add("Alt");
            if ((Modifiers & Win32.NativeMethods.MOD_WIN) != 0) parts.Add("Win");

            string keyName = VirtualKeyToString(VirtualKey);
            if (!string.IsNullOrEmpty(keyName))
            {
                parts.Add(keyName);
            }
            return string.Join(" + ", parts);
        }

        public static string VirtualKeyToString(uint vk)
        {
            if (vk >= 'A' && vk <= 'Z') return ((char)vk).ToString();
            if (vk >= '0' && vk <= '9') return ((char)vk).ToString();
            if (vk >= 0x70 && vk <= 0x7B) return "F" + (vk - 0x70 + 1);
            switch (vk)
            {
                case 0x1B: return "Esc";
                case 0x20: return "Space";
                case 0x09: return "Tab";
                case 0xC0: return "`";
                default:
                    var kc = (System.Windows.Forms.Keys)vk;
                    return kc.ToString();
            }
        }
    }

    [DataContract]
    public sealed class AppSettings
    {
        // 1. Translation
        [DataMember] public string SourceLanguage { get; set; } = "auto";
        [DataMember] public string TargetLanguage { get; set; } = "ru";
        [DataMember] public string ProviderName { get; set; } = TranslationProviderType.GoogleGtx.ToString();
        [DataMember] public string EncryptedDeepLApiKey { get; set; } = string.Empty;
        [DataMember] public bool DeepLUseFreeEndpoint { get; set; } = true;
        [DataMember] public string EncryptedOpenAiApiKey { get; set; } = string.Empty;
        [DataMember] public string OpenAiEndpoint { get; set; } = "https://api.openai.com/v1/chat/completions";
        [DataMember] public string OpenAiModel { get; set; } = "gpt-4o-mini";
        [DataMember] public string MyMemoryEmail { get; set; } = string.Empty;
        [DataMember] public bool AutoCopyTranslation { get; set; } = false;
        [DataMember] public int AutoHideOverlaySeconds { get; set; } = 0;

        // 2. Hotkeys
        [DataMember] public HotkeyConfig CursorHotkey { get; set; } = new HotkeyConfig
        {
            ActionId = "TranslateCursor",
            DisplayName = "Перевести текст под курсором",
            Modifiers = Win32.NativeMethods.MOD_CONTROL | Win32.NativeMethods.MOD_SHIFT,
            VirtualKey = 'T'
        };

        [DataMember] public HotkeyConfig RegionHotkey { get; set; } = new HotkeyConfig
        {
            ActionId = "TranslateRegion",
            DisplayName = "Выделить область для перевода",
            Modifiers = Win32.NativeMethods.MOD_CONTROL | Win32.NativeMethods.MOD_SHIFT,
            VirtualKey = 'A'
        };

        [DataMember] public HotkeyConfig ToggleOverlayHotkey { get; set; } = new HotkeyConfig
        {
            ActionId = "ToggleOverlay",
            DisplayName = "Показать или скрыть текущий перевод",
            Modifiers = Win32.NativeMethods.MOD_CONTROL | Win32.NativeMethods.MOD_SHIFT,
            VirtualKey = 'H'
        };

        // 3. Appearance & Overlay
        [DataMember] public string ThemeName { get; set; } = AppTheme.Dark.ToString();
        [DataMember] public string OverlayBgModeName { get; set; } = OverlayBackgroundMode.AutoSample.ToString();
        [DataMember] public double OverlayOpacity { get; set; } = 0.95;
        [DataMember] public string OverlayFontFamily { get; set; } = "Segoe UI";
        [DataMember] public bool AutoFitFontSize { get; set; } = true;
        [DataMember] public double FixedFontSize { get; set; } = 15.0;
        [DataMember] public bool ShowOverlayToolbar { get; set; } = true;

        // 4. Performance & OCR
        [DataMember] public string PreferredOcrLanguage { get; set; } = "auto";
        [DataMember] public bool UpscaleSmallRegions { get; set; } = true;
        [DataMember] public int CursorCaptureWidth { get; set; } = 460;
        [DataMember] public int CursorCaptureHeight { get; set; } = 130;
        [DataMember] public bool AutoExpandCursorCapture { get; set; } = true;
        [DataMember] public bool EnableTranslationCache { get; set; } = true;
        [DataMember] public int MaxCacheEntries { get; set; } = 500;
        [DataMember] public int RequestTimeoutSeconds { get; set; } = 8;

        // 5. General & Privacy
        [DataMember] public bool LaunchOnStartup { get; set; } = false;
        [DataMember] public bool MinimizeToTrayOnClose { get; set; } = true;
        [DataMember] public bool SaveSessionHistory { get; set; } = true;
        [DataMember] public bool ExcludeOverlayFromCapture { get; set; } = true;

        [IgnoreDataMember]
        public TranslationProviderType Provider
        {
            get => Enum.TryParse<TranslationProviderType>(ProviderName, true, out var p) ? p : TranslationProviderType.GoogleGtx;
            set => ProviderName = value.ToString();
        }

        [IgnoreDataMember]
        public AppTheme Theme
        {
            get => Enum.TryParse<AppTheme>(ThemeName, true, out var t) ? t : AppTheme.Dark;
            set => ThemeName = value.ToString();
        }

        [IgnoreDataMember]
        public OverlayBackgroundMode OverlayBgMode
        {
            get => Enum.TryParse<OverlayBackgroundMode>(OverlayBgModeName, true, out var m) ? m : OverlayBackgroundMode.AutoSample;
            set => OverlayBgModeName = value.ToString();
        }
    }
}
