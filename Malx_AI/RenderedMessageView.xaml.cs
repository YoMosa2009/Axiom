using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using Microsoft.Web.WebView2.Core;

namespace Malx_AI
{
    public partial class RenderedMessageView : UserControl
    {
        private const string KatexVirtualHostName = "katex.local";
        private const long RenderCacheByteLimit = 64L * 1024 * 1024;
        private const int RenderCacheEntryLimit = 64;
        private static readonly object RenderCacheGate = new();
        private static readonly Dictionary<string, CachedRender> RenderCache = new(StringComparer.Ordinal);
        private static readonly LinkedList<string> RenderCacheLru = new();
        private static long _renderCacheBytes;

        public static readonly DependencyProperty RawTextProperty = DependencyProperty.Register(
            nameof(RawText), typeof(string), typeof(RenderedMessageView),
            new PropertyMetadata(string.Empty, OnRawTextChanged));

        public static readonly DependencyProperty PlainTextProperty = DependencyProperty.Register(
            nameof(PlainText), typeof(string), typeof(RenderedMessageView),
            new PropertyMetadata(string.Empty, OnPlainTextChanged));

        public static readonly DependencyProperty IsHtmlRenderingEnabledProperty = DependencyProperty.Register(
            nameof(IsHtmlRenderingEnabled), typeof(bool), typeof(RenderedMessageView),
            new PropertyMetadata(true, OnIsHtmlRenderingEnabledChanged));

        public string RawText
        {
            get => (string)GetValue(RawTextProperty);
            set => SetValue(RawTextProperty, value);
        }

        public string PlainText
        {
            get => (string)GetValue(PlainTextProperty);
            set => SetValue(PlainTextProperty, value);
        }

        public bool IsHtmlRenderingEnabled
        {
            get => (bool)GetValue(IsHtmlRenderingEnabledProperty);
            set => SetValue(IsHtmlRenderingEnabledProperty, value);
        }

        private bool _initialized;
        private bool _initializationInProgress;
        private bool _isLoaded;
        private const double MinRenderHeight = 24;
        private const double MinRenderWidth = 120;
        private bool _hasMeasuredHeight;
        private string _lastRenderedHtml = string.Empty;
        private double _lastRenderWidth;
        private int _renderVersion;
        private bool _katexScriptsInjected;
        private bool _webViewReady;
        private bool _hasCapturedImage;
        private int _captureRequestVersion;
        private string _activeRenderCacheKey = string.Empty;
        public RenderedMessageView()
        {
            InitializeComponent();
            ResetRenderSurface();
            Loaded += RenderedMessageView_Loaded;
            SizeChanged += RenderedMessageView_SizeChanged;
        }

        protected override Size MeasureOverride(Size constraint)
        {
            Size measured = base.MeasureOverride(constraint);

            if (!IsHtmlRenderingEnabled)
                return measured;

            double availableWidth = constraint.Width;
            if (double.IsInfinity(availableWidth) || availableWidth <= 0)
                return measured;

            return new Size(Math.Max(measured.Width, availableWidth), measured.Height);
        }

        private async void RenderedMessageView_Loaded(object sender, RoutedEventArgs e)
        {
            _isLoaded = true;

            // A recycled ListBox row can restore an already-rendered KaTeX/Markdown image
            // synchronously. This prevents raw source from flashing while WebView2 warms up.
            Render();

            if (_initialized)
            {
                UpdatePresentationMode();
                Render();
                return;
            }

            if (IsHtmlRenderingEnabled)
                await EnsureBrowserInitializedAsync();
        }

        private void RenderedMessageView_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (!_initialized || !IsHtmlRenderingEnabled)
                return;

            double width = GetRenderWidth();
            if (width < MinRenderWidth)
                return;

            if (Math.Abs(width - _lastRenderWidth) > 6)
                Render();
        }

