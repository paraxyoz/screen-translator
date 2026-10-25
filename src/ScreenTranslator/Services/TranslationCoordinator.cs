using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using ScreenTranslator.Models;
using ScreenTranslator.UI;
using ScreenTranslator.Win32;
using WpfPoint = System.Windows.Point;

namespace ScreenTranslator.Services
{
    public sealed class TranslationCoordinator : IDisposable
    {
        private readonly LoggingService _logger;
        private readonly SettingsService _settings;
        private readonly TranslationCache _cache;
        private readonly ScreenCaptureService _capture;
        private readonly TextRecognitionService _ocr;
        private readonly TranslationService _translator;
        private readonly OverlayService _overlay;
        private readonly HotkeyService _hotkeys;
        private readonly List<TranslationHistoryEntry> _history = new List<TranslationHistoryEntry>();

        private CancellationTokenSource _activeCts;
        private RegionSelectorWindow _activeSelector;
        private bool _isSelectingRegion;
        private Rectangle _lastRegionRect;
        private bool _hasLastRegion;

        public long LastCaptureMs { get; private set; }
        public long LastOcrMs { get; private set; }
        public long LastTranslateMs { get; private set; }
        public string LastStatusMessage { get; private set; } = "Готов к работе";
        public bool IsBusy { get; private set; }

        public IReadOnlyList<TranslationHistoryEntry> History => _history;

        public event Action StateChanged;
        public event Action<string, bool> NotificationMessage;

        public TranslationCoordinator(
            LoggingService logger,
            SettingsService settings,
            TranslationCache cache,
            ScreenCaptureService capture,
            TextRecognitionService ocr,
            TranslationService translator,
            OverlayService overlay,
            HotkeyService hotkeys)
        {
            _logger = logger;
            _settings = settings;
            _cache = cache;
            _capture = capture;
            _ocr = ocr;
            _translator = translator;
            _overlay = overlay;
            _hotkeys = hotkeys;

            _hotkeys.TranslateCursorRequested += () => _ = TranslateUnderCursorAsync();
            _hotkeys.TranslateRegionRequested += () => StartRegionSelection();
            _hotkeys.ToggleOverlayRequested += () => _overlay.ToggleOverlayVisibility();
            _hotkeys.EscapePressed += OnEscapePressed;
            _hotkeys.LeftMouseButtonDown += pt => _overlay.HandleGlobalLeftMouseDown(pt);
            _hotkeys.HotkeyConflictDetected += msg => NotificationMessage?.Invoke(msg, true);

            _overlay.RepeatTranslationRequested += () => _ = RepeatLastTranslationAsync();
            _overlay.SelectNewRegionRequested += () => StartRegionSelection();
            _overlay.OverlayVisibilityChanged += UpdateEscapeHookState;
            _overlay.ToolbarSettingChanged += () => StateChanged?.Invoke();
        }

        private void OnEscapePressed()
        {
            if (_isSelectingRegion && _activeSelector != null)
            {
                try { _activeSelector.CancelSelection(); } catch { }
                return;
            }

            if (_activeCts != null)
            {
                try { _activeCts.Cancel(); } catch { }
            }

            if (_overlay.IsOverlayVisible)
            {
                _overlay.CloseOverlay();
            }
        }

        private void UpdateEscapeHookState()
        {
            bool shouldHookEsc = _isSelectingRegion || _overlay.IsOverlayVisible || IsBusy;
            _hotkeys.SetEscapeHookEnabled(shouldHookEsc);
            StateChanged?.Invoke();
        }

