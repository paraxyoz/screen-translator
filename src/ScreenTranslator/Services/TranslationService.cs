using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using ScreenTranslator.Models;

namespace ScreenTranslator.Services
{
    public sealed class TranslationService
    {
        private readonly LoggingService _logger;
        private readonly SettingsService _settings;
        private readonly TranslationCache _cache;
        private static readonly HttpClient SharedHttpClient = CreateHttpClient();

        public TranslationService(LoggingService logger, SettingsService settings, TranslationCache cache)
        {
            _logger = logger;
            _settings = settings;
            _cache = cache;
        }

        private static HttpClient CreateHttpClient()
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            var handler = new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
            };
            var client = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(30)
            };
            client.DefaultRequestHeaders.TryAddWithoutValidation(
                "User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) ScreenTranslator/1.0");
            return client;
        }

        public async Task<TranslationResult> TranslateRecognitionAsync(OcrRecognitionResult ocr, CancellationToken ct)
        {
            if (ocr == null || !ocr.Success || string.IsNullOrWhiteSpace(ocr.FullText))
            {
                return new TranslationResult
                {
                    Success = false,
                    ErrorMessage = ocr?.ErrorMessage ?? "Нет текста для перевода."
                };
            }

            string sourceLang = _settings.Current.SourceLanguage ?? "auto";
            string targetLang = _settings.Current.TargetLanguage ?? "ru";

            var result = await TranslateAsync(ocr.FullText, sourceLang, targetLang, ct);
            if (!result.Success)
            {
                return result;
            }

            PopulateLineTranslations(ocr.Lines, result);
            return result;
        }

        public async Task<TranslationResult> TranslateAsync(
            string text,
            string sourceLang,
            string targetLang,
            CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return new TranslationResult
                {
                    Success = false,
                    ErrorMessage = "Пустой текст для перевода."
                };
            }

            var provider = _settings.Current.Provider;
            string src = string.IsNullOrWhiteSpace(sourceLang) ? "auto" : sourceLang.Trim();
            string tgt = string.IsNullOrWhiteSpace(targetLang) ? "ru" : targetLang.Trim();

            _cache.MaxEntries = Math.Max(10, _settings.Current.MaxCacheEntries);
            if (_settings.Current.EnableTranslationCache &&
                _cache.TryGet(provider, src, tgt, text, out var cached))
            {
                _logger?.Info("TranslationService", $"Перевод получен из кэша ({provider}).");
                return cached;
            }

            int timeoutSec = Math.Max(2, _settings.Current.RequestTimeoutSeconds);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSec));

            var sw = Stopwatch.StartNew();
            try
            {
                TranslationResult res;
                switch (provider)
                {
                    case TranslationProviderType.DeepL:
                        res = await TranslateWithDeepLAsync(text, src, tgt, timeoutCts.Token);
                        break;
                    case TranslationProviderType.OpenAiCompatible:
                        res = await TranslateWithOpenAiAsync(text, src, tgt, timeoutCts.Token);
                        break;
                    case TranslationProviderType.MyMemory:
                        res = await TranslateWithMyMemoryAsync(text, src, tgt, timeoutCts.Token);
                        break;
                    case TranslationProviderType.GoogleGtx:
                    default:
                        res = await TranslateWithGoogleGtxAsync(text, src, tgt, timeoutCts.Token);
                        break;
                }

                sw.Stop();
                res.ElapsedMs = sw.ElapsedMilliseconds;
                res.Provider = provider;
                res.SourceText = text;
                res.TargetLanguage = tgt;

                if (res.Success && _settings.Current.EnableTranslationCache)
                {
                    _cache.Put(provider, src, tgt, text, res);
                }

                if (res.Success)
                {
                    _logger?.Info("TranslationService", $"Перевод ({provider}, {res.DetectedSourceLanguage}->{tgt}) выполнен за {res.ElapsedMs} мс.");
                }
                else
                {
                    _logger?.Warn("TranslationService", $"Ошибка перевода ({provider}): {res.ErrorMessage}");
                }

                return res;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                sw.Stop();
                string msg = $"Превышено время ожидания ответа от сервиса перевода ({timeoutSec} сек.). Проверьте подключение к сети.";
                _logger?.Warn("TranslationService", msg);
                return new TranslationResult
                {
                    Success = false,
                    Provider = provider,
                    SourceText = text,
                    TargetLanguage = tgt,
                    ElapsedMs = sw.ElapsedMilliseconds,
                    ErrorMessage = msg
                };
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (HttpRequestException ex)
            {
                sw.Stop();
                string msg = "Сетевая ошибка при обращении к сервису перевода: " + ex.Message;
                _logger?.Error("TranslationService", msg, ex);
                return new TranslationResult
                {
                    Success = false,
                    Provider = provider,
                    SourceText = text,
                    TargetLanguage = tgt,
                    ElapsedMs = sw.ElapsedMilliseconds,
                    ErrorMessage = msg
                };
            }
            catch (Exception ex)
            {
                sw.Stop();
                string msg = "Не удалось выполнить перевод: " + ex.Message;
                _logger?.Error("TranslationService", msg, ex);
                return new TranslationResult
                {
                    Success = false,
                    Provider = provider,
                    SourceText = text,
                    TargetLanguage = tgt,
                    ElapsedMs = sw.ElapsedMilliseconds,
                    ErrorMessage = msg
                };
            }
        }

        private async Task<TranslationResult> TranslateWithGoogleGtxAsync(string text, string src, string tgt, CancellationToken ct)
        {
            string sl = NormalizeGoogleLang(src, allowAuto: true);
            string tl = NormalizeGoogleLang(tgt, allowAuto: false);
            string encoded = Uri.EscapeDataString(text);
            string url = $"https://translate.googleapis.com/translate_a/single?client=gtx&sl={sl}&tl={tl}&dt=t&q={encoded}";

            string json = await SendWithRetryAsync(() => new HttpRequestMessage(HttpMethod.Get, url), ct);
            var serializer = new JavaScriptSerializer();
            object rootObj = serializer.DeserializeObject(json);

            if (rootObj is object[] rootArr && rootArr.Length > 0 && rootArr[0] is object[] sentences)
            {
                var sb = new StringBuilder();
                foreach (var item in sentences)
                {
                    if (item is object[] seg && seg.Length > 0 && seg[0] != null)
                    {
                        sb.Append(seg[0].ToString());
                    }
                }

                string detected = src;
                if (rootArr.Length > 2 && rootArr[2] is string detStr && !string.IsNullOrWhiteSpace(detStr))
                {
                    detected = detStr;
                }

                string translated = sb.ToString().Trim();
                if (!string.IsNullOrWhiteSpace(translated))
                {
                    return new TranslationResult
                    {
                        Success = true,
                        TranslatedText = translated,
                        DetectedSourceLanguage = detected
                    };
                }
            }

            return new TranslationResult
            {
                Success = false,
                ErrorMessage = "Сервис Google Translate вернул пустой или некорректный ответ."
            };
        }

        private async Task<TranslationResult> TranslateWithMyMemoryAsync(string text, string src, string tgt, CancellationToken ct)
        {
            string resolvedSrc = src;
            if (string.Equals(resolvedSrc, "auto", StringComparison.OrdinalIgnoreCase))
            {
                resolvedSrc = DetectScriptLanguage(text, tgt);
            }

            string langPair = $"{NormalizeGoogleLang(resolvedSrc, false)}|{NormalizeGoogleLang(tgt, false)}";
            string url = $"https://api.mymemory.translated.net/get?q={Uri.EscapeDataString(text)}&langpair={Uri.EscapeDataString(langPair)}";
            if (!string.IsNullOrWhiteSpace(_settings.Current.MyMemoryEmail))
            {
                url += "&de=" + Uri.EscapeDataString(_settings.Current.MyMemoryEmail.Trim());
            }

            string json = await SendWithRetryAsync(() => new HttpRequestMessage(HttpMethod.Get, url), ct);
            var serializer = new JavaScriptSerializer();
            if (serializer.DeserializeObject(json) is IDictionary<string, object> dict &&
                dict.TryGetValue("responseData", out object rdObj) &&
                rdObj is IDictionary<string, object> rd &&
                rd.TryGetValue("translatedText", out object transObj) &&
                transObj != null)
            {
                string translated = WebUtility.HtmlDecode(transObj.ToString().Trim());
                if (!string.IsNullOrWhiteSpace(translated))
                {
                    return new TranslationResult
                    {
                        Success = true,
                        TranslatedText = translated,
                        DetectedSourceLanguage = resolvedSrc
                    };
                }
            }

            return new TranslationResult
            {
                Success = false,
                ErrorMessage = "Сервис MyMemory не смог выполнить перевод для указанной языковой пары."
            };
        }

        private async Task<TranslationResult> TranslateWithDeepLAsync(string text, string src, string tgt, CancellationToken ct)
        {
            string apiKey = _settings.GetDeepLApiKey();
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                return new TranslationResult
                {
                    Success = false,
                    ErrorMessage = "Для провайдера DeepL требуется API-ключ. Укажите ключ в разделе «Перевод» или выберите бесплатный провайдер Google Translate."
                };
            }

            string baseUrl = _settings.Current.DeepLUseFreeEndpoint
                ? "https://api-free.deepl.com/v2/translate"
                : "https://api.deepl.com/v2/translate";

            var payload = new Dictionary<string, object>
            {
                ["text"] = new[] { text },
                ["target_lang"] = NormalizeDeepLLang(tgt)
            };
            if (!string.Equals(src, "auto", StringComparison.OrdinalIgnoreCase))
            {
                payload["source_lang"] = NormalizeDeepLLang(src);
            }

            var serializer = new JavaScriptSerializer();
            string bodyJson = serializer.Serialize(payload);

            string responseJson = await SendWithRetryAsync(() =>
            {
                var req = new HttpRequestMessage(HttpMethod.Post, baseUrl);
                req.Headers.TryAddWithoutValidation("Authorization", $"DeepL-Auth-Key {apiKey}");
                req.Content = new StringContent(bodyJson, Encoding.UTF8, "application/json");
                return req;
            }, ct);

            if (serializer.DeserializeObject(responseJson) is IDictionary<string, object> dict &&
                dict.TryGetValue("translations", out object trObj) &&
                trObj is object[] trArr &&
                trArr.Length > 0 &&
                trArr[0] is IDictionary<string, object> first)
            {
                string translated = first.TryGetValue("text", out object tVal) ? tVal?.ToString() : string.Empty;
                string det = first.TryGetValue("detected_source_language", out object dVal) ? dVal?.ToString()?.ToLowerInvariant() : src;
                if (!string.IsNullOrWhiteSpace(translated))
                {
                    return new TranslationResult
                    {
                        Success = true,
                        TranslatedText = translated.Trim(),
                        DetectedSourceLanguage = det ?? src
                    };
                }
            }

            return new TranslationResult
            {
                Success = false,
                ErrorMessage = "DeepL API вернул пустой ответ. Проверьте корректность API-ключа и лимиты."
            };
        }

        private async Task<TranslationResult> TranslateWithOpenAiAsync(string text, string src, string tgt, CancellationToken ct)
        {
            string apiKey = _settings.GetOpenAiApiKey();
            string endpoint = string.IsNullOrWhiteSpace(_settings.Current.OpenAiEndpoint)
                ? "https://api.openai.com/v1/chat/completions"
                : _settings.Current.OpenAiEndpoint.Trim();

            bool isLocalEndpoint = endpoint.IndexOf("localhost", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                   endpoint.IndexOf("127.0.0.1", StringComparison.OrdinalIgnoreCase) >= 0;

            if (string.IsNullOrWhiteSpace(apiKey) && !isLocalEndpoint)
            {
                return new TranslationResult
                {
                    Success = false,
                    ErrorMessage = "Для облачного провайдера OpenAI / LLM требуется API-ключ. Укажите ключ в разделе «Перевод» или используйте локальный сервер."
                };
            }

            string model = string.IsNullOrWhiteSpace(_settings.Current.OpenAiModel) ? "gpt-4o-mini" : _settings.Current.OpenAiModel.Trim();
            string srcHint = string.Equals(src, "auto", StringComparison.OrdinalIgnoreCase) ? "automatically detected language" : src;

            var payload = new Dictionary<string, object>
            {
                ["model"] = model,
                ["temperature"] = 0.1,
                ["messages"] = new object[]
                {
                    new Dictionary<string, object>
                    {
                        ["role"] = "system",
                        ["content"] = $"You are a professional software UI and screen text translator. Translate from {srcHint} to language code '{tgt}'. Output ONLY the translated text without quotes or explanations, preserving the exact number of lines."
                    },
                    new Dictionary<string, object>
                    {
                        ["role"] = "user",
                        ["content"] = text
                    }
                }
            };

            var serializer = new JavaScriptSerializer();
            string bodyJson = serializer.Serialize(payload);

            string responseJson = await SendWithRetryAsync(() =>
            {
                var req = new HttpRequestMessage(HttpMethod.Post, endpoint);
                if (!string.IsNullOrWhiteSpace(apiKey))
                {
                    req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {apiKey}");
                }
                req.Content = new StringContent(bodyJson, Encoding.UTF8, "application/json");
                return req;
            }, ct);

            if (serializer.DeserializeObject(responseJson) is IDictionary<string, object> dict &&
                dict.TryGetValue("choices", out object choicesObj) &&
                choicesObj is object[] choices &&
                choices.Length > 0 &&
                choices[0] is IDictionary<string, object> choice0 &&
                choice0.TryGetValue("message", out object msgObj) &&
                msgObj is IDictionary<string, object> msg &&
                msg.TryGetValue("content", out object contentObj) &&
                contentObj != null)
            {
                string translated = contentObj.ToString().Trim();
                if (!string.IsNullOrWhiteSpace(translated))
                {
                    return new TranslationResult
                    {
                        Success = true,
                        TranslatedText = translated,
                        DetectedSourceLanguage = src
                    };
                }
            }

            return new TranslationResult
            {
                Success = false,
                ErrorMessage = "Совместимый LLM-провайдер вернул некорректный ответ."
            };
        }

        private async Task<string> SendWithRetryAsync(Func<HttpRequestMessage> requestFactory, CancellationToken ct)
        {
            const int maxAttempts = 2;
            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                using var req = requestFactory();
                using var resp = await SharedHttpClient.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct);

                if (resp.IsSuccessStatusCode)
                {
                    return await resp.Content.ReadAsStringAsync();
                }

                int status = (int)resp.StatusCode;
                bool isTransient = status == 429 || status == 502 || status == 503 || status == 504;
                if (isTransient && attempt < maxAttempts)
                {
                    await Task.Delay(350, ct);
                    continue;
                }

                if (status == 401 || status == 403)
                {
                    throw new InvalidOperationException($"Отказ в доступе (HTTP {status}). Проверьте правильность API-ключа в настройках.");
                }
                if (status == 429)
                {
                    throw new InvalidOperationException("Превышен лимит запросов к API перевода (HTTP 429). Подождите немного или смените провайдера.");
                }

                throw new HttpRequestException($"HTTP {status} ({resp.ReasonPhrase})");
            }

            throw new HttpRequestException("Не удалось выполнить сетевой запрос.");
        }

        public static void PopulateLineTranslations(List<RecognizedTextLine> ocrLines, TranslationResult translation)
        {
            if (ocrLines == null || ocrLines.Count == 0 || translation == null || string.IsNullOrWhiteSpace(translation.TranslatedText))
            {
                return;
            }

            string[] rawLines = translation.TranslatedText
                .Split(new[] { "\r\n", "\n" }, StringSplitOptions.None)
                .Select(l => l.Trim())
                .Where(l => !string.IsNullOrEmpty(l))
                .ToArray();

            translation.TranslatedLines.Clear();

            if (ocrLines.Count == 1)
            {
                string joined = string.Join(" ", rawLines);
                ocrLines[0].TranslatedText = joined;
                translation.TranslatedLines.Add(joined);
                return;
            }

            if (rawLines.Length == ocrLines.Count)
            {
                for (int i = 0; i < ocrLines.Count; i++)
                {
                    ocrLines[i].TranslatedText = rawLines[i];
                    translation.TranslatedLines.Add(rawLines[i]);
                }
                return;
            }

            // Proportional word distribution across OCR lines when line counts differ
            string fullTranslated = string.Join(" ", rawLines);
            string[] words = fullTranslated.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0)
            {
                return;
            }

            int totalSourceChars = Math.Max(1, ocrLines.Sum(l => Math.Max(1, l.Text.Length)));
            int wordIndex = 0;

            for (int i = 0; i < ocrLines.Count; i++)
            {
                if (i == ocrLines.Count - 1)
                {
                    string remainder = wordIndex < words.Length
                        ? string.Join(" ", words.Skip(wordIndex))
                        : string.Empty;
                    ocrLines[i].TranslatedText = remainder;
                    translation.TranslatedLines.Add(remainder);
                }
                else
                {
                    double ratio = (double)Math.Max(1, ocrLines[i].Text.Length) / totalSourceChars;
                    int takeCount = Math.Max(1, (int)Math.Round(words.Length * ratio));
                    if (wordIndex + takeCount > words.Length)
                    {
                        takeCount = Math.Max(0, words.Length - wordIndex);
                    }
                    string segment = takeCount > 0
                        ? string.Join(" ", words.Skip(wordIndex).Take(takeCount))
                        : string.Empty;
                    wordIndex += takeCount;
                    ocrLines[i].TranslatedText = segment;
                    translation.TranslatedLines.Add(segment);
                }
            }
        }

        private static string DetectScriptLanguage(string text, string targetLang)
        {
            int cyrillic = 0;
            int latin = 0;
            foreach (char c in text)
            {
                if (c >= 0x0400 && c <= 0x04FF) cyrillic++;
                else if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z')) latin++;
            }
            if (cyrillic > latin) return "ru";
            if (latin > 0) return "en";
            return string.Equals(targetLang, "ru", StringComparison.OrdinalIgnoreCase) ? "en" : "ru";
        }

        private static string NormalizeGoogleLang(string code, bool allowAuto)
        {
            if (string.IsNullOrWhiteSpace(code)) return allowAuto ? "auto" : "ru";
            string c = code.Trim().ToLowerInvariant();
            if (c == "auto") return allowAuto ? "auto" : "en";
            if (c.StartsWith("en")) return "en";
            if (c.StartsWith("ru")) return "ru";
            if (c.StartsWith("de")) return "de";
            if (c.StartsWith("fr")) return "fr";
            if (c.StartsWith("es")) return "es";
            if (c.StartsWith("zh")) return "zh-CN";
            if (c.StartsWith("ja")) return "ja";
            if (c.StartsWith("ko")) return "ko";
            if (c.StartsWith("uk")) return "uk";
            if (c.StartsWith("tr")) return "tr";
            return c;
        }

        private static string NormalizeDeepLLang(string code)
        {
            string c = NormalizeGoogleLang(code, false).ToUpperInvariant();
            if (c == "ZH-CN") return "ZH";
            return c;
        }
    }
}
