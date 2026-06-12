using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Data;
using System.Globalization;
using System.Collections.Specialized;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using LLama;
using LLama.Common;
using LLama.Sampling;
using LLama.Transformers;

namespace Malx_AI
{
    public partial class MainWindow : Window
    {
        private const string EmptyChatLogoResourcePath = "pack://application:,,,/Assets/Malx_Logo2.png";
        private LLamaWeights _model;
        private InteractiveExecutor _executor;
        private LLama.ChatSession _chatSession;
        private ModelParams _activeModelParams;
        private MtmdWeights _mtmdWeights;
        private string _mmprojPath = "";
        private CancellationTokenSource _cancellationTokenSource;
        private int _tokenCount = 0;
        private string _modelName = "No Model Loaded";
        private Stopwatch _inferenceTimer = new Stopwatch();
        private string _systemPrompt = "";
        private bool _isProcessing = false;
        private ImageSource _emptyChatLogoSource;
        private double _savedLocalTemperature = 0.7;
        private double _savedLocalMinP = 0.05;
        private double _savedLocalContextLength = 8192;
        private bool _isUpdatingInferenceSettingsUi;

        private DatabaseService _database;
        private JsonChatPersistence _jsonPersistence;
        private int _chatCounter = 0;
        private Dictionary<int, string> _chatContent = new Dictionary<int, string>();
        private int _currentChatId = -1;
        private Button _activeChatButton = null;
        private readonly Dictionary<int, string> _chatNames = new();
        private bool _isDarkMode = true;
        private HardwareProfile _detectedHardware = new HardwareProfile();
        private bool _isSettingsAnimating = false;
        private bool _isViewTransitionAnimating = false;
        private bool _isHistorySectionExpanded = true;
        private bool _isHistorySectionAnimating = false;
        private bool _isSidebarAnimating = false;
        private bool _isSidebarCollapsed = false;
        private bool _useGemma4LocalCliMode = false;
        private string _gemma4ModelPath = "";
        private readonly PersonaMemoryService _personaMemoryService = new();
        private readonly WebSearchService _webSearchService = new();
        private PersonaMemoryViewModel _personaMemoryViewModel;
        private readonly ChatWorkspaceStatePersistence _chatWorkspaceStatePersistence = new();
        private readonly SemaphoreSlim _stateSaveGate = new(1, 1);
        private readonly SemaphoreSlim _chatSessionSaveGate = new(1, 1);
        private readonly SemaphoreSlim _advancedStateSaveGate = new(1, 1);
        private readonly SemaphoreSlim _coordinatedPersistenceGate = new(1, 1);
        private readonly SemaphoreSlim _notificationGate = new(1, 1);
        private readonly ChatAdvancedStatePersistence _chatAdvancedStatePersistence = new();
        private readonly List<ChatDocumentAttachment> _chatDocuments = new();
        private readonly DocumentRetriever _documentRetriever = new();
        private readonly ObservableCollection<PinnedMessageEntry> _pinnedMessages = new();
        private readonly List<ChatBranch> _branches = new();
        private Guid _activeBranchId = Guid.Empty;
        private readonly List<PromptTemplateEntry> _promptTemplates = new();
        private readonly List<SystemPromptPresetEntry> _systemPromptPresets = new();
        private string _nextMessageModelOverride = "";
        private readonly Dictionary<int, WorkplaceSessionSnapshot> _workplaceChats = new();
        private readonly Dictionary<int, string> _workplaceChatNames = new();
        private int _workplaceChatCounter = 0;
        private int _currentWorkplaceChatId = -1;
        private Button _activeWorkplaceButton = null;

        private void LoadEmptyChatLogo()
        {
            if (_emptyChatLogoSource != null)
            {
                EmptyChatLogoImage.Source = _emptyChatLogoSource;
                return;
            }

            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.UriSource = new Uri(EmptyChatLogoResourcePath, UriKind.Absolute);
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
                bitmap.EndInit();
                bitmap.Freeze();

                _emptyChatLogoSource = ConvertEmptyChatLogoToWhite(bitmap);
                EmptyChatLogoImage.Source = _emptyChatLogoSource;
            }
            catch
            {
                EmptyChatLogoImage.Visibility = Visibility.Collapsed;
            }
        }

        private static BitmapSource ConvertEmptyChatLogoToWhite(BitmapSource source)
        {
            var formatted = source.Format == PixelFormats.Bgra32
                ? source
                : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);

            int stride = formatted.PixelWidth * 4;
            byte[] pixels = new byte[formatted.PixelHeight * stride];
            formatted.CopyPixels(pixels, stride, 0);

            const byte backgroundThreshold = 238;
            const byte detailThreshold = 190;

            for (int index = 0; index < pixels.Length; index += 4)
            {
                byte blue = pixels[index];
                byte green = pixels[index + 1];
                byte red = pixels[index + 2];
                byte alpha = pixels[index + 3];

                if (alpha == 0)
                    continue;

                bool nearWhiteBackground = red >= backgroundThreshold && green >= backgroundThreshold && blue >= backgroundThreshold;
                bool logoPixel = !nearWhiteBackground && (red <= detailThreshold || green <= detailThreshold || blue <= detailThreshold);

                pixels[index] = 255;
                pixels[index + 1] = 255;
                pixels[index + 2] = 255;
                pixels[index + 3] = logoPixel ? alpha : (byte)0;
            }

            var processed = BitmapSource.Create(
                formatted.PixelWidth,
                formatted.PixelHeight,
                formatted.DpiX,
                formatted.DpiY,
                PixelFormats.Bgra32,
                null,
                pixels,
                stride);