        public async Task TranslateUnderCursorAsync()
        {
            if (_isSelectingRegion) return;

            if (!NativeMethods.GetCursorPos(out var pt))
            {
                return;
            }

            var cursorPos = new System.Drawing.Point(pt.X, pt.Y);
            var cts = ResetActiveOperation();
            IsBusy = true;
            LastStatusMessage = "Распознавание текста под курсором...";
            UpdateEscapeHookState();

            int w = Math.Max(180, _settings.Current.CursorCaptureWidth);
            int h = Math.Max(60, _settings.Current.CursorCaptureHeight);
            var previewRect = ScreenCaptureService.ClampToVirtualScreen(new Rectangle(cursorPos.X - w / 2, cursorPos.Y - h / 2, w, h));

            try
            {
                var ocrRes = await Task.Run(() => _ocr.CaptureAndRecognizeUnderCursorAsync(
                    cursorPos,
                    beforeCaptureAction: () => _overlay.PrepareForScreenCapture(),
                    afterCaptureAction: () => _overlay.RestoreAfterScreenCapture(),
                    cts.Token), cts.Token);

                _lastRegionRect = ocrRes.CapturedScreenRect;
                _hasLastRegion = true;
                LastCaptureMs = ocrRes.CaptureElapsedMs;
                LastOcrMs = ocrRes.OcrElapsedMs;

                if (!ocrRes.Success)
                {
                    LastStatusMessage = ocrRes.ErrorMessage;
                    _overlay.ShowErrorState(previewRect, ocrRes.ErrorMessage, allowRetry: true);
                    NotificationMessage?.Invoke(ocrRes.ErrorMessage, true);
                    return;
                }

                _overlay.ShowLoadingState(ocrRes.CapturedScreenRect, "Перевод распознанного текста...");
                await CompleteTranslationAndShowOverlayAsync(ocrRes, cts.Token);
            }
            catch (OperationCanceledException)
            {
                LastStatusMessage = "Операция отменена";
                _logger?.Info("TranslationCoordinator", "Перевод под курсором отменён.");
            }
            catch (Exception ex)
            {
                LastStatusMessage = "Ошибка: " + ex.Message;
                _logger?.Error("TranslationCoordinator", "Сбой при переводе под курсором", ex);
                _overlay.ShowErrorState(previewRect, "Ошибка перевода: " + ex.Message, allowRetry: true);
            }
            finally
            {
                IsBusy = false;
                UpdateEscapeHookState();
            }
        }

        public void StartRegionSelection()
        {
            if (_isSelectingRegion) return;

            CancelActiveOperation();
            _isSelectingRegion = true;
            _overlay.PrepareForScreenCapture();
            UpdateEscapeHookState();

            try
            {
                _activeSelector = new RegionSelectorWindow();
                bool? ok = _activeSelector.ShowDialog();
                var selected = _activeSelector.SelectedPhysicalRegion;
                _activeSelector = null;
                _isSelectingRegion = false;

                if (ok == true && selected.HasValue)
                {
                    _ = TranslateRegionRectAsync(selected.Value);
                }
                else
                {
                    _overlay.RestoreAfterScreenCapture();
                    UpdateEscapeHookState();
                }
            }
            catch (Exception ex)
            {
                _isSelectingRegion = false;
                _activeSelector = null;
                _overlay.RestoreAfterScreenCapture();
                UpdateEscapeHookState();
                _logger?.Error("TranslationCoordinator", "Ошибка выделения области", ex);
            }
        }

