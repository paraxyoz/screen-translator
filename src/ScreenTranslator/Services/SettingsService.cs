using ScreenTranslator.Models;

namespace ScreenTranslator.Services
{
    public sealed class SettingsService
    {
        public AppSettings Current { get; private set; } = new AppSettings();
        public SettingsService(LoggingService logger, string customPath = null) { }
        public void Load() { }
        public void Save() { }
        public void SetDeepLApiKey(string plainKey) => Current.EncryptedDeepLApiKey = plainKey;
        public string GetDeepLApiKey() => string.Empty;
        public void SetOpenAiApiKey(string plainKey) => Current.EncryptedOpenAiApiKey = plainKey;
        public string GetOpenAiApiKey() => string.Empty;
        public void ApplyStartupSetting(bool enable) { }
    }
}
