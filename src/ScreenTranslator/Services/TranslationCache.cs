using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using ScreenTranslator.Models;

namespace ScreenTranslator.Services
{
    public sealed class TranslationCache
    {
        private sealed class CacheNode
        {
            public string Key { get; set; }
            public TranslationResult Value { get; set; }
            public DateTime CreatedUtc { get; set; }
        }

        private readonly object _sync = new object();
        private readonly Dictionary<string, LinkedListNode<CacheNode>> _map;
        private readonly LinkedList<CacheNode> _lruList;
        private readonly TimeSpan _ttl;
        private static readonly Regex WhitespaceRegex = new Regex(@"\s+", RegexOptions.Compiled);

        public int MaxEntries { get; set; }
        public int HitCount { get; private set; }
        public int MissCount { get; private set; }

        public int Count
        {
            get
            {
                lock (_sync)
                {
                    return _map.Count;
                }
            }
        }

        public TranslationCache(int maxEntries = 500, TimeSpan? ttl = null)
        {
            MaxEntries = Math.Max(1, maxEntries);
            _ttl = ttl ?? TimeSpan.FromHours(6);
            _map = new Dictionary<string, LinkedListNode<CacheNode>>(StringComparer.Ordinal);
            _lruList = new LinkedList<CacheNode>();
        }

        public static string BuildNormalizedKey(TranslationProviderType provider, string sourceLang, string targetLang, string text)
        {
            string normalizedText = WhitespaceRegex.Replace((text ?? string.Empty).Trim(), " ");
            string src = (sourceLang ?? "auto").Trim().ToLowerInvariant();
            string tgt = (targetLang ?? "ru").Trim().ToLowerInvariant();
            return $"{provider}|{src}|{tgt}|{normalizedText}";
        }

        public bool TryGet(TranslationProviderType provider, string sourceLang, string targetLang, string text, out TranslationResult result)
        {
            result = null;
            if (string.IsNullOrWhiteSpace(text)) return false;

            string key = BuildNormalizedKey(provider, sourceLang, targetLang, text);
            lock (_sync)
            {
                if (_map.TryGetValue(key, out var node))
                {
                    if (DateTime.UtcNow - node.Value.CreatedUtc > _ttl)
                    {
                        _lruList.Remove(node);
                        _map.Remove(key);
                        MissCount++;
                        return false;
                    }

                    _lruList.Remove(node);
                    _lruList.AddFirst(node);
                    HitCount++;

                    var cached = node.Value.Value;
                    result = new TranslationResult
                    {
                        Success = cached.Success,
                        SourceText = cached.SourceText,
                        TranslatedText = cached.TranslatedText,
                        TranslatedLines = new List<string>(cached.TranslatedLines),
                        DetectedSourceLanguage = cached.DetectedSourceLanguage,
                        TargetLanguage = cached.TargetLanguage,
                        Provider = cached.Provider,
                        FromCache = true,
                        ElapsedMs = 0
                    };
                    return true;
                }

                MissCount++;
                return false;
            }
        }

        public void Put(TranslationProviderType provider, string sourceLang, string targetLang, string text, TranslationResult result)
        {
            if (string.IsNullOrWhiteSpace(text) || result == null || !result.Success) return;

            string key = BuildNormalizedKey(provider, sourceLang, targetLang, text);
            lock (_sync)
            {
                if (_map.TryGetValue(key, out var existing))
                {
                    existing.Value.Value = CloneResult(result);
                    existing.Value.CreatedUtc = DateTime.UtcNow;
                    _lruList.Remove(existing);
                    _lruList.AddFirst(existing);
                    return;
                }

                while (_map.Count >= MaxEntries && _lruList.Last != null)
                {
                    var last = _lruList.Last;
                    _lruList.RemoveLast();
                    _map.Remove(last.Value.Key);
                }

                var cacheNode = new CacheNode
                {
                    Key = key,
                    Value = CloneResult(result),
                    CreatedUtc = DateTime.UtcNow
                };
                var listNode = new LinkedListNode<CacheNode>(cacheNode);
                _lruList.AddFirst(listNode);
                _map[key] = listNode;
            }
        }

        public void Clear()
        {
            lock (_sync)
            {
                _map.Clear();
                _lruList.Clear();
                HitCount = 0;
                MissCount = 0;
            }
        }

        private static TranslationResult CloneResult(TranslationResult r)
        {
            return new TranslationResult
            {
                Success = r.Success,
                SourceText = r.SourceText,
                TranslatedText = r.TranslatedText,
                TranslatedLines = r.TranslatedLines != null ? new List<string>(r.TranslatedLines) : new List<string>(),
                DetectedSourceLanguage = r.DetectedSourceLanguage,
                TargetLanguage = r.TargetLanguage,
                Provider = r.Provider,
                FromCache = false,
                ElapsedMs = r.ElapsedMs
            };
        }
    }
}
