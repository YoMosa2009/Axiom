using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Windows;

namespace Malx_AI
{
    public class ChatMessage : INotifyPropertyChanged
    {
        private static readonly Regex FencedCodeBlockRegex = new(@"```(?<language>[^\r\n`]*)\r?\n?(?<code>[\s\S]*?)```", RegexOptions.Compiled);
        private static readonly Regex ExcessiveBlankLinesRegex = new(@"(?:\r?\n){3,}", RegexOptions.Compiled);
        private static readonly string[] MathFenceLanguages = ["latex", "tex", "math", "katex"];
        private const int StreamingRevealIntervalMs = 18;

        public Guid Id { get; set; } = Guid.NewGuid();
        public int CloudPromptTokens { get; set; }
        public int CloudCompletionTokens { get; set; }
        public int CloudTotalTokens { get; set; }
        private string _content;
        private string _formattedContent;
        private string _displayFormattedContent;
        private string _thinkingContent = "";
        private string _formattedThinkingContent = "";
        private string _thinkingHeaderText = "Thinking";
        private bool _isThinkingExpanded;
        private bool _isThinkingInProgress;
        private bool _isStreaming;
        private bool _preferPlainTextRendering;
        private bool _isCodeBlockCollapsed;
        private string _streamingDisplayTarget = string.Empty;
        private System.Windows.Threading.DispatcherTimer? _streamingRevealTimer;

        public ObservableCollection<ChatMessageCodeBlock> CodeBlocks { get; } = new();

        public string Role { get; set; } // "user", "assistant", or "system"

        public string Content
        {
            get => _content;
            set
            {
                StopStreamingReveal();
                SetContentCore(value, preserveFormatting: false, notifyRichRendering: true);
            }
        }

        public bool IsStreaming
        {
            get => _isStreaming;
            set
            {
                if (InvokeOnUiThreadIfRequired(() => IsStreaming = value))
                    return;

                if (_isStreaming != value)
                {
                    _isStreaming = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(ShouldShowGenerationStatus));
                    OnPropertyChanged(nameof(GenerationStatusText));
                }
            }
        }

        public bool PreferPlainTextRendering
        {
            get => _preferPlainTextRendering;
            set
            {
                if (InvokeOnUiThreadIfRequired(() => PreferPlainTextRendering = value))
                    return;

                if (_preferPlainTextRendering != value)
                {
                    _preferPlainTextRendering = value;
                    OnPropertyChanged();
                }
            }
        }

        public DateTime Timestamp { get; set; }
        public string ModelLabel { get; set; } = "";

