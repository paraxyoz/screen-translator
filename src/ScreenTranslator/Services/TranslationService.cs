using System.Threading;
using System.Threading.Tasks;
using ScreenTranslator.Models;

namespace ScreenTranslator.Services
{
    public sealed class TranslationService
    {
        public TranslationService(LoggingService logger, SettingsService settings, TranslationCache cache) { }
        public Task<TranslationResult> TranslateAsync(string text, string sourceLang, string targetLang, CancellationToken ct)
            => Task.FromResult(new TranslationResult { Success = false, ErrorMessage = "Not implemented" });
    }
}