            processed.Freeze();
            return processed;
        }

        private void RefreshInferenceSettingsUi()
        {
            if (TemperatureSlider == null || TopPSlider == null || CtxSlider == null)
                return;

            _isUpdatingInferenceSettingsUi = true;
            try
            {
                if (_cloudModeActive)
                {
                    OpenRouterInferenceSettingsSnapshot snapshot = _openRouterChatService.GetInferenceSettingsSnapshot(_selectedOpenRouterModelId);

                    TemperatureSlider.Value = Math.Clamp(snapshot.Temperature, TemperatureSlider.Minimum, TemperatureSlider.Maximum);
                    TopPSlider.Value = Math.Clamp(snapshot.TopP, TopPSlider.Minimum, TopPSlider.Maximum);
                    CtxSlider.Value = Math.Clamp(snapshot.ContextWindowTokens, CtxSlider.Minimum, CtxSlider.Maximum);

                    TemperatureSlider.IsEnabled = false;
                    TopPSlider.IsEnabled = false;
                    CtxSlider.IsEnabled = false;

                    if (TemperatureValueText != null)
                        TemperatureValueText.Text = snapshot.Temperature.ToString("F2", CultureInfo.InvariantCulture);
                    if (TopPValueText != null)
                        TopPValueText.Text = snapshot.TopP.ToString("F2", CultureInfo.InvariantCulture);
                    if (ContextLengthValueText != null)
                        ContextLengthValueText.Text = snapshot.ContextWindowTokens.ToString("F0", CultureInfo.InvariantCulture) + " tokens";
                    if (InferenceSettingsSourceText != null)
                        InferenceSettingsSourceText.Text = $"Cloud mode active. Showing {snapshot.ModelLabel} inference profile values for Temperature, Min-P, and Context Length.";
                }
                else
                {
                    TemperatureSlider.Value = Math.Clamp(_savedLocalTemperature, TemperatureSlider.Minimum, TemperatureSlider.Maximum);
                    TopPSlider.Value = Math.Clamp(_savedLocalMinP, TopPSlider.Minimum, TopPSlider.Maximum);
                    CtxSlider.Value = Math.Clamp(_savedLocalContextLength, CtxSlider.Minimum, CtxSlider.Maximum);

                    TemperatureSlider.IsEnabled = true;
                    TopPSlider.IsEnabled = true;
                    CtxSlider.IsEnabled = true;

                    if (TemperatureValueText != null)
                        TemperatureValueText.Text = TemperatureSlider.Value.ToString("F1", CultureInfo.InvariantCulture);
                    if (TopPValueText != null)
                        TopPValueText.Text = TopPSlider.Value.ToString("F2", CultureInfo.InvariantCulture);
                    if (ContextLengthValueText != null)
                        ContextLengthValueText.Text = CtxSlider.Value.ToString("F0", CultureInfo.InvariantCulture) + " tokens";
                    if (InferenceSettingsSourceText != null)
                        InferenceSettingsSourceText.Text = "Local mode active. These controls affect the loaded on-device model.";
                }
            }
            finally
            {
                _isUpdatingInferenceSettingsUi = false;
            }
        }

        private void CacheLocalInferenceSettings()
        {
            if (_cloudModeActive || _isUpdatingInferenceSettingsUi || TemperatureSlider == null || TopPSlider == null || CtxSlider == null)
                return;

            _savedLocalTemperature = TemperatureSlider.Value;
            _savedLocalMinP = TopPSlider.Value;
            _savedLocalContextLength = CtxSlider.Value;

            if (TemperatureValueText != null)
                TemperatureValueText.Text = _savedLocalTemperature.ToString("F1", CultureInfo.InvariantCulture);
            if (TopPValueText != null)
                TopPValueText.Text = _savedLocalMinP.ToString("F2", CultureInfo.InvariantCulture);
            if (ContextLengthValueText != null)
                ContextLengthValueText.Text = _savedLocalContextLength.ToString("F0", CultureInfo.InvariantCulture) + " tokens";
        }
        private bool _isWorkplaceHistorySectionExpanded = true;
        private bool _isWorkplaceHistorySectionAnimating = false;
        private readonly RotateTransform _inlineSpinnerRotate = new();
        private readonly System.Windows.Threading.DispatcherTimer _neuronTimer = new();
        private readonly System.Windows.Threading.DispatcherTimer _toolActivityTimer = new();
        private readonly Dictionary<string, NeuronBranchNode> _neuronBranches = new(StringComparer.OrdinalIgnoreCase);
        private int _neuronTick;
        private string _activeToolIndicatorLabel = string.Empty;
        private int _activeToolIndicatorPhase;
        private int _activeToolIndicatorGlyphPhase;
        private bool _loadedPersistedWorkplaceChats;
        private Button? _normalThinkingToggleButton;
        private Button? _normalWebSearchToggleButton;
        private Button? _localModeButton;
        private Button? _cloudModeButton;
        private Button? _eidosModelButton;
        private Button? _hephaModelButton;
        private bool _isFetchingOpenRouterUsage;
        private double _openRouterUsagePercent;
        private readonly PythonExecutionService _pythonExecutionService = new();
        private readonly OpenRouterChatService _openRouterChatService = new();
        private ScrollViewer? _chatDisplayScrollViewer;
        private int _workspaceStateSaveVersion;
        private int _chatSessionSaveVersion;
        private int _advancedStateSaveVersion;
        private int _coordinatedPersistenceVersion;
        private bool _cloudModeActive;
        private string _selectedOpenRouterModelId = OpenRouterChatService.DefaultModelId;
        private bool _isTestingOpenRouterKey;
        private const int CloudHistoryTokenBudget = 6000;
        private const int CloudToolResultCharacterLimit = 6000;
        private const int CloudToolLoopIterationLimit = 4;
        private const int CloudContextNoticeThresholdPercent = 80;
        private const int CloudContextCriticalThresholdPercent = 92;
        private const string OpenRouterModelSettingKey = "openrouter_model_id";
        private const int SandboxEligibilityThreshold = 4;
        private const int ContextSelectionThresholdTokens = 1500;
        private const string AttachedDocumentHeaderLine = "ATTACHED DOCUMENT — YOU MUST READ AND USE THIS";
        private const string AttachedDocumentEndLine = "END OF DOCUMENT";
        private const string AttachedDocumentRequiredReferenceInstruction = "The user has attached a document. Your response must directly reference and use the content of the attached document. If the user's request relates to the document in any way, base your answer on the document content. Do not ignore the document.";
        private static readonly string[] ExplicitWebSearchMarkers =
        [
            "[web]", "search the web", "search online", "search for ", "look up", "lookup",
            "find online", "check online", "web search", "browse the web", "look it up",
            "google it", "bing it", "internet search"
        ];

        private static readonly string[] SandboxUnitWords =
        [
            "kilometers", "km", "meters", "miles", "kilograms", "kg", "pounds", "grams", "liters", "gallons",
            "seconds", "minutes", "hours", "degrees", "percent", "dollars", "euros", "watts", "volts", "amps",
            "newtons", "joules"
        ];

        private static readonly string[] SandboxQuantityPhrases =
        [
            "how many", "how much", "how far", "how long", "how fast", "what is the total", "what is the average",
            "what is the rate", "what is the ratio", "convert", "calculate", "compute", "solve for"
        ];

        private static readonly string[] SandboxDomainWords =
        [
            "velocity", "acceleration", "force", "mass", "momentum", "energy", "power", "voltage", "resistance",
            "frequency", "probability", "interest", "compound", "principal", "depreciation", "density", "pressure",
            "temperature", "distance", "area", "volume"
        ];

        private static readonly Regex SandboxExpressionRegex = new(@"\d+(?:\.\d+)?\s*[\+\-\*/\^]\s*\d+(?:\.\d+)?", RegexOptions.Compiled);
        private static readonly Regex SandboxPythonCodeBlockRegex = new(@"```(?:python)?\s*(?<code>[\s\S]*?)```", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex SandboxNumberWithUnitRegex = new(@"(?<label>[A-Za-z][A-Za-z0-9_-]*)?\s*(?<number>-?\d+(?:\.\d+)?)\s*(?<unit>kilometers|km|meters|miles|kilograms|kg|pounds|grams|liters|gallons|seconds|minutes|hours|degrees|percent|dollars|euros|watts|volts|amps|newtons|joules)?", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex PythonResultLineRegex = new(@"^(?<label>[^:=]+?)\s*[:=]\s*(?<value>-?\d+(?:\.\d+)?(?:[eE][\+\-]?\d+)?(?:\s*[%A-Za-z/]+)?)$", RegexOptions.Compiled);
        private static readonly Regex PythonNameErrorRegex = new(@"NameError:\s*name\s+'(?<name>[A-Za-z_][A-Za-z0-9_]*)'\s+is\s+not\s+defined", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly string[] DynamicInputIntentPhrases =
        [
            "user inputs", "user enters", "ask the user", "prompt the user", "takes input"
        ];

        private static readonly string[] CodingIntentPhrases =
        [
            "write", "code", "script", "function", "program", "generate", "build", "create", "implement",
            "python", "javascript", "c#", "java", "html", "css", "sql", "debug", "fix this", "error", "bug"
        ];

        private static readonly string[] PythonIntentPhrases =
        [
            "python", "python 3", "main.py", "online python compiler", "python compiler", "python interpreter"
        ];

        private static readonly string[] ComplexityReasoningPhrases =
        [
            "debug", "fix this", "why does", "trace", "step by step", "explain how", "walk me through"
        ];

        private static readonly string[] ComplexityAnalysisWords =
        [
            "design", "architect", "compare", "evaluate", "analyze", "plan"
        ];

        private static readonly string[] ComplexityConditionalWords =
        [
            "if", "when", "unless", "assuming", "given that"
        ];

        private static readonly string[] ConversationalFollowUpPhrases =
        [
            "continue", "go on", "expand", "elaborate", "more on that", "what about that", "how about that",
            "based on that", "from that", "compare that", "same topic", "same subject", "tell me more"
        ];

        private static readonly HashSet<string> CappedInjectionLabels = new(StringComparer.OrdinalIgnoreCase)
        {
            "WEB SEARCH DATA",
            "PROJECT KNOWLEDGE BASE",
            "PRIOR KNOWLEDGE",
            "CALCULATOR TOOL RESULTS"
        };

        private static readonly HashSet<string> StalePrunableInjectionLabels = new(StringComparer.OrdinalIgnoreCase)
        {
            "WEB SEARCH DATA",
            "PRIOR KNOWLEDGE",
            "CALCULATOR TOOL RESULTS",
            "PYTHON RESULT"
        };

        private static readonly Regex LabeledInjectionBlockRegex = new(@"\[\[(?<label>[A-Z0-9 _-]+)\]\]\s*(?<body>[\s\S]*?)\s*\[\[END \k<label>\]\]", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex QwenThinkBlockStripRegex = new(@"<think>[\s\S]*?</think>", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex LeadingBlankLinesRegex = new(@"^(?:[^\S\r\n]*\r?\n)+", RegexOptions.Compiled);
        private static readonly Regex CloudHistoryBracketedMetadataBlockRegex = new(@"\[\[(?<label>WEB SEARCH RESULTS|PYTHON RESULT|CALCULATOR RESULTS|VERIFIED CONSTANTS|PRIOR COMPUTATION RESULTS|PYTHON EXECUTION RESULTS)\]\][\s\S]*?\[\[END \k<label>\]\]\s*", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex CloudHistoryPlainMetadataBlockRegex = new(@"(?ims)^[ \t]*(?:WEB SEARCH RESULTS|PYTHON RESULT|CALCULATOR RESULTS|VERIFIED CONSTANTS|PRIOR COMPUTATION RESULTS|PYTHON EXECUTION RESULTS)(?::|\s*$)\s*(?:\r?\n)?[\s\S]*?(?=^[ \t]*(?:WEB SEARCH RESULTS|PYTHON RESULT|CALCULATOR RESULTS|VERIFIED CONSTANTS|PRIOR COMPUTATION RESULTS|PYTHON EXECUTION RESULTS)(?::|\s*$)|^\s*$|\z)", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Multiline);
        private static readonly Regex DigitLetterMultiplicationRegex = new(@"(?<digit>\d)(?<letter>[A-Za-z])", RegexOptions.Compiled);
        private static readonly Regex CaretExponentRegex = new(@"(?<left>[A-Za-z0-9_\)\.]+)\s*\^\s*(?<right>[A-Za-z0-9_\(\.]+)", RegexOptions.Compiled);
        private static readonly Regex SqrtRegex = new(@"\bsqrt\s*(?=\(|[A-Za-z0-9])", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex PiRegex = new(@"\bpi\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex NumericOnlyLineRegex = new(@"^(?<value>-?\d+(?:\.\d+)?(?:[eE][\+\-]?\d+)?)$", RegexOptions.Compiled);
        private static readonly Regex NumericWithOptionalLabelRegex = new(@"^(?:(?<label>[^:=]+?)\s*[:=]\s*)?(?<value>-?\d+(?:\.\d+)?(?:[eE][\+\-]?\d+)?)(?<suffix>\s*[%A-Za-z/]+)?$", RegexOptions.Compiled);
        private const string EmptyStrippedResponseInlineHtml = "<span style=\"color: #9CA3AF;\">The model did not produce a response for this input.</span>";
        private const string LocalMathLatexInstruction = "When writing mathematical expressions, use LaTeX notation. Use single dollar signs for inline math and double dollar signs for standalone equations on their own line.";

        private static readonly string[] EmptyChatGreetings =
        [
            "What's on your mind",
            "What are we solving today",
            "What do you want to explore",
            "Ready when you are",
            "What's the goal today",
            "Let's get to work",
            "What do you need",
            "Ask me anything"
        ];

        private const string NormalChatAttachmentDialogFilter = "Supported files (*.pdf;*.txt;*.md;*.json;*.csv;*.tsv;*.xlsx;*.docx;*.rtf;code;images)|*.pdf;*.txt;*.md;*.markdown;*.json;*.jsonc;*.xml;*.yaml;*.yml;*.toml;*.csv;*.tsv;*.xlsx;*.docx;*.rtf;*.log;*.ini;*.config;*.cs;*.js;*.ts;*.jsx;*.tsx;*.html;*.htm;*.css;*.sql;*.py;*.java;*.cpp;*.c;*.h;*.go;*.rs;*.rb;*.php;*.ps1;*.bat;*.sh;*.tex;*.srt;*.vtt;*.png;*.jpg;*.jpeg;*.webp;*.gif;*.bmp|Documents (*.pdf;*.txt;*.md;*.json;*.csv;*.tsv;*.xlsx;*.docx;*.rtf)|*.pdf;*.txt;*.md;*.json;*.csv;*.tsv;*.xlsx;*.docx;*.rtf|Images (*.png;*.jpg;*.jpeg;*.webp;*.gif;*.bmp)|*.png;*.jpg;*.jpeg;*.webp;*.gif;*.bmp|All files (*.*)|*.*";

        private sealed class NeuronBranchNode
        {
            public string Label { get; init; } = "";
            public int Weight { get; set; }
            public double OrbitAngle { get; set; }
            public double OrbitRadius { get; set; }
            public int LastSeenTick { get; set; }
            public int SourceIndex { get; set; } = 1;
        }

        private static readonly Color[] NeuronNodeColors =
        [
            Color.FromRgb(0xC8, 0xA9, 0x6A), // 0 User       – warm amber
            Color.FromRgb(0x5B, 0x8D, 0xB8), // 1 Chat       – steel blue
            Color.FromRgb(0xC9, 0x88, 0x2A), // 2 Workplace  – golden amber
            Color.FromRgb(0x4A, 0x9E, 0x8F), // 3 Documents  – teal
            Color.FromRgb(0x8B, 0x6B, 0xAE), // 4 Study      – soft purple
            Color.FromRgb(0xD4, 0x70, 0x3A), // 5 Calculator – burnt orange
        ];

        private readonly ObservableCollection<ChatMessage> _chatMessages = new();
        private ChatMessage _currentStreamingMessage;
        private readonly SmartContextCompactionEngine _compactionEngine = new();
        private const string KvStateFolder = "ChatHistory\\KvStates";
        private const int StreamFlushTokenThreshold = 16;
        private const int StreamUiFlushIntervalMs = 75;
        private bool _normalThinkingModeEnabled;
        private bool _normalWebSearchEnabled = false;
        private bool _chatScrollPending;

        public MainWindow()
        {
            try
            {
                InitializeComponent();
                LoadEmptyChatLogo();
                TemperatureSlider.ValueChanged += InferenceSettingsSlider_ValueChanged;
                TopPSlider.ValueChanged += InferenceSettingsSlider_ValueChanged;
                CtxSlider.ValueChanged += InferenceSettingsSlider_ValueChanged;
                CacheLocalInferenceSettings();
                SetRandomEmptyChatGreeting();

                try
                {
                    _database = new DatabaseService();
                }

                catch (Exception ex)
                {
                    Debug.WriteLine($"Database init error: {ex.Message}");
                    _database = null;
                }

                try
                {
                    _jsonPersistence = new JsonChatPersistence();
                    Directory.CreateDirectory(KvStateFolder);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"JSON persistence init error: {ex.Message}");
                    _jsonPersistence = null;
                }

                try
                {
                    LoadThemePreference();
                    InitializeTheme();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Theme init error: {ex.Message}");
                }

                try
                {
                    ChatDisplay.ItemsSource = _chatMessages;
                    AddChatMessage("system", "Axiom initialized. Ready to import model.");
                    AddChatMessage("system", $"Backend: {NativeBackendInit.DiagnosticMessage}");
                    ShowFirstRunQwen3PromptIfNeeded();
                    _chatMessages.CollectionChanged += (_, _) => UpdateEmptyChatGreetingVisibility();
                    UpdateEmptyChatGreetingVisibility();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"ChatDisplay init error: {ex.Message}");
                }

                RefreshNormalThinkingToggleUi();
                RefreshNormalWebToggleUi();
                LoadOpenRouterSettings();
                LoadStoredOpenRouterApiKey();
                RefreshCloudModeToggleUi();
                RefreshInferenceSettingsUi();

                _personaMemoryViewModel = new PersonaMemoryViewModel(_personaMemoryService);
                PersonaMemoryView.DataContext = _personaMemoryViewModel;
                _ = _personaMemoryViewModel.InitializeAsync();

                PurgeKvStateFromPreviousRun();
                _ = InitializePersistedNormalChatsAsync();
                LoadChatAdvancedState();
                InitializeBranchingIfEmpty();
                InitializePromptAndPresetDefaults();
                PopulateNextMessageModelSelector();
                UpdateTokenUsageIndicator();
                InitializeWorkplaceChats();
                SmartCompactionToggle.IsChecked = _compactionEngine.IsEnabled;

                _ = InitializeProcessingModeAsync();
                _neuronTimer.Interval = TimeSpan.FromMilliseconds(750);
                _neuronTimer.Tick += (_, _) => UpdateNeuronMap();
                _toolActivityTimer.Interval = TimeSpan.FromMilliseconds(500);
                _toolActivityTimer.Tick += ToolActivityTimer_Tick;
                _chatMessages.CollectionChanged += ChatMessages_CollectionChanged;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"CRITICAL: MainWindow constructor failed: {ex}");
                MessageBox.Show($"Failed to initialize application:\n\n{ex.Message}\n\n{ex.StackTrace}", "Startup Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void SidebarCollapseToggle_Click(object sender, RoutedEventArgs e)
        {
            if (_isSidebarAnimating)
                return;

            _isSidebarCollapsed = !_isSidebarCollapsed;
            AnimateSidebar(_isSidebarCollapsed);
            SidebarCollapseButton.Content = _isSidebarCollapsed ? "▸" : "◂";
        }

        private void AnimateSidebar(bool collapse)
        {
            _isSidebarAnimating = true;
            var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };

            if (SidebarBorder.RenderTransform is not TranslateTransform)
                SidebarBorder.RenderTransform = new TranslateTransform(0, 0);

            if (!collapse)
            {
                SidebarColumn.Width = new GridLength(304);
                SidebarBorder.Visibility = Visibility.Visible;
                SidebarBorder.Opacity = 0;
                SidebarBorder.RenderTransform = new TranslateTransform(-24, 0);
            }

            var opacityAnim = new DoubleAnimation
            {
                Duration = TimeSpan.FromMilliseconds(180),
                To = collapse ? 0 : 1,
                EasingFunction = ease
            };

            var slideAnim = new DoubleAnimation
            {
                Duration = TimeSpan.FromMilliseconds(180),
                To = collapse ? -24 : 0,
                EasingFunction = ease
            };

            opacityAnim.Completed += (_, _) =>
            {
                if (collapse)
                {
                    SidebarBorder.Visibility = Visibility.Collapsed;
                    SidebarColumn.Width = new GridLength(0);
                }
                _isSidebarAnimating = false;
            };

            SidebarBorder.BeginAnimation(UIElement.OpacityProperty, opacityAnim);
            ((TranslateTransform)SidebarBorder.RenderTransform).BeginAnimation(TranslateTransform.XProperty, slideAnim);
        }



        private bool ShouldUseDocumentContext(string query) => ShouldUseDocumentContext(query, _chatDocuments);

        private string BuildAttachmentKindSummary()
        {
            if (_chatDocuments.Count == 0)
                return string.Empty;

            int textCount = _chatDocuments.Count(doc => !doc.IsImage);
            int imageCount = _chatDocuments.Count(doc => doc.IsImage);
            var parts = new List<string>();
            if (textCount > 0)
                parts.Add(textCount == 1 ? "1 text/file document" : $"{textCount} text/file documents");
            if (imageCount > 0)
                parts.Add(imageCount == 1 ? "1 image" : $"{imageCount} images");

            return string.Join(" and ", parts);
        }

        private void UpsertChatAttachment(ChatDocumentAttachment attachment)
        {
            if (attachment == null || string.IsNullOrWhiteSpace(attachment.Name))
                return;

            _chatDocuments.RemoveAll(d => string.Equals(d.Name, attachment.Name, StringComparison.OrdinalIgnoreCase));
            _chatDocuments.Add(attachment);
        }

        private bool ShouldInjectPersistentDocumentContext(string currentUserMessage)
            => ShouldInjectPersistentDocumentContext(currentUserMessage, _chatDocuments, _chatMessages);

        private string BuildDocumentRetrievalQuery(string currentUserMessage)
            => BuildDocumentRetrievalQuery(currentUserMessage, _chatMessages, _chatDocuments);

        private bool MentionsAttachedDocumentName(string text)
            => MentionsAttachedDocumentName(text, _chatDocuments);

        private static bool ContainsDocumentFollowUpCue(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return false;

            string lower = text.ToLowerInvariant();
            string[] followUpCues =
            [
                "that document", "that file", "those documents", "those files", "the attachment", "attached file",
                "attached document", "continue", "go on", "expand", "elaborate", "more on that", "what about that"
            ];

            return followUpCues.Any(lower.Contains);
        }

        private void RebuildChatDocumentIndex()
        {
            _documentRetriever.ClearChunks();

            foreach (var doc in _chatDocuments)
            {
                // Images only carry a placeholder summary — indexing it pollutes retrieval.
                if (!doc.HasTextContent || doc.IsImage)
                    continue;

                List<DocumentChunk> chunks = DocumentChunker.ChunkDocument(doc.Name, doc.Content);
                if (chunks.Count > 0)
                    _documentRetriever.AddChunks(chunks);
            }
        }

        private string BuildAttachedDocumentMemoryBlock()
        {
            if (_chatDocuments.Count == 0)
                return string.Empty;

            string names = string.Join(", ", _chatDocuments.Select(d => d.Name).Take(6));
            if (_chatDocuments.Count > 6)
                names += $", +{_chatDocuments.Count - 6} more";

            bool hasLocalVisionImages = !_cloudModeActive
                && _mtmdWeights != null
                && _chatDocuments.Any(doc => doc.IsImage && !string.IsNullOrWhiteSpace(doc.Base64Data));

            return "[ATTACHED DOCUMENT MEMORY] The following documents remain attached to this chat and should be treated as persistent conversation context for follow-up questions: "
                + names
                + ". If document excerpts are provided for the current turn, use them directly instead of saying the files are unavailable."
                + (HasVisionAttachmentForCloudTurn() || hasLocalVisionImages
                    ? " Image attachments are provided to you directly in this chat — analyze them when relevant."
                    : string.Empty);
        }

        private string BuildPersistentDocumentContextBlock(string currentUserMessage, bool isCloudMode)
            => BuildPersistentDocumentContextBlock(
                currentUserMessage,
                isCloudMode,
                _chatDocuments,
                _chatMessages,
                (int)Math.Max(512, CtxSlider?.Value ?? 2048));

        private async void DeleteChat_Click(object sender, RoutedEventArgs e)
        {
            if (_currentChatId < 0)
                return;

            int deleteId = _currentChatId;
            _chatContent.Remove(deleteId);
            _database?.DeleteChat(deleteId);
            if (_jsonPersistence != null)
            {
                await _jsonPersistence.DeleteChatAsync(deleteId);
            }

            try
            {
                string chatKv = GetChatKvStatePath(deleteId);
                if (File.Exists(chatKv))
                    File.Delete(chatKv);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Delete chat KV cleanup warning: {ex.Message}");
            }

            var toRemove = ChatHistoryStack.Children
                .OfType<Button>()
                .FirstOrDefault(b => b.Tag is int id && id == deleteId);
            if (toRemove != null)
                ChatHistoryStack.Children.Remove(toRemove);

            if (_chatContent.Count == 0)
            {
                NewChat_Click(sender, e);
            }
            else
            {
                int nextId = _chatContent.Keys.OrderBy(k => k).First();
                string nextName = _chatNames.TryGetValue(nextId, out string? storedName) ? storedName : $"Chat{nextId}";
                await LoadChatAsync(nextId, nextName);
            }
            _ = QueueWorkspaceStateSaveAsync();
            SaveChatAdvancedState();
        }

        private void CopyMessage_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button b || b.Tag is not ChatMessage msg)
                return;

            if (!string.Equals(msg.Role, "assistant", StringComparison.OrdinalIgnoreCase))
                return;

            try
            {
                Clipboard.SetText(msg.Content ?? string.Empty);
                AppendSystemMessage("Copied assistant response.");
            }
            catch (Exception ex)
            {
                AppendSystemMessage($"Copy failed: {ex.Message}");
            }
        }

        private async void ImportChatDocument_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Filter = NormalChatAttachmentDialogFilter,
                Multiselect = true
            };

            if (dialog.ShowDialog() != true)
                return;

            int importedCount = 0;
            foreach (string file in dialog.FileNames)
            {
                try
                {
                    ChatAttachmentImportResult result = await ChatAttachmentImportService.ImportAsync(file);
                    UpsertChatAttachment(result.Attachment);
                    importedCount++;
                }
                catch (Exception ex)
                {
                    AppendSystemMessage($"Attachment import failed for {Path.GetFileName(file)}: {ex.Message}");
                }
            }

            RebuildChatDocumentIndex();

            if (importedCount > 0)
            {
                string summary = BuildAttachmentKindSummary();
                bool hasImages = _chatDocuments.Any(d => d.IsImage);
                string imageNote = !hasImages
                    ? string.Empty
                    : _cloudModeActive
                        ? " Images can be analyzed by cloud vision models."
                        : _mtmdWeights != null
                            ? " Images can be analyzed by the loaded local vision model."
                            : " Images need a vision model: place the matching mmproj .gguf next to the model file and re-import, or switch to cloud mode.";
                AppendSystemMessage($"Attachments ready: {_chatDocuments.Count} total ({summary}).{imageNote}");
                SaveCurrentChat();
            }
        }

        private void OpenPromptTemplates_Click(object sender, RoutedEventArgs e)
        {
            var categories = _promptTemplates.GroupBy(t => t.Category).OrderBy(g => g.Key).ToList();
            string summary = string.Join("\n\n", categories.Select(g => $"[{g.Key}]\n- " + string.Join("\n- ", g.Select(x => x.Text))));
            string? picked = PromptForText("Prompt Templates", "Paste/edit template text to insert into input. Use Add Custom by prefixing with custom:<category>:<text>", summary);
            if (string.IsNullOrWhiteSpace(picked)) return;

            if (picked.StartsWith("custom:", StringComparison.OrdinalIgnoreCase))
            {
                string payload = picked[7..];
                int idx = payload.IndexOf(':');
                if (idx > 0)
                {
                    string cat = payload[..idx].Trim();
                    string text = payload[(idx + 1)..].Trim();
                    if (!string.IsNullOrWhiteSpace(cat) && !string.IsNullOrWhiteSpace(text))
                    {
                        _promptTemplates.Add(new PromptTemplateEntry { Category = cat, Text = text, IsCustom = true });
                        SaveChatAdvancedState();
                        InputBox.Text = text;
                    }
                }
                return;
            }

            InputBox.Text = picked;
            InputBox.Focus();
            InputBox.SelectAll();
        }

        private void OpenSystemPromptEditor_Click(object sender, RoutedEventArgs e)
        {
            string presets = string.Join("\n", _systemPromptPresets.Select(p => $"- {p.Name}"));
            string? edited = PromptForText("System Prompt Editor", $"Edit active system prompt.\n\nPresets:\n{presets}\n\nUse preset:<name> to load preset. Use savepreset:<name> to save current text.", SystemPromptBox.Text);
            if (string.IsNullOrWhiteSpace(edited)) return;

            if (edited.StartsWith("preset:", StringComparison.OrdinalIgnoreCase))
            {
                string presetName = edited[7..].Trim();
                var preset = _systemPromptPresets.FirstOrDefault(p => string.Equals(p.Name, presetName, StringComparison.OrdinalIgnoreCase));
                if (preset != null)
                    SystemPromptBox.Text = preset.Prompt;
            }
            else if (edited.StartsWith("savepreset:", StringComparison.OrdinalIgnoreCase))
            {
                string name = edited[11..].Trim();
                if (!string.IsNullOrWhiteSpace(name))
                {
                    _systemPromptPresets.Add(new SystemPromptPresetEntry { Name = name, Prompt = SystemPromptBox.Text, IsBuiltIn = false });
                    SaveChatAdvancedState();
                }
            }
            else
            {
                SystemPromptBox.Text = edited;
            }

            RebuildChatSession();
            SaveChatAdvancedState();
        }

        private void PinMessage_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button b || b.Tag is not ChatMessage msg || !string.Equals(msg.Role, "assistant", StringComparison.OrdinalIgnoreCase))
                return;

            if (_pinnedMessages.Any(p => p.Content == msg.Content && p.Timestamp == msg.Timestamp))
                return;

            _pinnedMessages.Add(new PinnedMessageEntry { Role = msg.Role, Content = msg.Content, Timestamp = msg.Timestamp });
            msg.IsPinned = true;
            msg.IsCompactionProtected = true;
            msg.Importance = MessageImportance.High;
            SaveChatAdvancedState();
        }

        private void ClearReferences_Click(object sender, RoutedEventArgs e)
        {
            _pinnedMessages.Clear();
            SaveChatAdvancedState();
        }

        private void ExpandReference_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button b && b.Tag is PinnedMessageEntry p)
            {
                MessageBox.Show(p.Content, $"Pinned Message ({p.Timestamp:HH:mm:ss})", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private void CopyReference_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button b && b.Tag is PinnedMessageEntry p)
            {
                try { Clipboard.SetText(p.Content ?? ""); } catch { }
            }
        }

        private void UnpinReference_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button b && b.Tag is PinnedMessageEntry p)
            {
                _pinnedMessages.Remove(p);
                SaveChatAdvancedState();
            }
        }

        private void BranchMessage_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button b || b.Tag is not ChatMessage msg)
                return;

            int idx = _chatMessages.IndexOf(msg);
            if (idx < 0) return;

            string label = _chatMessages.Skip(idx + 1).FirstOrDefault(m => m.Role == "user")?.Content ?? "Branch";
            if (label.Length > 24) label = label[..24] + "...";

            var branch = new ChatBranch
            {
                Name = string.IsNullOrWhiteSpace(label) ? $"Branch {_branches.Count}" : label,
                ForkMessageIndex = idx,
                Messages = _chatMessages.Take(idx + 1).Select(m => new ChatMessageState { Id = m.Id, Role = m.Role, Content = m.Content, ThinkingContent = m.ThinkingContent, ThinkingHeaderText = m.ThinkingHeaderText, ModelLabel = m.ModelLabel, Timestamp = m.Timestamp }).ToList()
            };
            _branches.Add(branch);
            _activeBranchId = branch.Id;
            RefreshBranchNavigator();
            SaveChatAdvancedState();
            AppendSystemMessage($"Created branch '{branch.Name}'.");
            _ = CloneKvStateForBranchAsync(branch.Id);
        }

        private async void BranchNavigatorCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (BranchNavigatorCombo.SelectedItem is not ChatBranch branch)
                return;

            await PersistCurrentChatSessionAsync();
            _activeBranchId = branch.Id;
            _chatMessages.Clear();
            foreach (var m in branch.Messages)
            {
                _chatMessages.Add(new ChatMessage(m.Role, m.Content)
                {
                    Id = m.Id,
                    ThinkingContent = m.ThinkingContent,
                    ThinkingHeaderText = string.IsNullOrWhiteSpace(m.ThinkingHeaderText) ? "Thinking" : m.ThinkingHeaderText,
                    ModelLabel = m.ModelLabel,
                    Timestamp = m.Timestamp
                });
            }

            await TryLoadKvStateForBranchAsync(branch.Id, CancellationToken.None);

            UpdateTokenUsageIndicator();
            SaveChatAdvancedState();
        }

        private void LoadThemePreference()
        {
            string themeSetting = _database?.GetSetting("theme") ?? "";
            _isDarkMode = string.IsNullOrEmpty(themeSetting) || themeSetting == "dark";
        }

        private void InitializeTheme()
        {
            if (_isDarkMode)
                ApplyDarkMode();
            else
                ApplyLightMode();
        }

        private void ApplyDarkMode()
        {
            try
            {
                _isDarkMode = true;
                if (_database != null)
                {
                    _database.SaveSetting("theme", "dark");
                }
                this.Background = new SolidColorBrush((Color)this.Resources["DarkBackground"]);
                // Theme toggle button removed from UI
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"ApplyDarkMode error: {ex.Message}");
            }
        }

        private void ApplyLightMode()
        {
            try
            {
                _isDarkMode = false;
                if (_database != null)
                {
                    _database.SaveSetting("theme", "light");
                }
                this.Background = new SolidColorBrush((Color)this.Resources["DarkBackground"]);
                // Theme toggle button removed from UI
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"ApplyLightMode error: {ex.Message}");
            }
        }

        private void SetDarkMode_Click(object sender, RoutedEventArgs e) => ApplyDarkMode();
        private void SetLightMode_Click(object sender, RoutedEventArgs e) => ApplyLightMode();

        private void Settings_Click(object sender, RoutedEventArgs e)
        {
            if (_isSettingsAnimating)
                return;

            bool shouldShow = SettingsPanel.Visibility != Visibility.Visible;
            AnimateSettingsPanel(shouldShow);

            if (shouldShow)
                _ = EnsureOpenRouterUsageLoadedAsync();
        }

        private void AnimateSettingsPanel(bool show)
        {
            _isSettingsAnimating = true;

            if (show)
            {
                SettingsPanel.Visibility = Visibility.Visible;
            }

            var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };
            var opacityAnimation = new DoubleAnimation
            {
                Duration = TimeSpan.FromMilliseconds(180),
                From = show ? 0 : 1,
                To = show ? 1 : 0,
                EasingFunction = ease
            };

            var slideAnimation = new DoubleAnimation
            {
                Duration = TimeSpan.FromMilliseconds(180),
                From = show ? 28 : 0,
                To = show ? 0 : 28,
                EasingFunction = ease
            };

            var storyboard = new Storyboard();
            storyboard.Children.Add(opacityAnimation);
            storyboard.Children.Add(slideAnimation);

            Storyboard.SetTarget(opacityAnimation, SettingsPanel);
            Storyboard.SetTargetProperty(opacityAnimation, new PropertyPath(UIElement.OpacityProperty));
            Storyboard.SetTarget(slideAnimation, SettingsPanelCard);
            Storyboard.SetTargetProperty(slideAnimation, new PropertyPath("(UIElement.RenderTransform).(TranslateTransform.X)"));

            storyboard.Completed += (_, _) =>
            {
                if (!show)
                {
                    SettingsPanel.Visibility = Visibility.Collapsed;
                }

                _isSettingsAnimating = false;
            };

            storyboard.Begin();
        }

        private void NewChat_Click(object sender, RoutedEventArgs e)
        {
            _chatCounter++;
            _currentChatId = _chatCounter;
            string chatName = "New chat";

            _chatMessages.Clear();
            _chatDocuments.Clear();
            _documentRetriever.ClearChunks();
            SetRandomEmptyChatGreeting();
            _chatContent[_currentChatId] = "";
            _chatNames[_currentChatId] = chatName;

            // Start each new chat with a fresh branch lineage to avoid cross-topic KV bleed.
            _branches.Clear();
            _activeBranchId = Guid.Empty;
            InitializeBranchingIfEmpty();
            RefreshBranchNavigator();

            if (_model != null && _activeModelParams != null)
            {
                _ = ResetExecutorContextAsync(CancellationToken.None);
            }

            _database?.SaveChat(_currentChatId, chatName, "");

            // Register in the JSON index immediately so the chat survives a restart
            // even if the user closes before sending any messages.
            if (_jsonPersistence != null)
            {
                int idToRegister = _currentChatId;
                string nameToRegister = chatName;
                _ = Task.Run(() => _jsonPersistence.SaveChat(new ChatSession(nameToRegister)
                {
                    Id = idToRegister,
                    Name = nameToRegister,
                    CreatedAt = DateTime.Now,
                    UpdatedAt = DateTime.Now
                }));
            }

            AddChatToHistory(chatName, _currentChatId);
            InputBox.Focus();
            UpdateNormalChatChrome();
        }

        private void SetRandomEmptyChatGreeting()
        {
            if (EmptyChatGreetings.Length == 0)
                return;

            if (FindName("EmptyChatGreetingText") is TextBlock emptyChatGreetingText)
                emptyChatGreetingText.Text = EmptyChatGreetings[new Random().Next(EmptyChatGreetings.Length)];

            UpdateEmptyChatGreetingVisibility();
        }

        private void UpdateEmptyChatGreetingVisibility()
        {
            if (FindName("EmptyChatGreetingPanel") is not StackPanel emptyChatGreetingPanel)
                return;

            emptyChatGreetingPanel.Visibility = _chatMessages.Count == 0
                ? Visibility.Visible
                : Visibility.Collapsed;

            if (_chatMessages.Count == 0 && EmptyChatLogoImage != null && _emptyChatLogoSource != null)
                EmptyChatLogoImage.Visibility = Visibility.Visible;

            UpdateNormalChatChrome();
        }

        private void SidebarHistoryToggle_Click(object sender, RoutedEventArgs e)
        {
            if (_isHistorySectionAnimating)
                return;

            _isHistorySectionExpanded = !_isHistorySectionExpanded;
            AnimateSidebarHistorySection(_isHistorySectionExpanded);
        }

        private void AnimateSidebarHistorySection(bool expand)
        {
            _isHistorySectionAnimating = true;

            if (expand)
            {
                ChatHistorySectionContainer.Visibility = Visibility.Visible;
                HistorySectionToggle.Content = "▾ Recents";
            }
            else
            {
                HistorySectionToggle.Content = "▸ Recents";
            }

            var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };

            var maxHeightAnim = new DoubleAnimation
            {
                Duration = TimeSpan.FromMilliseconds(180),
                From = expand ? 0 : 500,
                To = expand ? 500 : 0,
                EasingFunction = ease
            };

            var opacityAnim = new DoubleAnimation
            {
                Duration = TimeSpan.FromMilliseconds(150),
                From = expand ? 0 : 1,
                To = expand ? 1 : 0,
                EasingFunction = ease
            };

            opacityAnim.Completed += (_, _) =>
            {
                if (!expand)
                {
                    ChatHistorySectionContainer.Visibility = Visibility.Collapsed;
                }

                _isHistorySectionAnimating = false;
            };

            ChatHistorySectionContainer.BeginAnimation(FrameworkElement.MaxHeightProperty, maxHeightAnim);
            ChatHistorySectionContainer.BeginAnimation(UIElement.OpacityProperty, opacityAnim);
        }

        private void WorkplaceSidebarHistoryToggle_Click(object sender, RoutedEventArgs e)
        {
            if (_isWorkplaceHistorySectionAnimating)
                return;

            _isWorkplaceHistorySectionExpanded = !_isWorkplaceHistorySectionExpanded;
            AnimateWorkplaceSidebarHistorySection(_isWorkplaceHistorySectionExpanded);
        }

        private void AnimateWorkplaceSidebarHistorySection(bool expand)
        {
            _isWorkplaceHistorySectionAnimating = true;

            if (expand)
            {
                WorkplaceHistorySectionContainer.Visibility = Visibility.Visible;
                WorkplaceHistorySectionToggle.Content = "▾ Workplace Chats";
            }
            else
            {
                WorkplaceHistorySectionToggle.Content = "▸ Workplace Chats";
            }

            var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };
            var maxHeightAnim = new DoubleAnimation
            {
                Duration = TimeSpan.FromMilliseconds(180),
                From = expand ? 0 : 420,
                To = expand ? 420 : 0,
                EasingFunction = ease
            };

            var opacityAnim = new DoubleAnimation
            {
                Duration = TimeSpan.FromMilliseconds(150),
                From = expand ? 0 : 1,
                To = expand ? 1 : 0,
                EasingFunction = ease
            };

            opacityAnim.Completed += (_, _) =>
            {
                if (!expand)
                    WorkplaceHistorySectionContainer.Visibility = Visibility.Collapsed;

                _isWorkplaceHistorySectionAnimating = false;
            };

            WorkplaceHistorySectionContainer.BeginAnimation(FrameworkElement.MaxHeightProperty, maxHeightAnim);
            WorkplaceHistorySectionContainer.BeginAnimation(UIElement.OpacityProperty, opacityAnim);
        }

        private void InitializeWorkplaceChats()
        {
            if (_loadedPersistedWorkplaceChats && _workplaceChats.Count > 0)
            {
                RebuildWorkplaceHistoryUi();
                int idToLoad = _currentWorkplaceChatId > 0 && _workplaceChats.ContainsKey(_currentWorkplaceChatId)
                    ? _currentWorkplaceChatId
                    : _workplaceChats.Keys.OrderBy(k => k).First();
                LoadWorkplaceChat(idToLoad, _workplaceChatNames.TryGetValue(idToLoad, out var savedName) ? savedName : $"Workplace {idToLoad}");
                return;
            }

            _workplaceChatCounter = 1;
            _currentWorkplaceChatId = 1;
            _workplaceChats[_currentWorkplaceChatId] = WorkplaceViewControl.CaptureSnapshot();
            _workplaceChatNames[_currentWorkplaceChatId] = "Workplace 1";
            AddWorkplaceToHistory("Workplace 1", _currentWorkplaceChatId);
        }

        private async void NewWorkplace_Click(object sender, RoutedEventArgs e)
        {
            SaveCurrentWorkplaceChat();

            _workplaceChatCounter++;
            _currentWorkplaceChatId = _workplaceChatCounter;
            string name = $"Workplace {_workplaceChatCounter}";

            WorkplaceViewControl.ResetWorkspaceSession();
            _workplaceChats[_currentWorkplaceChatId] = WorkplaceViewControl.CaptureSnapshot();
            _workplaceChatNames[_currentWorkplaceChatId] = name;
            AddWorkplaceToHistory(name, _currentWorkplaceChatId);

            // Await the save so the new workplace is guaranteed to persist even if
            // a background chat save holds the coordinated-persistence gate right now.
            await QueueCoordinatedChatPersistenceAsync(false, false, true, false);
        }

        private async void DeleteWorkplace_Click(object sender, RoutedEventArgs e)
        {
            if (_currentWorkplaceChatId < 0 || _workplaceChats.Count <= 1)
                return;

            int deleteId = _currentWorkplaceChatId;
            _workplaceChats.Remove(deleteId);
            _workplaceChatNames.Remove(deleteId);

            var toRemove = WorkplaceHistoryStack.Children
                .OfType<Button>()
                .FirstOrDefault(b => b.Tag is int id && id == deleteId);
            if (toRemove != null)
                WorkplaceHistoryStack.Children.Remove(toRemove);

            // CRITICAL: Invalidate _currentWorkplaceChatId BEFORE calling LoadWorkplaceChat.
            // LoadWorkplaceChat calls SaveCurrentWorkplaceChat() at its top, which does
            // _workplaceChats[_currentWorkplaceChatId] = CaptureSnapshot(). Without this
            // guard the just-deleted entry would be silently re-added to the dict and
            // persisted, causing it to reappear on the next run.
            _currentWorkplaceChatId = -1;

            int next = _workplaceChats.Keys.OrderBy(k => k).First();
            string nextName = _workplaceChatNames.TryGetValue(next, out string? n) ? n : $"Workplace {next}";
            LoadWorkplaceChat(next, nextName);

            // Await the save so the deletion is guaranteed to be persisted immediately.
            await QueueCoordinatedChatPersistenceAsync(false, false, true, false);
        }

        private void AddWorkplaceToHistory(string name, int workplaceId)
        {
            var button = new Button
            {
                Content = name,
                Style = (Style)this.Resources["SubtleButtonStyle"],
                Height = 34,
                Margin = new Thickness(0, 0, 0, 6),
                Tag = workplaceId
            };

            button.Click += (_, _) => LoadWorkplaceChat(workplaceId, name);
            WorkplaceHistoryStack.Children.Add(button);
            SetActiveWorkplaceButton(button);
        }

        private void RebuildWorkplaceHistoryUi()
        {
            WorkplaceHistoryStack.Children.Clear();
            _activeWorkplaceButton = null;

            foreach (var id in _workplaceChats.Keys.OrderBy(k => k))
            {
                string name = _workplaceChatNames.TryGetValue(id, out var n) ? n : $"Workplace {id}";
                AddWorkplaceToHistory(name, id);
            }
        }

        private void SetActiveWorkplaceButton(Button button)
        {
            if (_activeWorkplaceButton != null)
            {
                _activeWorkplaceButton.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#302D2A"));
                _activeWorkplaceButton.BorderThickness = new Thickness(1);
            }

            button.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#B8924A"));
            button.BorderThickness = new Thickness(1);

            _activeWorkplaceButton = button;
        }

        private void SaveCurrentWorkplaceChat()
        {
            if (_currentWorkplaceChatId < 0)
                return;

            _workplaceChats[_currentWorkplaceChatId] = WorkplaceViewControl.CaptureSnapshot();
            if (!_workplaceChatNames.ContainsKey(_currentWorkplaceChatId))
                _workplaceChatNames[_currentWorkplaceChatId] = $"Workplace {_currentWorkplaceChatId}";
        }

        private void LoadWorkplaceChat(int workplaceId, string workplaceName)
        {
            SaveCurrentWorkplaceChat();

            if (!_workplaceChats.TryGetValue(workplaceId, out var snapshot))
                return;

            _currentWorkplaceChatId = workplaceId;
            WorkplaceViewControl.RestoreSnapshot(snapshot);

            foreach (UIElement child in WorkplaceHistoryStack.Children)
            {
                if (child is Button btn && btn.Tag is int id && id == workplaceId)
                {
                    SetActiveWorkplaceButton(btn);
                    break;
                }
            }

            AppendSystemMessage($"Loaded {workplaceName}");
        }

        private void AddChatToHistory(string chatName, int chatId)
        {
            Button chatButton = new Button
            {
                Content = chatName,
                Style = (Style)this.Resources["ChatHistoryItemButtonStyle"],
                Height = 38,
                Margin = new Thickness(0, 2, 0, 2),
                Tag = chatId
            };

            _chatNames[chatId] = chatName;
            
            chatButton.Click += ChatHistoryButton_Click;
            ChatHistoryStack.Children.Add(chatButton);
            SetActiveChatButton(chatButton);
        }

        private void SetActiveChatButton(Button button)
        {
            if (button == null)
            {
                Debug.WriteLine("SetActiveChatButton: button parameter is null");
                return;
            }

            try
            {
                if (_activeChatButton != null)
                {
                    _activeChatButton.Background = Brushes.Transparent;
                    _activeChatButton.BorderBrush = Brushes.Transparent;
                    _activeChatButton.BorderThickness = new Thickness(1);
                }

                button.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#B8924A"));
                button.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#302D2A"));
                button.BorderThickness = new Thickness(1);

                _activeChatButton = button;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"SetActiveChatButton error: {ex.Message}");
            }
        }

        private async Task LoadChatAsync(int chatId, string chatName)
        {
            _currentChatId = chatId;
            _chatMessages.Clear();
            _chatDocuments.Clear();
            _documentRetriever.ClearChunks();
            _chatNames[chatId] = chatName;

            ChatSession? persisted = null;
            if (_jsonPersistence != null)
            {
                persisted = await _jsonPersistence.LoadChatAsync(chatId);
            }

            if (persisted?.Messages != null && persisted.Messages.Count > 0)
            {
                foreach (var m in persisted.Messages)
                {
                    _chatMessages.Add(new ChatMessage(m.Role, m.Content)
                    {
                        Id = m.Id,
                        Timestamp = m.Timestamp,
                        ModelLabel = m.ModelLabel,
                        ThinkingContent = m.ThinkingContent,
                        ThinkingHeaderText = string.IsNullOrWhiteSpace(m.ThinkingHeaderText) ? "Thinking" : m.ThinkingHeaderText,
                        IsPinned = m.IsPinned,
                        IsCompactionProtected = m.IsCompactionProtected,
                        Importance = m.Importance,
                        IsCompactionMarker = m.IsCompactionMarker,
                        CompactionSummaries = m.CompactionSummaries
                    });
                }

                foreach (var doc in persisted.AttachedDocuments ?? [])
                {
                    if (!string.IsNullOrWhiteSpace(doc?.Name) && !string.IsNullOrWhiteSpace(doc.Content))
                        _chatDocuments.Add(doc);
                }

                RebuildChatDocumentIndex();
            }
            else if (_chatContent.ContainsKey(chatId))
            {
                AddChatMessage("system", $"Loaded chat: {chatName}");
            }

            await TryLoadKvStateForCurrentChatAsync(CancellationToken.None);

            if (ChatHistoryStack.Children.Count > 0)
            {
                foreach (UIElement child in ChatHistoryStack.Children)
                {
                    if (child is Button btn && (int)btn.Tag == chatId)
                    {
                        SetActiveChatButton(btn);
                        break;
                    }
                }
            }

            UpdateTokenUsageIndicator();
            ScrollChatToEnd();
            UpdateNormalChatChrome();
        }

        private static string BuildAutomaticChatTitle(string? userMessage)
        {
            if (string.IsNullOrWhiteSpace(userMessage))
                return "New chat";

            string title = Regex.Replace(userMessage.Trim(), @"\s+", " ");
            title = title.Trim('"', '\'', '.', ',', ':', ';', '-', '—');

            if (title.Length > 34)
                title = title[..34].TrimEnd() + "...";

            return string.IsNullOrWhiteSpace(title) ? "New chat" : title;
        }

        private void UpdateCurrentChatTitle(string userMessage)
        {
            if (_currentChatId < 0)
                return;

            string currentName = _chatNames.TryGetValue(_currentChatId, out string existingName)
                ? existingName
                : string.Empty;

            bool shouldAutoRename = string.IsNullOrWhiteSpace(currentName)
                || string.Equals(currentName, "New chat", StringComparison.OrdinalIgnoreCase)
                || Regex.IsMatch(currentName, @"^Chat\d+$", RegexOptions.IgnoreCase);

            if (!shouldAutoRename)
                return;

            string title = BuildAutomaticChatTitle(userMessage);
            _chatNames[_currentChatId] = title;

            Button? chatButton = ChatHistoryStack.Children
                .OfType<Button>()
                .FirstOrDefault(b => b.Tag is int id && id == _currentChatId);

            if (chatButton != null)
            {
                chatButton.Content = title;
                chatButton.Click -= ChatHistoryButton_Click;
                chatButton.Click += ChatHistoryButton_Click;
            }
        }

        private async void ChatHistoryButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button || button.Tag is not int chatId)
                return;

            string chatName = button.Content?.ToString() ?? (_chatNames.TryGetValue(chatId, out string savedName) ? savedName : $"Chat{chatId}");
            await LoadChatAsync(chatId, chatName);
        }

        private string GetActiveModelDisplayName()
        {
            if (_cloudModeActive)
            {
                // Use the user's selected model label, not the internally detected one,
                // so the header always reflects the model the user actually chose.
                string selectedLabel = _openRouterChatService.ResolveModelLabel(_selectedOpenRouterModelId);
                return string.IsNullOrWhiteSpace(selectedLabel)
                    ? (string.IsNullOrWhiteSpace(_selectedOpenRouterModelId) ? OpenRouterChatService.DefaultModelLabel : _selectedOpenRouterModelId)
                    : selectedLabel;
            }

            return string.IsNullOrWhiteSpace(_modelName) ? "No Model Loaded" : _modelName;
        }

        private void AppendSystemMessage(string message)
        {
            AddChatMessage("system", message);
            ScrollChatToEnd();
            SaveCurrentChat();
        }

        private sealed class PendingChatPersistencePayload
        {
            public int ChatId { get; init; }
            public string ConversationTranscript { get; init; } = string.Empty;
        }

        private sealed class CoordinatedChatPersistenceSnapshot
        {
            public PendingChatPersistencePayload? PendingPayload { get; init; }
            public ChatSession? ChatSession { get; init; }
            public ChatWorkspaceSnapshot? WorkspaceSnapshot { get; init; }
            public ChatAdvancedStateSnapshot? AdvancedSnapshot { get; init; }
            public Guid ActiveBranchId { get; init; }
            public bool CanSaveKvState { get; init; }
        }

        private void ChatMessages_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            RefreshCloudModeToggleUi();
        }

        private async Task InitializePersistedNormalChatsAsync()
        {
            try
            {
                var index = _jsonPersistence?.GetChatIndex() ?? new List<ChatIndexEntry>();
                if (index.Count == 0)
                {
                    await LoadWorkspaceStateAsync();
                    if (_currentChatId < 0)
                    {
                        NewChat_Click(this, new RoutedEventArgs());
                    }
                    return;
                }

                ChatHistoryStack.Children.Clear();
                _activeChatButton = null;
                _chatContent.Clear();

                foreach (var entry in index.OrderBy(e => e.Id))
                {
                    _chatCounter = Math.Max(_chatCounter, entry.Id);
                    _chatContent[entry.Id] = string.Empty;
                    AddChatToHistory(string.IsNullOrWhiteSpace(entry.Name) ? $"Chat{entry.Id}" : entry.Name, entry.Id);
                }

                int idToLoad = _currentChatId > 0 && index.Any(i => i.Id == _currentChatId)
                    ? _currentChatId
                    : index.OrderByDescending(i => i.UpdatedAt).First().Id;
                string nameToLoad = index.FirstOrDefault(i => i.Id == idToLoad)?.Name ?? $"Chat{idToLoad}";
                await LoadChatAsync(idToLoad, nameToLoad);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"InitializePersistedNormalChatsAsync error: {ex.Message}");
                if (_currentChatId < 0)
                {
                    NewChat_Click(this, new RoutedEventArgs());
                }
            }
        }

        private void PurgeKvStateFromPreviousRun()
        {
            try
            {
                if (!Directory.Exists(KvStateFolder))
                    return;

                foreach (var file in Directory.EnumerateFiles(KvStateFolder, "*.kvstate"))
                {
                    try { File.Delete(file); } catch { }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"PurgeKvStateFromPreviousRun warning: {ex.Message}");
            }
        }

        private static string GetMostRelevantError(Exception ex)
        {
            Exception current = ex;
            while (current.InnerException != null)
            {
                current = current.InnerException;
            }

            return current.Message;
        }

        private static bool ShouldUseDocumentContext(string query, IReadOnlyCollection<ChatDocumentAttachment> chatDocuments)
        {
            // Any non-empty query with documents attached should be able to see them.
            return chatDocuments is { Count: > 0 } && !string.IsNullOrWhiteSpace(query);
        }

        private void InitializePromptAndPresetDefaults()
        {
            if (_promptTemplates.Count == 0)
                _promptTemplates.AddRange(ChatAdvancedStatePersistence.GetDefaultPromptTemplates());

            if (_systemPromptPresets.Count == 0)
                _systemPromptPresets.AddRange(ChatAdvancedStatePersistence.GetBuiltInSystemPromptPresets());
        }

        private void InitializeBranchingIfEmpty()
        {
            if (_branches.Count > 0)
                return;

            var main = new ChatBranch { Name = "Main", ForkMessageIndex = 0 };
            _branches.Add(main);
            _activeBranchId = main.Id;
            RefreshBranchNavigator();
        }

        private void RefreshBranchNavigator()
        {
            if (_branches.Count <= 1)
            {
                BranchNavigatorBorder.Visibility = Visibility.Collapsed;
                return;
            }

            BranchNavigatorBorder.Visibility = Visibility.Visible;
            BranchNavigatorCombo.ItemsSource = null;
            BranchNavigatorCombo.ItemsSource = _branches;
            BranchNavigatorCombo.DisplayMemberPath = "Name";
            BranchNavigatorCombo.SelectedItem = _branches.FirstOrDefault(b => b.Id == _activeBranchId);
        }

        private void PopulateNextMessageModelSelector()
        {
            // Next-model selector removed from UI.
            _nextMessageModelOverride = "Default";
        }

        private void UpdateTokenUsageIndicator()
        {
            int used = _cloudModeActive
                ? _openRouterChatService.EstimateConversationTokens(
                    _chatMessages
                        .Where(m => string.Equals(m.Role, "user", StringComparison.OrdinalIgnoreCase)
                                 || string.Equals(m.Role, "assistant", StringComparison.OrdinalIgnoreCase))
                        .Select(m => new OpenRouterMessage(m.Role, CleanAssistantContentForOpenRouterHistory(m.Content))))
                : _chatMessages.Sum(m => Math.Max(0, m.Content?.Length ?? 0)) / 4;
            Border? cloudContextNoticeBorder = FindName("CloudContextNoticeBorder") as Border;
            TextBlock? cloudContextNoticeText = FindName("CloudContextNoticeText") as TextBlock;

            if (_cloudModeActive)
            {
                TokenUsageProgressBar.Visibility = Visibility.Collapsed;
                TokenUsageLabel.Visibility = Visibility.Collapsed;
                CompactionHealthGrid.Visibility = Visibility.Collapsed;
                CompactionStatusLabel.Visibility = Visibility.Collapsed;
                UpdateCloudContextNotice(used);
                return;
            }

            if (cloudContextNoticeBorder != null)
                cloudContextNoticeBorder.Visibility = Visibility.Collapsed;
            if (cloudContextNoticeText != null)
                cloudContextNoticeText.Text = string.Empty;
            TokenUsageProgressBar.Visibility = Visibility.Visible;
            TokenUsageLabel.Visibility = Visibility.Visible;
            int context = (int)Math.Max(512, CtxSlider?.Value ?? 2048);
            double pct = context <= 0 ? 0 : Math.Clamp(used * 100d / context, 0, 100);
            TokenUsageProgressBar.Value = pct;
            TokenUsageLabel.Text = $"approximately {used} of {context} tokens used";

            if (pct >= 90)
            {
                TokenUsageProgressBar.Foreground = new SolidColorBrush(Color.FromRgb(255, 59, 59));
                TokenUsageProgressBar.ToolTip = "Warning: model may begin losing earlier context.";
            }
            else if (pct >= 75)
            {
                TokenUsageProgressBar.Foreground = new SolidColorBrush(Color.FromRgb(245, 158, 11));
                TokenUsageProgressBar.ToolTip = "High context usage.";
            }
            else
            {
                TokenUsageProgressBar.Foreground = new SolidColorBrush(Color.FromRgb(34, 197, 94));
                TokenUsageProgressBar.ToolTip = null;
            }

            // Update compaction health display
            if (_compactionEngine.IsEnabled)
            {
                CompactionHealthGrid.Visibility = Visibility.Visible;
                var (_, effectiveLimit, thresholdPoint, healthLabel) = _compactionEngine.ComputeContextHealth(used, context, _modelName);
                double usagePctOfEffective = effectiveLimit <= 0 ? 0 : Math.Clamp(used * 100.0 / effectiveLimit, 0, 100);
                double thresholdPct = effectiveLimit <= 0 ? 80 : Math.Clamp(thresholdPoint * 100.0 / effectiveLimit, 0, 100);
                CompactionThresholdBar.Value = usagePctOfEffective;

                if (_compactionEngine.CompactionPending)
                {
                    CompactionThresholdBar.Foreground = new SolidColorBrush(Color.FromRgb(255, 59, 59));
                    CompactionHealthLabel.Text = $"Compaction pending • {usagePctOfEffective:F0}% of {effectiveLimit} effective tokens • triggers at {thresholdPct:F0}%";
                    CompactionHealthLabel.Foreground = new SolidColorBrush(Color.FromRgb(255, 59, 59));
                }
                else if (usagePctOfEffective >= 60)
                {
                    CompactionThresholdBar.Foreground = new SolidColorBrush(Color.FromRgb(245, 158, 11));
                    CompactionHealthLabel.Text = $"{healthLabel} • {usagePctOfEffective:F0}% of {effectiveLimit} effective tokens • triggers at {thresholdPct:F0}%";
                    CompactionHealthLabel.Foreground = new SolidColorBrush(Color.FromRgb(245, 158, 11));
                }
                else
                {
                    CompactionThresholdBar.Foreground = new SolidColorBrush(Color.FromRgb(34, 197, 94));
                    CompactionHealthLabel.Text = $"{healthLabel} • {usagePctOfEffective:F0}% of {effectiveLimit} effective tokens • triggers at {thresholdPct:F0}%";
                    CompactionHealthLabel.Foreground = new SolidColorBrush(Color.FromRgb(122, 122, 114));
                }
            }
            else
            {
                CompactionHealthGrid.Visibility = Visibility.Collapsed;
            }
            CompactionStatusLabel.Visibility = Visibility.Collapsed;
        }

        private void SaveChatAdvancedState()
        {
            _ = QueueAdvancedStateSaveAsync();
        }

        private async Task AddChatMessageAsync(string role, string content)
        {
            if (Dispatcher.CheckAccess())
            {
                AddChatMessage(role, content);
                return;
            }

            await Dispatcher.InvokeAsync(() => AddChatMessage(role, content), System.Windows.Threading.DispatcherPriority.Background);
        }

        private void LoadChatAdvancedState()
        {
            try
            {
                var state = _chatAdvancedStatePersistence.Load();
                if (!string.IsNullOrWhiteSpace(_chatAdvancedStatePersistence.LastLoadStatusMessage))
                    AppendSystemMessage(_chatAdvancedStatePersistence.LastLoadStatusMessage);
                if (state == null)
                    return;

                _branches.Clear();
                _branches.AddRange(state.Branches);
                _activeBranchId = state.ActiveBranchId;

                _pinnedMessages.Clear();
                foreach (var p in state.PinnedMessages)
                    _pinnedMessages.Add(p);

                _promptTemplates.Clear();
                _promptTemplates.AddRange(state.PromptTemplates);

                _systemPromptPresets.Clear();
                _systemPromptPresets.AddRange(state.SystemPromptPresets);

                if (!string.IsNullOrWhiteSpace(state.ActiveSystemPrompt))
                    SystemPromptBox.Text = state.ActiveSystemPrompt;

                _workplaceChats.Clear();
                _workplaceChatNames.Clear();
                foreach (var w in state.WorkplaceChats)
                {
                    if (w.Id <= 0 || w.Snapshot == null)
                        continue;

                    _workplaceChats[w.Id] = w.Snapshot;
                    _workplaceChatNames[w.Id] = string.IsNullOrWhiteSpace(w.Name) ? $"Workplace {w.Id}" : w.Name;
                }

                _currentWorkplaceChatId = state.CurrentWorkplaceChatId;
                _workplaceChatCounter = Math.Max(state.WorkplaceChatCounter, _workplaceChats.Keys.DefaultIfEmpty(0).Max());
                _loadedPersistedWorkplaceChats = _workplaceChats.Count > 0;

                LoadOpenRouterSettings();
                RefreshCloudModeToggleUi();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Advanced chat state load error: {ex.Message}");
            }
        }

        private string? PromptForText(string title, string prompt, string defaultValue = "")
        {
            var bg = new SolidColorBrush(Color.FromRgb(0x17, 0x16, 0x15));
            var surface = new SolidColorBrush(Color.FromRgb(0x21, 0x1F, 0x1D));
            var border = new SolidColorBrush(Color.FromRgb(0x30, 0x2D, 0x2A));
            var fg = new SolidColorBrush(Color.FromRgb(0xED, 0xE8, 0xE3));
            var secondary = new SolidColorBrush(Color.FromRgb(0x8A, 0x82, 0x79));
            var accent = new SolidColorBrush(Color.FromRgb(0xB8, 0x92, 0x4A));

            var window = new Window
            {
                Title = title,
                Width = 580,
                Height = 360,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode = ResizeMode.NoResize,
                Background = bg,
                Foreground = fg,
                Owner = this
            };

            var root = new DockPanel { Margin = new Thickness(16) };

            var label = new TextBlock
            {
                Text = prompt,
                Foreground = secondary,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 10)
            };
            DockPanel.SetDock(label, Dock.Top);

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 10, 0, 0)
            };

            var ok = new Button
            {
                Content = "OK",
                Width = 90,
                Height = 32,
                Margin = new Thickness(0, 0, 8, 0),
                Background = accent,
                Foreground = fg,
                BorderThickness = new Thickness(0),
                FontWeight = FontWeights.SemiBold
            };
            var cancel = new Button
            {
                Content = "Cancel",
                Width = 90,
                Height = 32,
                Background = surface,
                Foreground = fg,
                BorderBrush = border,
                BorderThickness = new Thickness(1)
            };

            ok.Click += (_, _) => window.DialogResult = true;
            cancel.Click += (_, _) => window.DialogResult = false;
            buttons.Children.Add(ok);
            buttons.Children.Add(cancel);
            DockPanel.SetDock(buttons, Dock.Bottom);

            var text = new TextBox
            {
                Text = defaultValue,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Background = surface,
                Foreground = fg,
                CaretBrush = fg,
                BorderBrush = border,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(10),
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 12
            };

            root.Children.Add(label);
            root.Children.Add(buttons);
            root.Children.Add(text);
            window.Content = root;
            bool? result = window.ShowDialog();
            return result == true ? text.Text : null;
        }

        private void SaveCurrentChat()
        {
            _ = SaveCurrentChatAsync();
        }

        private async Task SaveCurrentChatAsync()
        {
            if (_currentChatId < 0)
                return;

            PendingChatPersistencePayload? payload = await Dispatcher.InvokeAsync(() =>
            {
                if (_currentChatId < 0)
                    return null;

                return new PendingChatPersistencePayload
                {
                    ChatId = _currentChatId,
                    ConversationTranscript = string.Join(Environment.NewLine, _chatMessages.Select(msg => $"[{msg.Role}] {msg.Content}"))
                };
            }, System.Windows.Threading.DispatcherPriority.Background);

            if (payload == null)
                return;

            _chatContent[payload.ChatId] = payload.ConversationTranscript;
            await PersistCurrentChatSessionAsync().ConfigureAwait(false);
        }

        private static List<CompactionSummaryEntry>? CloneCompactionSummaries(List<CompactionSummaryEntry>? source)
        {
            return source?.Select(entry => new CompactionSummaryEntry
            {
                TopicLabel = entry.TopicLabel,
                OriginalMessageCount = entry.OriginalMessageCount,
                Summary = entry.Summary
            }).ToList();
        }

        private static ChatMessageState CloneChatMessageState(ChatMessageState message)
        {
            return new ChatMessageState
            {
                Id = message.Id,
                Role = message.Role,
                Content = message.Content,
                ThinkingContent = message.ThinkingContent,
                ThinkingHeaderText = message.ThinkingHeaderText,
                ModelLabel = message.ModelLabel,
                Timestamp = message.Timestamp,
                Importance = message.Importance,
                IsCompactionProtected = message.IsCompactionProtected
            };
        }

        private static ChatBranch CloneChatBranch(ChatBranch branch)
        {
            return new ChatBranch
            {
                Id = branch.Id,
                Name = branch.Name,
                ForkMessageIndex = branch.ForkMessageIndex,
                Messages = branch.Messages.Select(CloneChatMessageState).ToList()
            };
        }

        private static WorkplaceSessionSnapshot CloneWorkplaceSessionSnapshot(WorkplaceSessionSnapshot snapshot)
        {
            return new WorkplaceSessionSnapshot
            {
                ObjectiveText = snapshot.ObjectiveText,
                ProjectCanvasText = snapshot.ProjectCanvasText,
                CloudModeEnabled = snapshot.CloudModeEnabled,
                GlobalContextSize = snapshot.GlobalContextSize,
                ArchitectContextSize = snapshot.ArchitectContextSize,
                BuilderContextSize = snapshot.BuilderContextSize,
                CriticContextSize = snapshot.CriticContextSize,
                AutoOptimizeRoleContexts = snapshot.AutoOptimizeRoleContexts,
                ChatCards = snapshot.ChatCards.Select(card => new WorkplaceChatMessageDto
                {
                    Role = card.Role,
                    Content = card.Content,
                    Timestamp = card.Timestamp
                }).ToList(),
                Documents = snapshot.Documents.Select(document => new WorkplaceDocumentDto
                {
                    Name = document.Name,
                    FilePath = document.FilePath,
                    Type = document.Type,
                    Info = document.Info,
                    ChunkCount = document.ChunkCount
                }).ToList(),
                CouncilModels = snapshot.CouncilModels.ToDictionary(
                    kvp => kvp.Key,
                    kvp => new WorkplaceCouncilModelDto
                    {
                        ModelPath = kvp.Value.ModelPath,
                        DisplayName = kvp.Value.DisplayName,
                        Format = kvp.Value.Format,
                        UseCloud = kvp.Value.UseCloud,
                        CloudModelId = kvp.Value.CloudModelId
                    },
                    StringComparer.OrdinalIgnoreCase),
                HippocampusEntries = snapshot.HippocampusEntries.Select(entry => new SessionHippocampusEntry
                {
                    Content = entry.Content,
                    Source = entry.Source,
                    Tag = entry.Tag,
                    Priority = entry.Priority,
                    Timestamp = entry.Timestamp,
                    SessionRunIndex = entry.SessionRunIndex,
                    AccessCount = entry.AccessCount,
                    LastAccessedTimestamp = entry.LastAccessedTimestamp
                }).ToList(),
                StudySessionCompleted = snapshot.StudySessionCompleted,
                StudySessionProcessedDocumentCount = snapshot.StudySessionProcessedDocumentCount,
                CompletedCouncilRunCount = snapshot.CompletedCouncilRunCount,
                SavedAt = snapshot.SavedAt
            };
        }

        private ChatSession? CaptureCurrentChatSessionSnapshot()
        {
            if (_currentChatId < 0)
                return null;

            var session = new ChatSession($"Chat{_currentChatId}")
            {
                Id = _currentChatId,
                Name = _chatNames.TryGetValue(_currentChatId, out string savedName) ? savedName : $"Chat{_currentChatId}",
                CreatedAt = DateTime.Now,
                UpdatedAt = DateTime.Now
            };

            session.AttachedDocuments = _chatDocuments
                .Select(d => new ChatDocumentAttachment
                {
                    Name = d.Name,
                    Content = d.Content,
                    Kind = d.Kind,
                    MimeType = d.MimeType,
                    Base64Data = d.Base64Data,
                    FileSizeBytes = d.FileSizeBytes,
                    ImportedAt = DateTime.Now
                })
                .ToList();

            foreach (var msg in _chatMessages)
            {
                session.Messages.Add(new ChatMessage(msg.Role, msg.Content)
                {
                    Id = msg.Id,
                    Timestamp = msg.Timestamp,
                    ModelLabel = msg.ModelLabel,
                    ThinkingContent = msg.ThinkingContent,
                    IsPinned = msg.IsPinned,
                    Importance = msg.Importance,
                    IsCompactionProtected = msg.IsCompactionProtected,
                    IsCompactionMarker = msg.IsCompactionMarker,
                    CompactionSummaries = CloneCompactionSummaries(msg.CompactionSummaries)
                });
            }

            return session;
        }

        private ChatWorkspaceSnapshot CaptureChatWorkspaceSnapshot()
        {
            var snapshot = new ChatWorkspaceSnapshot
            {
                ModelName = _modelName,
                CurrentChatId = _currentChatId,
                AttachedDocuments = _chatDocuments.Select(d => new ChatDocumentAttachment
                {
                    Name = d.Name,
                    Content = d.Content,
                    Kind = d.Kind,
                    MimeType = d.MimeType,
                    Base64Data = d.Base64Data,
                    FileSizeBytes = d.FileSizeBytes,
                    ImportedAt = DateTime.Now
                }).ToList(),
                SavedAt = DateTime.Now
            };

            foreach (var msg in _chatMessages)
            {
                snapshot.Messages.Add(new ChatMessageState
                {
                    Id = msg.Id,
                    Role = msg.Role,
                    Content = msg.Content,
                    ThinkingContent = msg.ThinkingContent,
                    ThinkingHeaderText = msg.ThinkingHeaderText,
                    ModelLabel = msg.ModelLabel,
                    Timestamp = msg.Timestamp,
                    Importance = msg.Importance.ToString(),
                    IsCompactionProtected = msg.IsCompactionProtected
                });
            }

            return snapshot;
        }

        private static bool ShouldInjectPersistentDocumentContext(string currentUserMessage, IReadOnlyCollection<ChatDocumentAttachment> chatDocuments, IReadOnlyList<ChatMessage> chatMessages)
        {
            if (chatDocuments == null || chatDocuments.Count == 0)
                return false;

            // Always inject for any non-trivial user message when documents are attached
            if (!string.IsNullOrWhiteSpace(currentUserMessage)
                && currentUserMessage.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length >= 2)
            {
                return true;
            }

            if (ShouldUseDocumentContext(currentUserMessage, chatDocuments)
                || MentionsAttachedDocumentName(currentUserMessage, chatDocuments)
                || ContainsDocumentFollowUpCue(currentUserMessage))
            {
                return true;
            }

            return chatMessages
                .TakeLast(8)
                .Any(m => string.Equals(m.Role, "user", StringComparison.OrdinalIgnoreCase)
                    && (ShouldUseDocumentContext(m.Content, chatDocuments)
                        || MentionsAttachedDocumentName(m.Content, chatDocuments)
                        || ContainsDocumentFollowUpCue(m.Content)));
        }

        private static string BuildDocumentRetrievalQuery(string currentUserMessage, IReadOnlyList<ChatMessage> chatMessages, IReadOnlyCollection<ChatDocumentAttachment> chatDocuments)
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(currentUserMessage))
                parts.Add(currentUserMessage.Trim());

            // Include recent user messages regardless of doc-cue for richer retrieval context
            foreach (string prior in chatMessages
                .Where(m => string.Equals(m.Role, "user", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(m.Content))
                .Select(m => m.Content.Trim())
                .Reverse()
                .Where(text => !string.Equals(text, currentUserMessage?.Trim(), StringComparison.Ordinal))
                .Take(3))
            {
                parts.Add(prior);
            }

            // Add document name stems as additional keyword hints
            foreach (var doc in (chatDocuments ?? []).Take(4))
            {
                string stem = Path.GetFileNameWithoutExtension(doc.Name);
                if (!string.IsNullOrWhiteSpace(stem) && stem.Length >= 3)
                    parts.Add(stem);
            }

            return string.Join("\n", parts.Distinct(StringComparer.Ordinal));
        }

        private string BuildPersistentDocumentContextBlock(string currentUserMessage, bool isCloudMode, IReadOnlyList<ChatDocumentAttachment> chatDocuments, IReadOnlyList<ChatMessage> chatMessages, int contextSize)
        {
            if (!(chatDocuments?.Any(doc => doc.HasTextContent && !doc.IsImage) ?? false)
                || !ShouldInjectPersistentDocumentContext(currentUserMessage, chatDocuments, chatMessages))
            {
                return string.Empty;
            }

            // Budget roughly a third of the context window for document text (4 chars ≈ 1 token),
            // leaving room for system prompt, history, and the response.
            int maxChars = isCloudMode
                ? 8000
                : Math.Clamp((int)(contextSize * 1.4), 2400, 16000);

            var textDocuments = chatDocuments.Where(doc => doc.HasTextContent && !doc.IsImage).ToList();

            // When every attached document fits in the budget, inject them whole — retrieval
            // can only lose information, and "read/analyze this file" needs the full text.
            int fullInjectionSize = textDocuments.Sum(doc => doc.Content.Length + doc.Name.Length + 32);
            if (fullInjectionSize <= maxChars)
            {
                var fullBuilder = new StringBuilder();
                fullBuilder.AppendLine();
                fullBuilder.AppendLine("[[DOCUMENT CONTEXT]]");
                foreach (ChatDocumentAttachment doc in textDocuments)
                {
                    fullBuilder.AppendLine($"=== {doc.Name} (complete) ===");
                    fullBuilder.AppendLine(doc.Content.Trim());
                }
                fullBuilder.AppendLine("[[END DOCUMENT CONTEXT]]");
                return fullBuilder.ToString();
            }

            string query = BuildDocumentRetrievalQuery(currentUserMessage, chatMessages, chatDocuments);
            int maxChunks = Math.Clamp(_documentRetriever.CalculateMaxChunksForContext(contextSize), 2, isCloudMode ? 8 : 10);
            // allowFallback: documents are attached and the turn warrants document context, so
            // always provide at least representative chunks — an empty result makes the model
            // claim the file is unreadable.
            List<DocumentChunk> relevantChunks = _documentRetriever.RetrieveRelevantChunks(query, maxChunks, allowFallback: true)
                .GroupBy(chunk => $"{chunk.FileName}:{chunk.ChunkId}")
                .Select(group => group.First())
                .ToList();

            if (relevantChunks.Count == 0)
                return string.Empty;

            var builder = new StringBuilder();
            builder.AppendLine();
            builder.AppendLine("[[DOCUMENT CONTEXT]]");

            int charsUsed = 0;
            foreach (DocumentChunk chunk in relevantChunks)
            {
                string entry = $"=== {chunk.FileName} | chunk {chunk.ChunkId + 1} ===\n{chunk.Content.Trim()}\n";
                if (charsUsed + entry.Length > maxChars)
                {
                    int remaining = maxChars - charsUsed;
                    if (remaining > 160)
                    {
                        string truncated = entry[..Math.Min(entry.Length, remaining)].TrimEnd();
                        builder.AppendLine(truncated);
                        builder.AppendLine("[...truncated to preserve context budget]");
                    }
                    break;
                }

                builder.Append(entry);
                charsUsed += entry.Length;
            }

            builder.AppendLine("[[END DOCUMENT CONTEXT]]");
            return builder.ToString();
        }

        private static bool MentionsAttachedDocumentName(string? message, IReadOnlyCollection<ChatDocumentAttachment> chatDocuments)
        {
            if (string.IsNullOrWhiteSpace(message) || chatDocuments == null || chatDocuments.Count == 0)
                return false;

            string normalized = message.Trim();
            return chatDocuments.Any(doc =>
            {
                if (string.IsNullOrWhiteSpace(doc?.Name))
                    return false;

                string fileName = Path.GetFileNameWithoutExtension(doc.Name)?.Trim() ?? string.Empty;
                if (fileName.Length >= 3 && normalized.Contains(fileName, StringComparison.OrdinalIgnoreCase))
                    return true;

                return normalized.Contains(doc.Name.Trim(), StringComparison.OrdinalIgnoreCase);
            });
        }

        private static string AppendAttachedDocumentContextToSystemPrompt(string systemPrompt, string documentContext)
        {
            if (string.IsNullOrWhiteSpace(documentContext))
                return systemPrompt;

            return AppendSystemInstruction(
                systemPrompt,
                AttachedDocumentRequiredReferenceInstruction + "\n\n" + documentContext);
        }

        private ChatAdvancedStateSnapshot CaptureChatAdvancedStateSnapshot()
        {
            return new ChatAdvancedStateSnapshot
            {
                Branches = _branches.Select(CloneChatBranch).ToList(),
                ActiveBranchId = _activeBranchId,
                PinnedMessages = _pinnedMessages.Select(p => new PinnedMessageEntry
                {
                    Id = p.Id,
                    Role = p.Role,
                    Content = p.Content,
                    Timestamp = p.Timestamp
                }).ToList(),
                PromptTemplates = _promptTemplates.Select(t => new PromptTemplateEntry
                {
                    Category = t.Category,
                    Text = t.Text,
                    IsCustom = t.IsCustom
                }).ToList(),
                SystemPromptPresets = _systemPromptPresets.Select(p => new SystemPromptPresetEntry
                {
                    Name = p.Name,
                    Prompt = p.Prompt,
                    IsBuiltIn = p.IsBuiltIn
                }).ToList(),
                ActiveSystemPrompt = SystemPromptBox?.Text ?? "",
                WorkplaceChats = _workplaceChats
                    .OrderBy(k => k.Key)
                    .Select(kvp => new WorkplaceChatStateEntry
                    {
                        Id = kvp.Key,
                        Name = _workplaceChatNames.TryGetValue(kvp.Key, out var name) ? name : $"Workplace {kvp.Key}",
                        Snapshot = CloneWorkplaceSessionSnapshot(kvp.Value)
                    })
                    .ToList(),
                CurrentWorkplaceChatId = _currentWorkplaceChatId,
                WorkplaceChatCounter = _workplaceChatCounter
            };
        }

        private async Task PersistCurrentChatSessionAsync()
        {
            await QueueCoordinatedChatPersistenceAsync(includeChatSession: true, includeWorkspaceState: false, includeAdvancedState: false, includeKvState: true).ConfigureAwait(false);
        }

        private async Task QueueCoordinatedChatPersistenceAsync(bool includeChatSession, bool includeWorkspaceState, bool includeAdvancedState, bool includeKvState)
        {
            int requestedVersion = Interlocked.Increment(ref _coordinatedPersistenceVersion);
            await Task.Yield();

            if (!await _coordinatedPersistenceGate.WaitAsync(0).ConfigureAwait(false))
                return;

            try
            {
                while (true)
                {
                    CoordinatedChatPersistenceSnapshot snapshot = await Dispatcher.InvokeAsync(
                        () => CaptureCoordinatedChatPersistenceSnapshot(includeChatSession, includeWorkspaceState, includeAdvancedState, includeKvState),
                        System.Windows.Threading.DispatcherPriority.Background);

                    if (snapshot.PendingPayload != null)
                        _chatContent[snapshot.PendingPayload.ChatId] = snapshot.PendingPayload.ConversationTranscript;

                    if (includeChatSession && snapshot.ChatSession != null && _jsonPersistence != null)
                        await Task.Run(() => _jsonPersistence.SaveChat(snapshot.ChatSession)).ConfigureAwait(false);

                    if (includeWorkspaceState && snapshot.WorkspaceSnapshot != null)
                        await Task.Run(() => _chatWorkspaceStatePersistence.Save(snapshot.WorkspaceSnapshot)).ConfigureAwait(false);

                    if (includeAdvancedState && snapshot.AdvancedSnapshot != null)
                        await Task.Run(() => _chatAdvancedStatePersistence.Save(snapshot.AdvancedSnapshot)).ConfigureAwait(false);

                    if (includeKvState && snapshot.CanSaveKvState)
                    {
                        await TrySaveKvStateForCurrentChatAsync(CancellationToken.None).ConfigureAwait(false);

                        if (snapshot.ActiveBranchId != Guid.Empty)
                            await TrySaveKvStateAsync(GetBranchKvStatePath(snapshot.ActiveBranchId), CancellationToken.None).ConfigureAwait(false);
                    }

                    int latestVersion = Volatile.Read(ref _coordinatedPersistenceVersion);
                    if (requestedVersion >= latestVersion)
                        break;

                    requestedVersion = latestVersion;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Coordinated persistence error: {ex.Message}");
            }
            finally
            {
                _coordinatedPersistenceGate.Release();
            }
        }

        private CoordinatedChatPersistenceSnapshot CaptureCoordinatedChatPersistenceSnapshot(bool includeChatSession, bool includeWorkspaceState, bool includeAdvancedState, bool includeKvState)
        {
            if (_currentChatId < 0)
                return new CoordinatedChatPersistenceSnapshot
                {
                    AdvancedSnapshot = includeAdvancedState ? CaptureChatAdvancedStateSnapshot() : null,
                    WorkspaceSnapshot = includeWorkspaceState ? CaptureChatWorkspaceSnapshot() : null,
                    ActiveBranchId = _activeBranchId,
                    CanSaveKvState = includeKvState && !_isProcessing
                };

            return new CoordinatedChatPersistenceSnapshot
            {
                PendingPayload = new PendingChatPersistencePayload
                {
                    ChatId = _currentChatId,
                    ConversationTranscript = string.Join(Environment.NewLine, _chatMessages.Select(msg => $"[{msg.Role}] {msg.Content}"))
                },
                ChatSession = includeChatSession ? CaptureCurrentChatSessionSnapshot() : null,
                WorkspaceSnapshot = includeWorkspaceState ? CaptureChatWorkspaceSnapshot() : null,
                AdvancedSnapshot = includeAdvancedState ? CaptureChatAdvancedStateSnapshot() : null,
                ActiveBranchId = _activeBranchId,
                CanSaveKvState = includeKvState && !_isProcessing
            };
        }

        private string GetChatKvStatePath(int chatId)
        {
            return Path.Combine(KvStateFolder, $"chat_{chatId}.kvstate");
        }

        private string GetBranchKvStatePath(Guid branchId)
        {
            return Path.Combine(KvStateFolder, $"branch_{branchId:N}.kvstate");
        }

        private async Task CloneKvStateForBranchAsync(Guid branchId)
        {
            try
            {
                string sourcePath = GetChatKvStatePath(_currentChatId);
                string targetPath = GetBranchKvStatePath(branchId);

                if (!File.Exists(sourcePath))
                {
                    await TrySaveKvStateForCurrentChatAsync(CancellationToken.None);
                }

                if (File.Exists(sourcePath))
                {
                    await Task.Run(() => File.Copy(sourcePath, targetPath, true));
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"CloneKvStateForBranchAsync fallback: {ex.Message}");
            }
        }

        private async Task<bool> TrySaveKvStateAsync(string statePath, CancellationToken token)
        {
            try
            {
                if (_executor == null || _useGemma4LocalCliMode)
                    return false;

                Directory.CreateDirectory(KvStateFolder);
                await _executor.SaveState(statePath, token);
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"KV SaveState failed for '{statePath}': {ex.Message}");
                return false;
            }
        }

        private async Task<bool> TrySaveKvStateForCurrentChatAsync(CancellationToken token)
        {
            if (_currentChatId < 0)
                return false;

            return await TrySaveKvStateAsync(GetChatKvStatePath(_currentChatId), token);
        }

        private async Task TryLoadKvStateForCurrentChatAsync(CancellationToken token)
        {
            if (_currentChatId < 0)
                return;

            string statePath = GetChatKvStatePath(_currentChatId);
            bool loaded = await TryLoadKvStateAsync(statePath, token);
            if (!loaded)
            {
                await RebuildChatSessionFromMessagesAsync(token);
            }
        }

        private async Task TryLoadKvStateForBranchAsync(Guid branchId, CancellationToken token)
        {
            string statePath = GetBranchKvStatePath(branchId);
            bool loaded = await TryLoadKvStateAsync(statePath, token);
            if (!loaded)
            {
                await RebuildChatSessionFromMessagesAsync(token);
            }
        }

        private async Task<bool> TryLoadKvStateAsync(string statePath, CancellationToken token)
        {
            if (_executor == null || _model == null || _activeModelParams == null || _useGemma4LocalCliMode)
                return false;

            if (!File.Exists(statePath))
                return false;

            // Free the old KV cache BEFORE allocating the new one. Keeping both alive
            // doubles peak VRAM and aborts the process on CUDA OOM. Allocation happens
            // off the UI thread — it can take seconds for large contexts.
            var oldExecutor = _executor;
            _executor = null;
            _chatSession = null;
            try { oldExecutor?.Context?.Dispose(); } catch { }

            try
            {
                var newContext = await Task.Run(() => _model.CreateContext(_activeModelParams), token);
                var newExecutor = new InteractiveExecutor(newContext);

                await newExecutor.LoadState(statePath, token);

                _executor = newExecutor;
                RebuildChatSession();
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"KV LoadState failed for '{statePath}': {ex.Message}");

                // The old context is already gone — recreate a fresh executor so the
                // caller's history-replay fallback has a live session to rebuild into.
                try
                {
                    var recoveryContext = await Task.Run(() => _model.CreateContext(_activeModelParams), token);
                    _executor = new InteractiveExecutor(recoveryContext);
                    RebuildChatSession();
                }
                catch (Exception recoveryEx)
                {
                    Debug.WriteLine($"KV state recovery context creation failed: {recoveryEx.Message}");
                }

                return false;
            }
        }

        private async Task RebuildChatSessionFromMessagesAsync(CancellationToken token)
        {
            try
            {
                if (_executor == null || _chatSession == null || _useGemma4LocalCliMode)
                    return;

                RebuildChatSession();
                bool lastAcceptedWasUser = false;
                foreach (var msg in _chatMessages.Select(CloneMessageForInferenceContext))
                {
                    if (string.Equals(msg.Role, "user", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!string.IsNullOrWhiteSpace(msg.Content))
                        {
                            await _chatSession.AddAndProcessUserMessage(msg.Content ?? string.Empty, token);
                            lastAcceptedWasUser = true;
                        }
                    }
                    else if (string.Equals(msg.Role, "assistant", StringComparison.OrdinalIgnoreCase))
                    {
                        if (lastAcceptedWasUser && !string.IsNullOrWhiteSpace(msg.Content))
                        {
                            await _chatSession.AddAndProcessAssistantMessage(msg.Content ?? string.Empty, token);
                            lastAcceptedWasUser = false;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"RebuildChatSessionFromMessagesAsync fallback failed: {ex.Message}");
            }
        }

        private void AddChatMessage(string role, string content)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(() => AddChatMessage(role, content));
                return;
            }

            var msg = new ChatMessage(role, content);
            msg.Importance = SmartContextCompactionEngine.ClassifyImportance(role, content, false);
            _chatMessages.Add(msg);

            if (string.Equals(role, "user", StringComparison.OrdinalIgnoreCase))
                UpdateCurrentChatTitle(content);

            var activeBranch = _branches.FirstOrDefault(b => b.Id == _activeBranchId);
            if (activeBranch != null)
            {
                activeBranch.Messages.Add(new ChatMessageState
                {
                    Id = msg.Id,
                    Role = msg.Role,
                    Content = msg.Content,
                    ThinkingContent = msg.ThinkingContent,
                    ThinkingHeaderText = msg.ThinkingHeaderText,
                    ModelLabel = msg.ModelLabel,
                    Timestamp = msg.Timestamp,
                    Importance = msg.Importance.ToString(),
                    IsCompactionProtected = msg.IsCompactionProtected
                });
            }

            ScrollChatToEnd();
            UpdateTokenUsageIndicator();
            UpdateNormalChatChrome();
            if (!_isProcessing)
            {
                SaveChatAdvancedState();
                _ = QueueWorkspaceStateSaveAsync();
            }
        }

        private void ScrollChatToEnd()
        {
            if (ChatDisplay == null || _chatMessages.Count == 0)
                return;

            if (_chatScrollPending)
                return;

            _chatScrollPending = true;
            _ = Dispatcher.InvokeAsync(() =>
            {
                try
                {
                    EnsureChatDisplayScrollViewer();
                    _chatDisplayScrollViewer?.ScrollToBottom();
                }
                finally
                {
                    _chatScrollPending = false;
                }
            }, System.Windows.Threading.DispatcherPriority.Background);
        }

        private void ChatDisplay_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            EnsureChatDisplayScrollViewer();
            if (_chatDisplayScrollViewer == null)
                return;

            double nextOffset = _chatDisplayScrollViewer.VerticalOffset - e.Delta;
            nextOffset = Math.Max(0, Math.Min(nextOffset, _chatDisplayScrollViewer.ScrollableHeight));
            _chatDisplayScrollViewer.ScrollToVerticalOffset(nextOffset);
            e.Handled = true;
        }

        private void EnsureChatDisplayScrollViewer()
        {
            if (_chatDisplayScrollViewer != null)
                return;

            _chatDisplayScrollViewer = FindDescendant<ScrollViewer>(ChatDisplay);
        }

        private static T? FindDescendant<T>(DependencyObject? root) where T : DependencyObject
        {
            if (root == null)
                return null;

            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(root, i);
                if (child is T match)
                    return match;

                T? nested = FindDescendant<T>(child);
                if (nested != null)
                    return nested;
            }

            return null;
        }

        private async Task QueueWorkspaceStateSaveAsync()
        {
            await QueueCoordinatedChatPersistenceAsync(includeChatSession: false, includeWorkspaceState: true, includeAdvancedState: false, includeKvState: false).ConfigureAwait(false);
            return;

            int requestedVersion = Interlocked.Increment(ref _workspaceStateSaveVersion);
            await Task.Yield();

            if (!await _stateSaveGate.WaitAsync(0).ConfigureAwait(false))
                return;

            try
            {
                while (true)
                {
                    ChatWorkspaceSnapshot snapshot = await Dispatcher.InvokeAsync(CaptureChatWorkspaceSnapshot, System.Windows.Threading.DispatcherPriority.Background);
                    await Task.Run(() => _chatWorkspaceStatePersistence.Save(snapshot)).ConfigureAwait(false);

                    int latestVersion = Volatile.Read(ref _workspaceStateSaveVersion);
                    if (requestedVersion >= latestVersion)
                        break;

                    requestedVersion = latestVersion;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Workspace state save error: {ex.Message}");
            }
            finally
            {
                _stateSaveGate.Release();
            }
        }

        private async Task QueueAdvancedStateSaveAsync()
        {
            await QueueCoordinatedChatPersistenceAsync(includeChatSession: false, includeWorkspaceState: false, includeAdvancedState: true, includeKvState: false).ConfigureAwait(false);
            return;

            int requestedVersion = Interlocked.Increment(ref _advancedStateSaveVersion);
            await Task.Yield();

            if (!await _advancedStateSaveGate.WaitAsync(0).ConfigureAwait(false))
                return;

            try
            {
                while (true)
                {
                    ChatAdvancedStateSnapshot snapshot = await Dispatcher.InvokeAsync(CaptureChatAdvancedStateSnapshot, System.Windows.Threading.DispatcherPriority.Background);
                    await Task.Run(() => _chatAdvancedStatePersistence.Save(snapshot)).ConfigureAwait(false);

                    int latestVersion = Volatile.Read(ref _advancedStateSaveVersion);
                    if (requestedVersion >= latestVersion)
                        break;

                    requestedVersion = latestVersion;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Advanced chat state save error: {ex.Message}");
            }
            finally
            {
                _advancedStateSaveGate.Release();
            }
        }

        private async Task LoadWorkspaceStateAsync()
        {
            try
            {
                var snapshot = await _chatWorkspaceStatePersistence.LoadAsync();
                if (!string.IsNullOrWhiteSpace(_chatWorkspaceStatePersistence.LastLoadStatusMessage))
                    AppendSystemMessage(_chatWorkspaceStatePersistence.LastLoadStatusMessage);
                if (snapshot == null || snapshot.Messages.Count == 0)
                    return;

                Dispatcher.Invoke(() =>
                {
                    _modelName = string.IsNullOrWhiteSpace(snapshot.ModelName) ? _modelName : snapshot.ModelName;
                    _currentChatId = snapshot.CurrentChatId;
                    _chatMessages.Clear();
                    _chatDocuments.Clear();
                    foreach (var msg in snapshot.Messages)
                    {
                        _chatMessages.Add(new ChatMessage(msg.Role, msg.Content)
                        {
                            Id = msg.Id,
                            ThinkingContent = msg.ThinkingContent,
                            ModelLabel = msg.ModelLabel,
                            Timestamp = msg.Timestamp
                        });
                    }
                    foreach (var doc in snapshot.AttachedDocuments ?? [])
                    {
                        if (!string.IsNullOrWhiteSpace(doc?.Name) && (!string.IsNullOrWhiteSpace(doc.Content) || !string.IsNullOrWhiteSpace(doc.Base64Data)))
                            _chatDocuments.Add(doc);
                    }
                    RebuildChatDocumentIndex();
                    UpdateHeaderDisplay();
                    ScrollChatToEnd();
                    UpdateTokenUsageIndicator();
                });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Workspace state load error: {ex.Message}");
            }
        }

        private async Task ShowNonIntrusiveErrorAsync(string message)
        {
            await _notificationGate.WaitAsync();
            try
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    ErrorNotificationText.Text = message;
                    ErrorNotificationBar.Visibility = Visibility.Visible;
                });

                await Task.Delay(3200);

                await Dispatcher.InvokeAsync(() =>
                {
                    ErrorNotificationBar.Visibility = Visibility.Collapsed;
                    ErrorNotificationText.Text = string.Empty;
                });
            }
            finally
            {
                _notificationGate.Release();
            }
        }

        private void UpdateUIState(bool isReady)
        {
            bool canSend = _cloudModeActive
                ? _openRouterChatService.HasValidKey
                : (_chatSession != null || _useGemma4LocalCliMode);
            bool hasInput = !string.IsNullOrWhiteSpace(InputBox?.Text);
            SendButton.IsEnabled = isReady && canSend && hasInput;
            StopButton.IsEnabled = !isReady && _isProcessing;
            InputBox.IsEnabled = isReady;
        }

        private void SwitchToChat_Click(object sender, RoutedEventArgs e)
        {
            if (_isViewTransitionAnimating || ChatView.Visibility == Visibility.Visible)
                return;

            _neuronTimer.Stop();
            SaveCurrentWorkplaceChat();

            var active = GetActiveContentView();
            AnimateViewSwitch(active, ChatView);

            ChatTabButton.Style = (Style)this.Resources["ActiveNavigationTabButtonStyle"];
            WorkplaceTabButton.Style = (Style)this.Resources["InactiveNavigationTabButtonStyle"];
            PersonaTabButton.Style = (Style)this.Resources["InactiveNavigationTabButtonStyle"];
            NeuronTabButton.Style = (Style)this.Resources["InactiveNavigationTabButtonStyle"];
            HistorySectionToggle.Visibility = Visibility.Visible;
            ChatHistorySectionContainer.Visibility = _isHistorySectionExpanded ? Visibility.Visible : Visibility.Collapsed;
            WorkplaceHistorySectionToggle.Visibility = Visibility.Collapsed;
            WorkplaceHistorySectionContainer.Visibility = Visibility.Collapsed;
        }

        private void SwitchToWorkplace_Click(object sender, RoutedEventArgs e)
        {
            if (_isViewTransitionAnimating || WorkplaceViewControl.Visibility == Visibility.Visible)
                return;

            _neuronTimer.Stop();
            SaveCurrentChat();

            var active = GetActiveContentView();
            AnimateViewSwitch(active, WorkplaceViewControl);

            ChatTabButton.Style = (Style)this.Resources["InactiveNavigationTabButtonStyle"];
            WorkplaceTabButton.Style = (Style)this.Resources["ActiveNavigationTabButtonStyle"];
            PersonaTabButton.Style = (Style)this.Resources["InactiveNavigationTabButtonStyle"];
            NeuronTabButton.Style = (Style)this.Resources["InactiveNavigationTabButtonStyle"];
            HistorySectionToggle.Visibility = Visibility.Collapsed;
            ChatHistorySectionContainer.Visibility = Visibility.Collapsed;
            WorkplaceHistorySectionToggle.Visibility = Visibility.Visible;
            WorkplaceHistorySectionContainer.Visibility = _isWorkplaceHistorySectionExpanded ? Visibility.Visible : Visibility.Collapsed;

            // Initialize Workplace with its own session via the executor
            if (_executor != null)
            {
                uint contextSize = (uint)CtxSlider.Value;
                WorkplaceViewControl.InitializeWithSession(_executor, contextSize);
            }
        }

        private void SwitchToPersona_Click(object sender, RoutedEventArgs e)
        {
            if (_isViewTransitionAnimating || PersonaMemoryView.Visibility == Visibility.Visible)
                return;

            _neuronTimer.Stop();
            SaveCurrentWorkplaceChat();

            var active = GetActiveContentView();
            AnimateViewSwitch(active, PersonaMemoryView);
            ChatTabButton.Style = (Style)this.Resources["InactiveNavigationTabButtonStyle"];
            WorkplaceTabButton.Style = (Style)this.Resources["InactiveNavigationTabButtonStyle"];
            PersonaTabButton.Style = (Style)this.Resources["ActiveNavigationTabButtonStyle"];
            NeuronTabButton.Style = (Style)this.Resources["InactiveNavigationTabButtonStyle"];
            HistorySectionToggle.Visibility = Visibility.Visible;
            ChatHistorySectionContainer.Visibility = _isHistorySectionExpanded ? Visibility.Visible : Visibility.Collapsed;
            WorkplaceHistorySectionToggle.Visibility = Visibility.Collapsed;
            WorkplaceHistorySectionContainer.Visibility = Visibility.Collapsed;
        }

        private void SwitchToNeuron_Click(object sender, RoutedEventArgs e)
        {
            if (_isViewTransitionAnimating || NeuronView.Visibility == Visibility.Visible)
                return;

            SaveCurrentWorkplaceChat();
            SaveCurrentChat();

            var active = GetActiveContentView();
            AnimateViewSwitch(active, NeuronView);

            ChatTabButton.Style = (Style)this.Resources["InactiveNavigationTabButtonStyle"];
            WorkplaceTabButton.Style = (Style)this.Resources["InactiveNavigationTabButtonStyle"];
            PersonaTabButton.Style = (Style)this.Resources["InactiveNavigationTabButtonStyle"];
            NeuronTabButton.Style = (Style)this.Resources["ActiveNavigationTabButtonStyle"];
            HistorySectionToggle.Visibility = Visibility.Visible;
            ChatHistorySectionContainer.Visibility = _isHistorySectionExpanded ? Visibility.Visible : Visibility.Collapsed;
            WorkplaceHistorySectionToggle.Visibility = Visibility.Collapsed;
            WorkplaceHistorySectionContainer.Visibility = Visibility.Collapsed;

            UpdateNeuronMap();
            _neuronTimer.Start();
        }

        private UIElement GetActiveContentView()
        {
            if (NeuronView.Visibility == Visibility.Visible)
                return NeuronView;
            if (WorkplaceViewControl.Visibility == Visibility.Visible)
                return WorkplaceViewControl;
            if (PersonaMemoryView.Visibility == Visibility.Visible)
                return PersonaMemoryView;
            return ChatView;
        }

        private void UpdateNeuronMap()
        {
            if (NeuronCanvas == null)
                return;

            _neuronTick++;
            double width = Math.Max(NeuronCanvas.ActualWidth, 760);
            double height = Math.Max(NeuronCanvas.ActualHeight, 460);
            NeuronCanvas.Children.Clear();
            double t = DateTime.Now.TimeOfDay.TotalSeconds;

            var workplaceSnapshot = WorkplaceViewControl?.CaptureSnapshot();
            int normalMessages = _chatMessages.Count;
            int workplaceMessages = workplaceSnapshot?.ChatCards.Count ?? 0;
            int normalDocs = _chatDocuments.Count;
            int workplaceDocs = workplaceSnapshot?.Documents.Count ?? 0;
            int councilRuns = workplaceSnapshot?.CompletedCouncilRunCount ?? 0;
            int hippocampusCount = workplaceSnapshot?.HippocampusEntries.Count ?? 0;
            int calculatorOps = _chatMessages.Count(m => m.Role == "system" && m.Content.Contains("Calculator tool active", StringComparison.OrdinalIgnoreCase))
                + (workplaceSnapshot?.ChatCards.Count(m => string.Equals(m.Role, "system", StringComparison.OrdinalIgnoreCase) && m.Content.Contains("Calculator tool active", StringComparison.OrdinalIgnoreCase)) ?? 0);
            int studySignals = (workplaceSnapshot?.StudySessionCompleted == true ? 8 : 0)
                + (workplaceSnapshot?.StudySessionProcessedDocumentCount ?? 0)
                + (workplaceSnapshot?.ChatCards.Count(m => m.Content.Contains("Study Session", StringComparison.OrdinalIgnoreCase)) ?? 0);

            var nodes = new List<(string Label, int Weight, Point P)>
            {
                ("User",       Math.Max(1, normalMessages + workplaceMessages),      new Point(width * 0.50, height * 0.48)),
                ("Chat",       Math.Max(1, normalMessages),                           new Point(width * 0.22, height * 0.30)),
                ("Workplace",  Math.Max(1, workplaceMessages + councilRuns * 2),     new Point(width * 0.78, height * 0.30)),
                ("Documents",  Math.Max(1, normalDocs + workplaceDocs),              new Point(width * 0.22, height * 0.72)),
                ("Study",      Math.Max(1, studySignals),                            new Point(width * 0.50, height * 0.80)),
                ("Calculator", Math.Max(1, calculatorOps),                           new Point(width * 0.78, height * 0.72))
            };

            if (_neuronTick % 2 == 0)
            {
                IEnumerable<(string? Content, int Source)> taggedTexts =
                    _chatMessages.TakeLast(24).Select(m => ((string?)m.Content, 1))
                    .Concat((workplaceSnapshot?.ChatCards ?? new List<WorkplaceChatMessageDto>()).TakeLast(24).Select(m => ((string?)m.Content, 2)));

                if (!string.IsNullOrWhiteSpace(workplaceSnapshot?.ObjectiveText))
                    taggedTexts = taggedTexts.Append(((string?)workplaceSnapshot.ObjectiveText, 2));
                if (!string.IsNullOrWhiteSpace(workplaceSnapshot?.ProjectCanvasText))
                    taggedTexts = taggedTexts.Append(((string?)workplaceSnapshot.ProjectCanvasText, 2));
                if (workplaceSnapshot?.HippocampusEntries is { Count: > 0 } hipEntries)
                    taggedTexts = taggedTexts.Concat(hipEntries.Select(e => ((string?)e.Content, 4)));

                taggedTexts = taggedTexts.Concat(_chatDocuments.Select(d => ((string?)d.Name, 3)));
                if (workplaceSnapshot?.Documents is { Count: > 0 } wDocs)
                    taggedTexts = taggedTexts.Concat(wDocs.Select(d => ((string?)d.Name, 3)));

                UpdateNeuronBranches(taggedTexts, width, height);
            }

            // Pre-cache per-source brushes
            var srcFill = new SolidColorBrush[NeuronNodeColors.Length];
            var srcGlow = new SolidColorBrush[NeuronNodeColors.Length];
            var srcLine = new SolidColorBrush[NeuronNodeColors.Length];
            for (int k = 0; k < NeuronNodeColors.Length; k++)
            {
                var c = NeuronNodeColors[k];
                srcFill[k] = new SolidColorBrush(c); srcFill[k].Freeze();
                srcGlow[k] = new SolidColorBrush(Color.FromArgb(0x22, c.R, c.G, c.B)); srcGlow[k].Freeze();
                srcLine[k] = new SolidColorBrush(Color.FromArgb(0x7A, c.R, c.G, c.B)); srcLine[k].Freeze();
            }
            var strokeBrush = new SolidColorBrush(Color.FromRgb(0x30, 0x2D, 0x2A)); strokeBrush.Freeze();
            var labelFgBrush = new SolidColorBrush(Color.FromRgb(0xED, 0xE8, 0xE3)); labelFgBrush.Freeze();
            var dimLabelBrush = new SolidColorBrush(Color.FromRgb(0x8A, 0x82, 0x79)); dimLabelBrush.Freeze();
            var labelFont = new FontFamily("Segoe UI");

            Point centerP = nodes[0].P;

            // ═══ PASS 1: GLOW HALOS + ALL LINES (drawn first, render behind circles) ═══

            // Glow halos behind each hub node
            for (int i = 0; i < nodes.Count; i++)
            {
                var node = nodes[i];
                double baseR = 16 + Math.Min(24, Math.Log10(node.Weight + 1) * 10);
                double glowR = baseR * 2.4;
                var glow = new System.Windows.Shapes.Ellipse
                {
                    Width = glowR * 2,
                    Height = glowR * 2,
                    Fill = srcGlow[i]
                };
                Canvas.SetLeft(glow, node.P.X - glowR);
                Canvas.SetTop(glow, node.P.Y - glowR);
                NeuronCanvas.Children.Add(glow);
            }

            // Main connector lines from center to each hub
            for (int i = 1; i < nodes.Count; i++)
            {
                var node = nodes[i];
                NeuronCanvas.Children.Add(new System.Windows.Shapes.Line
                {
                    X1 = centerP.X, Y1 = centerP.Y,
                    X2 = node.P.X, Y2 = node.P.Y,
                    Stroke = srcLine[i],
                    Opacity = 0.65,
                    StrokeThickness = 1.0 + Math.Min(2.5, node.Weight / 22.0)
                });

                // Satellite branch lines from each hub
                int branchCount = Math.Clamp(node.Weight / 10, 2, 8);
                for (int j = 0; j < branchCount; j++)
                {
                    double angle = (Math.PI * 2 * j / branchCount) + (i * 0.41);
                    double dist = 22 + (j % 4) * 7;
                    NeuronCanvas.Children.Add(new System.Windows.Shapes.Line
                    {
                        X1 = node.P.X, Y1 = node.P.Y,
                        X2 = node.P.X + Math.Cos(angle) * dist,
                        Y2 = node.P.Y + Math.Sin(angle) * dist,
                        Stroke = srcLine[i],
                        Opacity = 0.28,
                        StrokeThickness = 0.7
                    });
                }
            }

            // Orbital term lines (from source hub to term position)
            var branchSnapshot = _neuronBranches.Values.OrderByDescending(b => b.Weight).Take(22).ToList();
            var branchPositions = new List<(NeuronBranchNode Branch, double X, double Y)>();
            foreach (var branch in branchSnapshot)
            {
                int si = Math.Clamp(branch.SourceIndex, 0, nodes.Count - 1);
                Point orbitCenter = nodes[si].P;
                double drift = Math.Sin(t * 0.8 + branch.OrbitAngle) * 3;
                double angle = branch.OrbitAngle + (_neuronTick * 0.009);
                double bx = orbitCenter.X + Math.Cos(angle) * (branch.OrbitRadius + drift);
                double by = orbitCenter.Y + Math.Sin(angle) * (branch.OrbitRadius + drift * 0.55);
                branchPositions.Add((branch, bx, by));

                NeuronCanvas.Children.Add(new System.Windows.Shapes.Line
                {
                    X1 = orbitCenter.X, Y1 = orbitCenter.Y,
                    X2 = bx, Y2 = by,
                    Stroke = srcLine[si],
                    Opacity = 0.32,
                    StrokeThickness = 0.75
                });
            }

            // ═══ PASS 2: SATELLITE + BRANCH DOTS ═══

            for (int i = 1; i < nodes.Count; i++)
            {
                var node = nodes[i];
                int branchCount = Math.Clamp(node.Weight / 10, 2, 8);
                for (int j = 0; j < branchCount; j++)
                {
                    double angle = (Math.PI * 2 * j / branchCount) + (i * 0.41);
                    double dist = 22 + (j % 4) * 7;
                    double satX = node.P.X + Math.Cos(angle) * dist;
                    double satY = node.P.Y + Math.Sin(angle) * dist;
                    var satDot = new System.Windows.Shapes.Ellipse
                    {
                        Width = 3.0, Height = 3.0,
                        Fill = srcFill[i],
                        Opacity = 0.55
                    };
                    Canvas.SetLeft(satDot, satX - 1.5);
                    Canvas.SetTop(satDot, satY - 1.5);
                    NeuronCanvas.Children.Add(satDot);
                }
            }

            // Orbital term dots
            foreach (var (branch, bx, by) in branchPositions)
            {
                int si = Math.Clamp(branch.SourceIndex, 0, nodes.Count - 1);
                double dotSize = 4 + Math.Min(4.5, branch.Weight / 6.0);
                var dot = new System.Windows.Shapes.Ellipse
                {
                    Width = dotSize, Height = dotSize,
                    Fill = srcFill[si],
                    Opacity = 0.75
                };
                Canvas.SetLeft(dot, bx - dotSize / 2);
                Canvas.SetTop(dot, by - dotSize / 2);
                NeuronCanvas.Children.Add(dot);
            }

            // ═══ PASS 3: MAIN HUB CIRCLES + LABELS ═══

            for (int i = 0; i < nodes.Count; i++)
            {
                var node = nodes[i];
                double pulse = 1.0 + Math.Sin(t * 2.1 + i * 0.8) * 0.07;
                double radius = 16 + Math.Min(24, Math.Log10(node.Weight + 1) * 10);
                radius *= pulse;
                var ellipse = new System.Windows.Shapes.Ellipse
                {
                    Width = radius * 2, Height = radius * 2,
                    Fill = srcFill[i],
                    Stroke = strokeBrush,
                    StrokeThickness = 1.2,
                    Opacity = 0.90
                };
                Canvas.SetLeft(ellipse, node.P.X - radius);
                Canvas.SetTop(ellipse, node.P.Y - radius);
                NeuronCanvas.Children.Add(ellipse);

                string labelText = $"{node.Label}  {node.Weight}";
                double lx = node.P.X - labelText.Length * 3.6;
                double ly = node.P.Y > height * 0.62 ? node.P.Y - radius - 19 : node.P.Y + radius + 5;
                var label = new TextBlock
                {
                    Text = labelText,
                    Foreground = labelFgBrush,
                    FontSize = 10,
                    FontFamily = labelFont,
                    FontWeight = FontWeights.SemiBold
                };
                Canvas.SetLeft(label, lx);
                Canvas.SetTop(label, ly);
                NeuronCanvas.Children.Add(label);
            }

            // Orbital term labels
            foreach (var (branch, bx, by) in branchPositions)
            {
                var label = new TextBlock
                {
                    Text = branch.Label,
                    FontSize = 9,
                    Foreground = dimLabelBrush,
                    FontFamily = labelFont
                };
                Canvas.SetLeft(label, bx + 5);
                Canvas.SetTop(label, by - 7);
                NeuronCanvas.Children.Add(label);
            }

            NeuronSummaryText.Text = $"Live neural map  ·  Chat {normalMessages}  ·  Workplace {workplaceMessages}  ·  Docs {normalDocs + workplaceDocs}  ·  Council {councilRuns}  ·  Memory {hippocampusCount}";
        }

        private void UpdateNeuronBranches(IEnumerable<(string? Content, int Source)> taggedTexts, double width, double height)
        {
            var stop = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "the","and","for","with","that","this","from","into","about","have","has","are","was","were","will","just",
                "chat","workplace","system","assistant","user","model","output","response","please","need","should","could","would",
                "when","then","than","what","they","them","their","there","here","where","which","also","some","more","your",
                "using","used","like","make","made","been","each","such","both","through","very","much","only","even",
                "being","doing","having","going","getting","based","after","before","already","always","never","often",
                "still","again","most","many","help","want","know","think","because","however","other","these","those",
                "message","messages","document","documents","file","files","text","content","section","information",
                "data","example","value","result","results","note","context","code","type","true","false","null","void"
            };

            // Track term frequency per source
            var termSourceCounts = new Dictionary<string, Dictionary<int, int>>(StringComparer.OrdinalIgnoreCase);
            foreach (var (content, source) in taggedTexts)
            {
                if (string.IsNullOrWhiteSpace(content)) continue;
                foreach (Match m in Regex.Matches(content, @"\b[a-zA-Z][a-zA-Z0-9_\-]{3,}\b"))
                {
                    string word = m.Value.ToLowerInvariant();
                    if (stop.Contains(word)) continue;
                    if (!termSourceCounts.TryGetValue(word, out var srcMap))
                    {
                        srcMap = new Dictionary<int, int>();
                        termSourceCounts[word] = srcMap;
                    }
                    srcMap.TryGetValue(source, out int cur);
                    srcMap[source] = cur + 1;
                }
            }

            var terms = termSourceCounts
                .Select(kv => (
                    Term: kv.Key,
                    Weight: kv.Value.Values.Sum(),
                    DominantSource: kv.Value.OrderByDescending(e => e.Value).First().Key
                ))
                .OrderByDescending(x => x.Weight)
                .Take(16)
                .ToList();

            foreach (var (term, weight, dominantSource) in terms)
            {
                if (!_neuronBranches.TryGetValue(term, out var node))
                {
                    int h = Math.Abs(term.GetHashCode());
                    double orbitR = dominantSource == 0 ? 90 + (h % 110) : 52 + (h % 58);
                    node = new NeuronBranchNode
                    {
                        Label = term,
                        Weight = weight,
                        OrbitAngle = (h % 628) / 100.0,
                        OrbitRadius = orbitR,
                        LastSeenTick = _neuronTick,
                        SourceIndex = dominantSource
                    };
                    _neuronBranches[term] = node;
                }
                else
                {
                    node.Weight = Math.Clamp((int)Math.Round(node.Weight * 0.75 + weight * 0.9), 1, 30);
                    node.LastSeenTick = _neuronTick;
                    node.SourceIndex = dominantSource;
                }
            }

            var stale = _neuronBranches.Where(kv => _neuronTick - kv.Value.LastSeenTick > 40).Select(kv => kv.Key).ToList();
            foreach (var key in stale)
                _neuronBranches.Remove(key);

            if (_neuronBranches.Count > 28)
            {
                foreach (var key in _neuronBranches.OrderBy(kv => kv.Value.Weight).ThenBy(kv => kv.Value.LastSeenTick)
                    .Take(_neuronBranches.Count - 28).Select(kv => kv.Key).ToList())
                    _neuronBranches.Remove(key);
            }
        }

        private void AnimateViewSwitch(UIElement hideElement, UIElement showElement)
        {
            _isViewTransitionAnimating = true;
            showElement.Visibility = Visibility.Visible;

            var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };

            var hideAnimation = new DoubleAnimation
            {
                Duration = TimeSpan.FromMilliseconds(160),
                From = 1,
                To = 0,
                EasingFunction = ease
            };

            hideAnimation.Completed += (_, _) =>
            {
                hideElement.Visibility = Visibility.Collapsed;
                hideElement.Opacity = 1;

                showElement.Opacity = 0;
                var showAnimation = new DoubleAnimation
                {
                    Duration = TimeSpan.FromMilliseconds(160),
                    From = 0,
                    To = 1,
                    EasingFunction = ease
                };

                showAnimation.Completed += (_, _) => _isViewTransitionAnimating = false;
                showElement.BeginAnimation(UIElement.OpacityProperty, showAnimation);
            };

            hideElement.BeginAnimation(UIElement.OpacityProperty, hideAnimation);
        }

        protected override void OnClosed(EventArgs e)
        {
            SaveCurrentWorkplaceChat();
            SaveCurrentChat();
            try
            {
                QueueCoordinatedChatPersistenceAsync(includeChatSession: true, includeWorkspaceState: true, includeAdvancedState: true, includeKvState: false).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Shutdown persistence error: {ex}");
                try { BackendLogService.LogErrorAsync("MainWindow.ShutdownPersistence", ex).GetAwaiter().GetResult(); } catch { }
            }
            base.OnClosed(e);
        }
    }
}