        public string ThinkingContent
        {
            get => _thinkingContent;
            set
            {
                if (InvokeOnUiThreadIfRequired(() => ThinkingContent = value))
                    return;

                if (_thinkingContent != value)
                {
                    _thinkingContent = value ?? string.Empty;
                    _formattedThinkingContent = MarkdownParser.ToDisplayText(_thinkingContent);
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(FormattedThinkingContent));
                    OnPropertyChanged(nameof(RichThinkingContent));
                    OnPropertyChanged(nameof(HasThinkingContent));
                    OnPropertyChanged(nameof(ShouldShowThinkingHeader));
                    OnPropertyChanged(nameof(SupportsRichThinkingRendering));
                }
            }
        }

        public bool IsThinkingExpanded
        {
            get => _isThinkingExpanded;
            set
            {
                if (InvokeOnUiThreadIfRequired(() => IsThinkingExpanded = value))
                    return;

                if (_isThinkingExpanded != value)
                {
                    _isThinkingExpanded = value;
                    OnPropertyChanged();
                }
            }
        }

        public bool IsThinkingInProgress
        {
            get => _isThinkingInProgress;
            set
            {
                if (InvokeOnUiThreadIfRequired(() => IsThinkingInProgress = value))
                    return;

                if (_isThinkingInProgress != value)
                {
                    _isThinkingInProgress = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(ShouldShowThinkingHeader));
                    OnPropertyChanged(nameof(GenerationStatusText));
                }
            }
        }

        public bool HasThinkingContent => !string.IsNullOrWhiteSpace(_thinkingContent);
        public bool ShouldShowThinkingHeader => HasThinkingContent;
        public bool ShouldShowGenerationStatus => string.Equals(Role, "assistant", StringComparison.OrdinalIgnoreCase) && IsStreaming;
        public string GenerationStatusText => IsThinkingInProgress || !HasDisplayContent ? "Thinking" : "Generating";

        public string ThinkingHeaderText
        {
            get => string.IsNullOrWhiteSpace(_thinkingHeaderText) ? "Thinking" : _thinkingHeaderText;
            set
            {
                if (InvokeOnUiThreadIfRequired(() => ThinkingHeaderText = value))
                    return;

                string normalized = string.IsNullOrWhiteSpace(value) ? "Thinking" : value;
                if (_thinkingHeaderText != normalized)
                {
                    _thinkingHeaderText = normalized;
                    OnPropertyChanged();
                }
            }
        }

        public string FormattedThinkingContent
        {
            get => _formattedThinkingContent;
            set
            {
                if (InvokeOnUiThreadIfRequired(() => FormattedThinkingContent = value))
                    return;

                if (_formattedThinkingContent != value)
                {
                    _formattedThinkingContent = value;
                    OnPropertyChanged();
                }
            }
        }

        public string DisplayFormattedContent
        {
            get => _displayFormattedContent;
            set
            {
                if (InvokeOnUiThreadIfRequired(() => DisplayFormattedContent = value))
                    return;

                if (_displayFormattedContent != value)
                {
                    _displayFormattedContent = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(HasDisplayContent));
                    OnPropertyChanged(nameof(GenerationStatusText));
                }
            }
        }

        public bool HasDisplayContent => !string.IsNullOrWhiteSpace(_displayFormattedContent);

        public string RichRenderContent => NormalizeMathCodeFences(_content);

        public string RichThinkingContent => NormalizeMathCodeFences(_thinkingContent);

        public bool HasCodeBlocks => CodeBlocks.Count > 0;

        public bool IsCodeBlockCollapsed
        {
            get => _isCodeBlockCollapsed;
            set
            {
                if (InvokeOnUiThreadIfRequired(() => IsCodeBlockCollapsed = value))
                    return;

                if (_isCodeBlockCollapsed != value)
                {
                    _isCodeBlockCollapsed = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(CodeBlockToggleText));
                }
            }
        }

        public string CodeBlockToggleText => _isCodeBlockCollapsed ? "Show" : "Hide";

        public string CodeBlockHeaderText => CodeBlocks.Count == 1 ? "Code" : $"Code ({CodeBlocks.Count})";

        public bool SupportsRichThinkingRendering => MessageHtmlRenderer.NeedsHtmlRendering(RichThinkingContent);

        public bool IsPinned { get; set; }
        public MessageImportance Importance { get; set; } = MessageImportance.Low;
        public bool IsCompactionProtected { get; set; }
        public bool IsCompactionMarker { get; set; }
        public List<CompactionSummaryEntry>? CompactionSummaries { get; set; }

        public string FormattedContent
        {
            get => _formattedContent;
            set
            {
                if (InvokeOnUiThreadIfRequired(() => FormattedContent = value))
                    return;

                if (_formattedContent != value)
                {
                    _formattedContent = value;
                    OnPropertyChanged();
                }
            }
        }

        public bool SupportsRichRendering => MessageHtmlRenderer.NeedsHtmlRendering(RichRenderContent);

        public event PropertyChangedEventHandler PropertyChanged;

        public ChatMessage(string role, string content)
        {
            Role = role;
            _content = content;
            _formattedContent = MarkdownParser.ToDisplayText(content);
            _displayFormattedContent = _formattedContent;
            UpdateCodeBlockPresentation(content);
            Timestamp = DateTime.Now;
        }

        public void SetStreamingContent(string? content)
        {
            SetContentCore(content, preserveFormatting: true, notifyRichRendering: false, smoothStreaming: true);
        }

        public void FinalizeStreamingContent(string? content)
        {
            StopStreamingReveal();
            SetContentCore(content, preserveFormatting: false, notifyRichRendering: true);
        }

        private void SetContentCore(string? value, bool preserveFormatting, bool notifyRichRendering, bool smoothStreaming = false)
        {
            if (InvokeOnUiThreadIfRequired(() => SetContentCore(value, preserveFormatting, notifyRichRendering, smoothStreaming)))
                return;

            string normalized = value ?? string.Empty;
            bool contentChanged = _content != normalized;
            if (!contentChanged && !smoothStreaming && preserveFormatting)
                return;

            _content = normalized;
            _formattedContent = preserveFormatting
                ? normalized
                : MarkdownParser.ToDisplayText(normalized);
            if (preserveFormatting)
            {
                ReplaceCodeBlocks([]);
                if (smoothStreaming)
                    SetStreamingDisplayTarget(normalized);
                else
                    DisplayFormattedContent = normalized;
            }
            else
            {
                UpdateCodeBlockPresentation(normalized);
            }

            if (contentChanged)
            {
                OnPropertyChanged(nameof(Content));
                OnPropertyChanged(nameof(FormattedContent));
                OnPropertyChanged(nameof(RichRenderContent));
                if (notifyRichRendering)
                    OnPropertyChanged(nameof(SupportsRichRendering));
            }
        }

        private void SetStreamingDisplayTarget(string target)
        {
            _streamingDisplayTarget = target ?? string.Empty;

            if (string.IsNullOrEmpty(_streamingDisplayTarget))
            {
                StopStreamingReveal();
                DisplayFormattedContent = string.Empty;
                return;
            }

            if (!_streamingDisplayTarget.StartsWith(_displayFormattedContent ?? string.Empty, StringComparison.Ordinal))
                DisplayFormattedContent = string.Empty;

            EnsureStreamingRevealTimer();
            RevealStreamingTextStep();
        }

        private void EnsureStreamingRevealTimer()
        {
            if (_streamingRevealTimer == null)
            {
                _streamingRevealTimer = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(StreamingRevealIntervalMs)
                };
                _streamingRevealTimer.Tick += (_, _) => RevealStreamingTextStep();
            }

            if (!_streamingRevealTimer.IsEnabled)
                _streamingRevealTimer.Start();
        }

        private void RevealStreamingTextStep()
        {
            int currentLength = _displayFormattedContent?.Length ?? 0;
            int targetLength = _streamingDisplayTarget.Length;

            if (currentLength >= targetLength)
            {
                _streamingRevealTimer?.Stop();
                return;
            }

            int remaining = targetLength - currentLength;
            int chunkSize = CalculateStreamingRevealChunkSize(remaining);
            int nextLength = Math.Min(targetLength, currentLength + chunkSize);
            DisplayFormattedContent = _streamingDisplayTarget.Substring(0, nextLength);

            if (nextLength >= targetLength)
                _streamingRevealTimer?.Stop();
        }

        private static int CalculateStreamingRevealChunkSize(int remaining)
        {
            if (remaining > 600) return 96;
            if (remaining > 240) return 56;
            if (remaining > 80) return 28;
            if (remaining > 24) return 12;
            return 4;
        }

        private void StopStreamingReveal()
        {
            if (InvokeOnUiThreadIfRequired(StopStreamingReveal))
                return;

            _streamingRevealTimer?.Stop();
            _streamingDisplayTarget = string.Empty;
        }

        private void UpdateCodeBlockPresentation(string content)
        {
            var codeBlocks = new List<ChatMessageCodeBlock>();
            foreach (Match match in FencedCodeBlockRegex.Matches(content ?? string.Empty))
            {
                string language = match.Groups["language"].Value.Trim();
                if (IsMathFenceLanguage(language))
                    continue;

                string code = match.Groups["code"].Value.Replace("\r\n", "\n").TrimEnd();
                if (string.IsNullOrWhiteSpace(code))
                    continue;

                codeBlocks.Add(new ChatMessageCodeBlock(language, code));
            }

            ReplaceCodeBlocks(codeBlocks);

            string prose = NormalizeMathCodeFences(FencedCodeBlockRegex.Replace(content ?? string.Empty, match =>
            {
                string language = match.Groups["language"].Value.Trim();
                return IsMathFenceLanguage(language)
                    ? NormalizeMathFenceContent(match.Groups["code"].Value)
                    : Environment.NewLine;
            })).Trim();
            prose = ExcessiveBlankLinesRegex.Replace(prose, Environment.NewLine + Environment.NewLine).Trim();
            DisplayFormattedContent = MarkdownParser.ToDisplayText(prose);
        }

        private static string NormalizeMathCodeFences(string? content)
        {
            if (string.IsNullOrWhiteSpace(content))
                return string.Empty;

            return FencedCodeBlockRegex.Replace(content, match =>
            {
                string language = match.Groups["language"].Value.Trim();
                return IsMathFenceLanguage(language)
                    ? NormalizeMathFenceContent(match.Groups["code"].Value)
                    : match.Value;
            });
        }

        private static bool IsMathFenceLanguage(string? language)
        {
            if (string.IsNullOrWhiteSpace(language))
                return false;

            return MathFenceLanguages.Any(candidate => string.Equals(candidate, language.Trim(), StringComparison.OrdinalIgnoreCase));
        }

        private static string NormalizeMathFenceContent(string code)
        {
            string normalized = (code ?? string.Empty).Replace("\r\n", "\n").Trim();
            return string.IsNullOrWhiteSpace(normalized)
                ? string.Empty
                : "$$\n" + normalized + "\n$$";
        }

        private void ReplaceCodeBlocks(IEnumerable<ChatMessageCodeBlock> codeBlocks)
        {
            if (InvokeOnUiThreadIfRequired(() => ReplaceCodeBlocks(codeBlocks)))
                return;

            CodeBlocks.Clear();
            foreach (ChatMessageCodeBlock codeBlock in codeBlocks ?? [])
                CodeBlocks.Add(codeBlock);

            if (CodeBlocks.Count == 0)
                IsCodeBlockCollapsed = false;

            OnPropertyChanged(nameof(CodeBlocks));
            OnPropertyChanged(nameof(HasCodeBlocks));
            OnPropertyChanged(nameof(CodeBlockHeaderText));
            OnPropertyChanged(nameof(CodeBlockToggleText));
        }

        private static bool InvokeOnUiThreadIfRequired(Action action)
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.CheckAccess())
                return false;

            dispatcher.Invoke(action);
            return true;
        }

        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    public sealed class ChatMessageCodeBlock
    {
        public ChatMessageCodeBlock(string language, string code)
        {
            Language = language ?? string.Empty;
            Code = code ?? string.Empty;
        }

        public string Language { get; }
        public string Code { get; }
        public bool HasLanguage => !string.IsNullOrWhiteSpace(Language);
    }

    public sealed class ChatDocumentAttachment
    {
        public string Name { get; set; } = "";
        public string Content { get; set; } = "";
        public string Kind { get; set; } = "text";
        public string MimeType { get; set; } = "";
        public string Base64Data { get; set; } = "";
        public long FileSizeBytes { get; set; }
        public DateTime ImportedAt { get; set; } = DateTime.Now;

        /// <summary>
        /// True while the attachment is staged in the input box and not yet sent. The input
        /// tray shows only pending chips, so they clear once the message is sent — but the
        /// document stays in <c>_chatDocuments</c> as conversation context so follow-up
        /// questions about it still work.
        /// </summary>
        public bool IsPending { get; set; } = true;

        public bool HasTextContent => !string.IsNullOrWhiteSpace(Content);
        public bool IsImage => string.Equals(Kind, "image", StringComparison.OrdinalIgnoreCase);
    }

    public class ChatSession : INotifyPropertyChanged
    {
        private string _name;

        public int Id { get; set; }

        public string Name
        {
            get => _name;
            set
            {
                if (_name != value)
                {
                    _name = value;
                    OnPropertyChanged();
                }
            }
        }

        public ObservableCollection<ChatMessage> Messages { get; set; }
        public List<ChatDocumentAttachment> AttachedDocuments { get; set; } = new();
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }

        public event PropertyChangedEventHandler PropertyChanged;

        public ChatSession(string name)
        {
            Name = name;
            Messages = new ObservableCollection<ChatMessage>();
            CreatedAt = DateTime.Now;
            UpdatedAt = DateTime.Now;
        }

        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
