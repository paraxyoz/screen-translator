using ScreenTranslator.Models;

namespace ScreenTranslator.Services
{
    public sealed class TranslationCache
    {
        public int HitCount => 0;
        public int MissCount => 0;
        public int Count => 0;
        public int MaxEntries { get; set; }
        public TranslationCache(int maxEntries = 500) { MaxEntries = maxEntries; }
        public bool TryGet(TranslationProviderType provider, string sourceLang, string targetLang, string text, out TranslationResult result)
        {
            result = null;
            return false;
        }
        public void Put(TranslationProviderType provider, string sourceLang, string targetLang, string text, TranslationResult result) { }
        public void Clear() { }
    }
}