        private static void OnRawTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is RenderedMessageView v)
                v.Render();
        }

        private static void OnPlainTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is RenderedMessageView v)
                v.UpdatePresentationMode();
        }

        private static void OnIsHtmlRenderingEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is RenderedMessageView v)
            {
                v.UpdatePresentationMode();
                if (v.IsHtmlRenderingEnabled && v._isLoaded && !v._initialized)
                    _ = v.EnsureBrowserInitializedAsync();
                else if (v._initialized)
                    v.Render();
            }
        }

        private void Render()
        {
            if (!IsHtmlRenderingEnabled)
                return;

            double renderWidth = GetRenderWidth();
            if (renderWidth < MinRenderWidth)
                return;

            string html = MessageHtmlRenderer.BuildHtml(RawText ?? string.Empty);
            string cacheKey = BuildRenderCacheKey(html, renderWidth);
            if (TryGetCachedRender(cacheKey, out CachedRender? cached))
            {
                _lastRenderedHtml = html;
                _lastRenderWidth = renderWidth;
                _activeRenderCacheKey = cacheKey;
                ApplyCachedRender(cached!);
                return;
            }

            if (!_initialized)
            {
                if (_isLoaded && !_initializationInProgress)
                    _ = EnsureBrowserInitializedAsync();
                return;
            }

            if (string.Equals(_lastRenderedHtml, html, StringComparison.Ordinal)
                && _hasMeasuredHeight
                && Math.Abs(renderWidth - _lastRenderWidth) <= 6)
            {
                UpdatePresentationMode();
                return;
            }

            _lastRenderedHtml = html;
            _lastRenderWidth = renderWidth;
            _activeRenderCacheKey = cacheKey;
            _renderVersion++;
            ResetRenderSurface();
            Browser.Width = renderWidth;
            Browser.NavigateToString(html);
        }

        private void UpdatePresentationMode()
        {
            TextBlock? plainTextBlock = GetPlainTextBlock();
            if (!_initialized && !_hasCapturedImage)
            {
                if (plainTextBlock != null)
                    plainTextBlock.Text = PlainText ?? RawText ?? string.Empty;
                RenderedImage.Visibility = Visibility.Collapsed;
                Browser.Visibility = Visibility.Collapsed;
                return;
            }

            if (plainTextBlock != null)
                plainTextBlock.Text = PlainText ?? RawText ?? string.Empty;

            if (IsHtmlRenderingEnabled)
            {
                if (_hasCapturedImage)
                {
                    // Captured image is ready — hide the HWND, show the WPF image.
                    // This is the steady state: no airspace overlap.
                    Canvas? browserHost = GetBrowserHost();
                    if (browserHost != null)
                        browserHost.Visibility = Visibility.Collapsed;
                    Browser.Visibility = Visibility.Collapsed;
                    RenderedImage.Visibility = Visibility.Visible;
                    if (plainTextBlock != null)
                        plainTextBlock.Visibility = Visibility.Collapsed;
                }
                else if (_webViewReady && _hasMeasuredHeight)
                {
                    // Capture not yet done — keep the live browser visible briefly
                    // so Chromium stays active and renders KaTeX properly.
                    Canvas? browserHost = GetBrowserHost();
                    if (browserHost != null)
                    {
                        browserHost.Width = Math.Max(1, Browser.Width);
                        browserHost.Height = Math.Max(MinRenderHeight, Browser.Height);
                        browserHost.Visibility = Visibility.Visible;
                    }
                    Browser.Visibility = Visibility.Visible;
                    RenderedImage.Visibility = Visibility.Collapsed;
                    if (plainTextBlock != null)
                        plainTextBlock.Visibility = Visibility.Collapsed;
                }
                else
                {
                    // Still loading — show plain text, browser hidden.
                    Canvas? browserHost = GetBrowserHost();
                    if (browserHost != null)
                        browserHost.Visibility = Visibility.Collapsed;
                    Browser.Visibility = Visibility.Collapsed;
                    RenderedImage.Visibility = Visibility.Collapsed;
                    if (plainTextBlock != null)
                        plainTextBlock.Visibility = Visibility.Visible;
                }
            }
            else
            {
                Canvas? browserHost = GetBrowserHost();
                if (browserHost != null)
                    browserHost.Visibility = Visibility.Collapsed;
                RenderedImage.Visibility = Visibility.Collapsed;
                Browser.Visibility = Visibility.Collapsed;
                ResetRenderSurface();
                if (plainTextBlock != null)
                    plainTextBlock.Visibility = Visibility.Visible;
            }
        }

        private async Task EnsureBrowserInitializedAsync()
        {
            if (_initialized || _initializationInProgress || !IsHtmlRenderingEnabled)
                return;

            _initializationInProgress = true;

            try
            {
                var webViewEnvironment = await Microsoft.Web.WebView2.Core.CoreWebView2Environment.CreateAsync(
                    browserExecutableFolder: null,
                    userDataFolder: AppDataPaths.WebView2UserData,
                    options: WebView2GpuPolicy.CreateEnvironmentOptions());
                await Browser.EnsureCoreWebView2Async(webViewEnvironment);
                ConfigureKatexVirtualHostMapping();
                await WebView2OfflinePolicy.ConfigureAsync(Browser.CoreWebView2, KatexVirtualHostName);
                await InjectKatexScriptsAsync();
                Browser.NavigationCompleted -= Browser_NavigationCompleted;
                Browser.NavigationCompleted += Browser_NavigationCompleted;
                Browser.CoreWebView2.WebMessageReceived -= CoreWebView2_WebMessageReceived;
                Browser.CoreWebView2.WebMessageReceived += CoreWebView2_WebMessageReceived;
                _initialized = true;
                _webViewReady = true;
                UpdatePresentationMode();
                Render();
            }
            catch
            {
                _initialized = false;
                _webViewReady = false;
                Browser.Visibility = Visibility.Collapsed;
                TextBlock? plainTextBlock = GetPlainTextBlock();
                if (plainTextBlock != null)
                {
                    plainTextBlock.Visibility = Visibility.Visible;
                    plainTextBlock.Text = PlainText ?? RawText ?? string.Empty;
                }
            }
            finally
            {
                _initializationInProgress = false;
            }
        }

        private TextBlock? GetPlainTextBlock()
        {
            return FindName("PlainTextBlock") as TextBlock;
        }

        private Canvas? GetBrowserHost()
        {
            return Browser.Parent as Canvas;
        }


        private void ConfigureKatexVirtualHostMapping()
        {
            if (Browser.CoreWebView2 == null)
                return;

            string? katexFolderPath = ResolveKatexFolderPath();
            if (string.IsNullOrWhiteSpace(katexFolderPath) || !Directory.Exists(katexFolderPath))
                return;

            Browser.CoreWebView2.SetVirtualHostNameToFolderMapping(
                KatexVirtualHostName,
                katexFolderPath,
                CoreWebView2HostResourceAccessKind.Allow);
        }

        private static string? ResolveKatexFolderPath()
        {
            string outputFolderPath = Path.Combine(AppContext.BaseDirectory, "KaTeX");
            if (Directory.Exists(outputFolderPath))
                return outputFolderPath;

            string projectFolderPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "KaTeX"));
            if (Directory.Exists(projectFolderPath))
                return projectFolderPath;

            return null;
        }

        private async Task InjectKatexScriptsAsync()
        {
            if (_katexScriptsInjected || Browser.CoreWebView2 == null)
                return;

            string? katexFolderPath = ResolveKatexFolderPath();
            if (string.IsNullOrWhiteSpace(katexFolderPath))
                return;

            string katexScriptPath = Path.Combine(katexFolderPath, "katex.min.js");
            string autoRenderScriptPath = Path.Combine(katexFolderPath, "auto-render.min.js");
            if (!File.Exists(katexScriptPath) || !File.Exists(autoRenderScriptPath))
                return;

            await Browser.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(await File.ReadAllTextAsync(katexScriptPath));
            await Browser.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(await File.ReadAllTextAsync(autoRenderScriptPath));
            _katexScriptsInjected = true;
        }

        private async void Browser_NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            if (!e.IsSuccess || Browser.CoreWebView2 == null)
            {
                _webViewReady = false;
                UpdatePresentationMode();
                return;
            }

            int renderVersion = _renderVersion;

            try
            {
                await Task.Delay(220);
                if (renderVersion != _renderVersion || Browser.CoreWebView2 == null)
                    return;

                string katexReady = await Browser.ExecuteScriptAsync("(function(){ return !!(window.katex && window.renderMathInElement); })();");
                if (!string.Equals(katexReady?.Trim('"'), "true", StringComparison.OrdinalIgnoreCase))
                {
                    await Task.Delay(180);
                    katexReady = await Browser.ExecuteScriptAsync("(function(){ return !!(window.katex && window.renderMathInElement); })();");
                }

                _webViewReady = true;
                string value = await Browser.ExecuteScriptAsync("(function(){ var root = document.getElementById('content-root') || document.body; if (!root) { return { type: 'height', value: 24 }; } var rect = root.getBoundingClientRect(); var style = window.getComputedStyle(root); var marginTop = parseFloat(style.marginTop || '0') || 0; var marginBottom = parseFloat(style.marginBottom || '0') || 0; var rectHeight = rect && isFinite(rect.height) ? rect.height : 0; var contentHeight = Math.max(rectHeight, root.scrollHeight || 0, root.offsetHeight || 0, root.clientHeight || 0); return { type: 'height', value: Math.max(24, Math.ceil(contentHeight + marginTop + marginBottom)) }; })();");
                ApplyHeight(value);

                // Wait for KaTeX to finish painting glyphs, then capture once.
                await Task.Delay(200);
                if (renderVersion != _renderVersion)
                    return;

                _ = CaptureRenderedPreviewAsync(renderVersion);
            }
            catch
            {
                _webViewReady = false;
                UpdatePresentationMode();
            }
        }

        private void CoreWebView2_WebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            string messageType = GetMessageType(e.WebMessageAsJson);
            ApplyHeight(e.WebMessageAsJson);
        }

        private void ApplyHeight(string raw)
        {
            if (!TryParseHeight(raw, out double h))
                return;

            double target = Math.Max(MinRenderHeight, Math.Ceiling(h));
            if (double.IsNaN(target) || double.IsInfinity(target))
                return;

            target = Math.Min(target, 2400);
            if (Math.Abs(Browser.Height - target) > 0.5)
                Browser.Height = target;

            RenderedImage.Width = Math.Max(1, Browser.Width);
            RenderedImage.Height = target;

            Canvas? browserHost = GetBrowserHost();
            if (browserHost != null)
            {
                browserHost.Width = Math.Max(1, Browser.Width);
                browserHost.Height = target;
            }

            _hasMeasuredHeight = true;
            ClipToBounds = true;
            Height = target;
            MinHeight = target;
            InvalidateVisual();
            InvalidateMeasure();
            UpdatePresentationMode();
        }

        private void ResetRenderSurface()
        {
            _hasMeasuredHeight = false;
            _hasCapturedImage = false;
            Browser.Height = MinRenderHeight;
            Browser.Width = Math.Max(1, GetRenderWidth());
            Canvas? browserHost = GetBrowserHost();
            if (browserHost != null)
            {
                browserHost.Width = Browser.Width;
                browserHost.Height = Browser.Height;
                browserHost.Visibility = Visibility.Collapsed;
            }
            Browser.Visibility = Visibility.Collapsed;
            RenderedImage.Width = Browser.Width;
            RenderedImage.Height = Browser.Height;
            RenderedImage.Source = null;
            RenderedImage.Visibility = Visibility.Collapsed;
            Height = double.NaN;
            MinHeight = 0;
        }

        private double GetRenderWidth()
        {
            return double.IsNaN(ActualWidth) ? 0 : Math.Max(0, ActualWidth);
        }

        private static bool TryParseHeight(string raw, out double height)
        {
            height = 0;
            if (string.IsNullOrWhiteSpace(raw))
                return false;

            string cleaned = raw.Trim();
            if (cleaned.StartsWith("\"{", StringComparison.Ordinal) && cleaned.EndsWith("}\"", StringComparison.Ordinal))
            {
                try
                {
                    cleaned = JsonSerializer.Deserialize<string>(cleaned) ?? string.Empty;
                }
                catch
                {
                    return false;
                }
            }

            if (cleaned.StartsWith("{", StringComparison.Ordinal))
            {
                try
                {
                    using JsonDocument document = JsonDocument.Parse(cleaned);
                    if (document.RootElement.TryGetProperty("value", out JsonElement valueElement))
                    {
                        if (valueElement.ValueKind == JsonValueKind.Number)
                            return valueElement.TryGetDouble(out height);

                        if (valueElement.ValueKind == JsonValueKind.String)
                            return double.TryParse(valueElement.GetString(), out height);
                    }
                }
                catch
                {
                    return false;
                }

                return false;
            }

            return double.TryParse(cleaned.Trim('"'), out height);
        }

        private static string GetMessageType(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return string.Empty;

            string cleaned = raw.Trim();
            if (cleaned.StartsWith("\"{", StringComparison.Ordinal) && cleaned.EndsWith("}\"", StringComparison.Ordinal))
            {
                try
                {
                    cleaned = JsonSerializer.Deserialize<string>(cleaned) ?? string.Empty;
                }
                catch
                {
                    return string.Empty;
                }
            }

            if (!cleaned.StartsWith("{", StringComparison.Ordinal))
                return string.Empty;

            try
            {
                using JsonDocument document = JsonDocument.Parse(cleaned);
                if (document.RootElement.TryGetProperty("type", out JsonElement typeElement) && typeElement.ValueKind == JsonValueKind.String)
                    return typeElement.GetString() ?? string.Empty;
            }
            catch
            {
            }

            return string.Empty;
        }

        private async Task CaptureRenderedPreviewAsync(int renderVersion)
        {
            if (Browser.CoreWebView2 == null || !_hasMeasuredHeight)
                return;

            int captureRequestVersion = ++_captureRequestVersion;

            try
            {
                // Ensure the browser is fully visible at the correct size before capturing.
                // CapturePreviewAsync requires an active/visible compositor — Collapsed kills it.
                Canvas? browserHost = GetBrowserHost();
                if (browserHost != null)
                {
                    browserHost.Width = Math.Max(1, Browser.Width);
                    browserHost.Height = Math.Max(MinRenderHeight, Browser.Height);
                    browserHost.Visibility = Visibility.Visible;
                }
                Browser.Visibility = Visibility.Visible;

                // Give the compositor one frame to paint at the correct size.
                await Task.Delay(50);
                if (renderVersion != _renderVersion || captureRequestVersion != _captureRequestVersion || Browser.CoreWebView2 == null)
                    return;

                using var memoryStream = new MemoryStream();
                await Browser.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, memoryStream);
                if (renderVersion != _renderVersion || captureRequestVersion != _captureRequestVersion)
                    return;

                memoryStream.Position = 0;
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.StreamSource = memoryStream;
                image.EndInit();
                image.Freeze();

                RenderedImage.Width = Math.Max(1, Browser.Width);
                RenderedImage.Height = Math.Max(MinRenderHeight, Browser.Height);
                RenderedImage.Source = image;
                _hasCapturedImage = true;
                AddCachedRender(
                    _activeRenderCacheKey,
                    image,
                    Math.Max(1, Browser.Width),
                    Math.Max(MinRenderHeight, Browser.Height));

                // NOW hide the HWND and switch to the static image — single atomic update.
                UpdatePresentationMode();
            }
            catch
            {
                _hasCapturedImage = false;
                UpdatePresentationMode();
            }
        }

        private void ApplyCachedRender(CachedRender cached)
        {
            _captureRequestVersion++;
            _hasMeasuredHeight = true;
            _hasCapturedImage = true;
            Browser.Width = cached.Width;
            Browser.Height = cached.Height;
            RenderedImage.Width = cached.Width;
            RenderedImage.Height = cached.Height;
            RenderedImage.Source = cached.Image;
            Canvas? browserHost = GetBrowserHost();
            if (browserHost != null)
            {
                browserHost.Width = cached.Width;
                browserHost.Height = cached.Height;
                browserHost.Visibility = Visibility.Collapsed;
            }
            Height = cached.Height;
            MinHeight = cached.Height;
            ClipToBounds = true;
            UpdatePresentationMode();
        }

        private static string BuildRenderCacheKey(string html, double width)
        {
            int widthBucket = Math.Max(1, (int)Math.Round(width));
            byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(html ?? string.Empty));
            return widthBucket.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + ":" + Convert.ToHexString(digest);
        }

        private static bool TryGetCachedRender(string key, out CachedRender? cached)
        {
            lock (RenderCacheGate)
            {
                if (!RenderCache.TryGetValue(key, out cached))
                    return false;

                RenderCacheLru.Remove(key);
                RenderCacheLru.AddLast(key);
                return true;
            }
        }

        private static void AddCachedRender(string key, BitmapImage image, double width, double height)
        {
            if (string.IsNullOrWhiteSpace(key) || image == null)
                return;

            long estimatedBytes = Math.Max(1L, (long)image.PixelWidth * image.PixelHeight * 4L);
            if (estimatedBytes > RenderCacheByteLimit / 2)
                return;

            lock (RenderCacheGate)
            {
                if (RenderCache.TryGetValue(key, out CachedRender? existing))
                {
                    _renderCacheBytes -= existing.EstimatedBytes;
                    RenderCacheLru.Remove(key);
                }

                RenderCache[key] = new CachedRender(image, width, height, estimatedBytes);
                RenderCacheLru.AddLast(key);
                _renderCacheBytes += estimatedBytes;

                while (RenderCacheLru.Count > RenderCacheEntryLimit || _renderCacheBytes > RenderCacheByteLimit)
                {
                    LinkedListNode<string>? oldest = RenderCacheLru.First;
                    if (oldest == null)
                        break;

                    RenderCacheLru.RemoveFirst();
                    if (RenderCache.Remove(oldest.Value, out CachedRender? removed))
                        _renderCacheBytes -= removed.EstimatedBytes;
                }
            }
        }

        private sealed record CachedRender(BitmapImage Image, double Width, double Height, long EstimatedBytes);
    }
}