        public async Task TranslateRegionRectAsync(Rectangle physicalRect)
        {
            var cts = ResetActiveOperation();
            IsBusy = true;
            _lastRegionRect = physicalRect;
            _hasLastRegion = true;
            LastStatusMessage = "Распознавание выделенной области...";
            UpdateEscapeHookState();

            try
            {
                var ocrRes = await Task.Run(() => _ocr.CaptureAndRecognizeRegionAsync(
                    physicalRect,
                    beforeCaptureAction: () => _overlay.PrepareForScreenCapture(),
                    afterCaptureAction: () => _overlay.RestoreAfterScreenCapture(),
                    cts.Token), cts.Token);

                LastCaptureMs = ocrRes.CaptureElapsedMs;
                LastOcrMs = ocrRes.OcrElapsedMs;

                if (!ocrRes.Success)
                {
                    LastStatusMessage = ocrRes.ErrorMessage;
                    _overlay.ShowErrorState(physicalRect, ocrRes.ErrorMessage, allowRetry: true);
                    NotificationMessage?.Invoke(ocrRes.ErrorMessage, true);
                    return;
                }

                _overlay.ShowLoadingState(ocrRes.CapturedScreenRect, "Перевод распознанного текста...");
                await CompleteTranslationAndShowOverlayAsync(ocrRes, cts.Token);
            }
            catch (OperationCanceledException)
            {
                LastStatusMessage = "Перевод отменён";
            }
            catch (Exception ex)
            {
                LastStatusMessage = "Ошибка: " + ex.Message;
                _logger?.Error("TranslationCoordinator", "Сбой при переводе выделенной области", ex);
                _overlay.ShowErrorState(physicalRect, "Ошибка перевода: " + ex.Message, allowRetry: true);
            }
            finally
            {
                IsBusy = false;
                UpdateEscapeHookState();
            }
        }

        public async Task RepeatLastTranslationAsync()
        {
            if (_hasLastRegion && _lastRegionRect.Width > 8 && _lastRegionRect.Height > 8)
            {
                await TranslateRegionRectAsync(_lastRegionRect);
            }
            else
            {
                await TranslateUnderCursorAsync();
            }
        }

        private async Task CompleteTranslationAndShowOverlayAsync(OcrRecognitionResult ocrRes, CancellationToken ct)
        {
            var transRes = await _translator.TranslateRecognitionAsync(ocrRes, ct);
            LastTranslateMs = transRes.ElapsedMs;

            if (!transRes.Success)
            {
                LastStatusMessage = transRes.ErrorMessage;
                _overlay.ShowErrorState(ocrRes.CapturedScreenRect, transRes.ErrorMessage, allowRetry: true);
                NotificationMessage?.Invoke(transRes.ErrorMessage, true);
                return;
            }

            if (_settings.Current.AutoCopyTranslation && !string.IsNullOrWhiteSpace(transRes.TranslatedText))
            {
                try { Clipboard.SetText(transRes.TranslatedText); } catch { }
            }

            if (_settings.Current.SaveSessionHistory)
            {
                if (_history.Count >= 30) _history.RemoveAt(_history.Count - 1);
                _history.Insert(0, new TranslationHistoryEntry
                {
                    Timestamp = DateTime.Now,
                    SourceLanguage = string.IsNullOrWhiteSpace(transRes.DetectedSourceLanguage) ? ocrRes.LanguageUsed : transRes.DetectedSourceLanguage,
                    TargetLanguage = transRes.TargetLanguage,
                    SourceText = ocrRes.FullText,
                    TranslatedText = transRes.TranslatedText,
                    Provider = transRes.Provider,
                    CaptureMs = ocrRes.CaptureElapsedMs,
                    OcrMs = ocrRes.OcrElapsedMs,
                    TranslateMs = transRes.ElapsedMs,
                    FromCache = transRes.FromCache
                });
            }

            LastStatusMessage = transRes.FromCache
                ? $"Переведено (из кэша) — OCR: {ocrRes.OcrElapsedMs} мс"
                : $"Переведено за {ocrRes.CaptureElapsedMs + ocrRes.OcrElapsedMs + transRes.ElapsedMs} мс";

            _overlay.ShowTranslation(ocrRes, transRes);
            StateChanged?.Invoke();
        }

        public void ClearHistory()
        {
            _history.Clear();
            StateChanged?.Invoke();
        }

        private CancellationTokenSource ResetActiveOperation()
        {
            CancelActiveOperation();
            _activeCts = new CancellationTokenSource();
            return _activeCts;
        }

        private void CancelActiveOperation()
        {
            if (_activeCts != null)
            {
                try { _activeCts.Cancel(); } catch { }
                try { _activeCts.Dispose(); } catch { }
                _activeCts = null;
            }
        }

        public void Dispose()
        {
            CancelActiveOperation();
        }
    }
}
