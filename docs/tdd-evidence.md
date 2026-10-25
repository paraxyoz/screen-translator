# TDD Evidence Report — Screen Translator

## 1. Source Plan & User Journeys
- **Journey 1**: As a Windows user, I want to hover over screen text or select a rectangular region and translate it in-place over the original text using local Windows OCR.
- **Journey 2**: As a privacy-conscious user, I want my API keys encrypted via Windows DPAPI and scrubbed from all diagnostic logs.
- **Journey 3**: As a user on a multi-monitor setup (including negative monitor coordinates), I want accurate screen capture, background color sampling, and LRU translation caching.

## 2. RED -> GREEN Validation Summary
- **RED Checkpoint**: Commit `839d63d` (`test: add unit and integration test suite for core services (RED)`) — compiled `ScreenTranslator.Tests.exe` and executed 10 core service tests (`Total: 10 | Passed: 0 | Failed: 10`).
- **GREEN Checkpoint**: Commit `445f8de` (`feat: implement core capture, OCR, translation, cache, settings, and logging services (GREEN)`) — implemented services and verified `Total: 10 | Passed: 10 | Failed: 0`, then expanded to 14 unit/integration tests + live E2E self-test (`ScreenTranslator.exe --self-test`).

## 3. Test Specification Matrix

| # | What is guaranteed | Test target | Type | Result | Evidence |
|---|--------------------|-------------|------|--------|----------|
| 1 | Whitespace-normalized keys hit LRU cache and set `FromCache=true` | `TestTranslationCacheNormalization` | unit | PASS | `ScreenTranslator.Tests.exe` |
| 2 | Least-recently-used entry is evicted when cache reaches capacity | `TestTranslationCacheLruEviction` | unit | PASS | `ScreenTranslator.Tests.exe` |
| 3 | Bearer tokens, `sk-` tokens, and `apiKey=` parameters are scrubbed from logs | `TestLoggingServiceRedaction` | unit | PASS | `ScreenTranslator.Tests.exe` |
| 4 | DeepL and OpenAI keys are encrypted with Windows DPAPI and never stored in plaintext JSON | `TestSettingsServiceDpapiRoundtrip` | integration | PASS | `ScreenTranslator.Tests.exe` |
| 5 | Solid dark and light backgrounds yield accurate background/foreground contrast pairs | `TestScreenCaptureStyleSampling` | unit | PASS | `ScreenTranslator.Tests.exe` |
| 6 | High-variance noisy backgrounds are flagged as `IsComplexBackground=true` | `TestScreenCaptureComplexBackground` | unit | PASS | `ScreenTranslator.Tests.exe` |
| 7 | Local `Windows.Media.Ocr` recognizes English (`en-US`) and Russian (`ru`) bitmaps offline | `TestLocalOcrEnglishAndRussian` | integration | PASS | `ScreenTranslator.Tests.exe` |
| 8 | Cursor capture detects lines clipped near capture box edges for auto-expansion | `TestAdaptiveCursorExpansionLogic` | unit | PASS | `ScreenTranslator.Tests.exe` |
| 9 | Missing DeepL API key returns actionable Russian error message without crashing | `TestTranslationMissingKeyError` | unit | PASS | `ScreenTranslator.Tests.exe` |
| 10 | Repeated identical translation requests return cached translation in 0 ms | `TestTranslationCacheHit` | integration | PASS | `ScreenTranslator.Tests.exe` |
| 11 | Multi-line translations align with OCR line bounding boxes | `TestMultilineAlignment` | unit | PASS | `ScreenTranslator.Tests.exe` |
| 12 | Negative multi-monitor coordinates (`X=-1920`) are preserved within virtual screen bounds | `TestMultiMonitorNegativeCoordinates` | unit | PASS | `ScreenTranslator.Tests.exe` |
| 13 | Win32 modifiers and virtual keys format into human-readable shortcut strings | `TestHotkeyFormatting` | unit | PASS | `ScreenTranslator.Tests.exe` |
| 14 | GDI+ vector tray icon generates valid 32x32 icon handles | `TestTrayIconCreation` | unit | PASS | `ScreenTranslator.Tests.exe` |
| 15 | Full E2E capture -> local OCR -> live translation -> Overlay display -> `WDA_EXCLUDEFROMCAPTURE` anti-self-capture -> Hide/Restore | `ScreenTranslator.exe --self-test` | E2E | PASS | `self_test_report.txt` |
