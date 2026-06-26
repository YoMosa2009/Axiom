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
using LLama.Native;
using LLama.Sampling;
using LLama.Transformers;

namespace Malx_AI
{
    public partial class MainWindow
    {
        private sealed class PromptInjectionBlockInfo
        {
            public string Label { get; init; } = string.Empty;
            public int TurnAge { get; init; }
            public bool IsCurrentTurn { get; init; }
            public bool IsCurrentPreflight { get; init; }
        }

        private sealed class ThinkingGateDecision
        {
            public int Score { get; init; }
            public bool UseThinking { get; init; }
            public bool UseReasoningPhaseCap { get; init; }
            public string Decision { get; init; } = string.Empty;
        }

        private sealed class ChatTurnContextCandidate
        {
            public int UserMessageIndex { get; init; }
            public int StartIndex { get; init; }
            public int EndIndex { get; init; }
            public double Score { get; set; }
        }

        private sealed class NormalChatRequestContext
        {
            public string SystemPrompt { get; init; } = string.Empty;
            public ThinkingGateDecision ThinkingGate { get; init; } = new();
            public SandboxPreparation SandboxPreparation { get; init; } = new();
            public string PersonaContext { get; init; } = string.Empty;
            public string WebContext { get; init; } = string.Empty;
            public bool HasWebContext { get; init; }
            public string EffectiveSystemPrompt { get; init; } = string.Empty;
            public string DocumentContext { get; init; } = string.Empty;
            public List<ChatMessage> SelectedHistoryMessages { get; init; } = new();
            public List<PromptInjectionBlockInfo> HistoryInjectionInfos { get; init; } = new();
            public bool UseIsolatedWebTurn { get; init; }
            public InferenceParams InferenceParams { get; init; } = new();
            public string CalculatorContext { get; init; } = string.Empty;
            public string ModelUserMessage { get; init; } = string.Empty;
            public bool IsGemma4Model { get; init; }
        }

        private sealed class NormalChatUiSnapshot
        {
            public string UserMessage { get; init; } = string.Empty;
            public string SystemPromptText { get; init; } = string.Empty;
            public string ModelName { get; init; } = string.Empty;
            public bool ChatDocumentsHaveTextContent { get; init; }
            public string AttachedDocumentMemory { get; init; } = string.Empty;
            public string DocumentContext { get; init; } = string.Empty;
            public bool HasVisionAttachmentForCloudTurn { get; init; }
            public int ContextSize { get; init; }
            public float Temperature { get; init; }
            public float MinP { get; init; }
            public List<ChatMessage> ChatMessages { get; init; } = new();
            public List<ChatDocumentAttachment> ChatDocuments { get; init; } = new();
            public Guid? CurrentStreamingMessageId { get; init; }
            public string HippocampusContext { get; init; } = string.Empty;
        }

        private static class Gemma4Formatter
        {
            public const string TurnOpen = "<|turn>";
            public const string TurnClose = "<turn|>";
            public const string ThinkControlToken = "<|channel|>thought";

            public static string BuildPrompt(string systemPrompt, IReadOnlyList<(string Role, string Content)> turns, string userPrompt, bool enableThinking)
            {
                var sb = new StringBuilder();
                string systemText = enableThinking
                    ? ThinkControlToken + "\n" + systemPrompt
                    : systemPrompt;

                sb.Append(TurnOpen).Append("system\n");
                sb.Append(systemText).Append('\n');
                sb.Append(TurnClose).Append('\n');

                foreach (var (role, content) in turns)
                {
                    sb.Append(TurnOpen).Append(role).Append('\n');
                    sb.Append(content).Append('\n');
                    sb.Append(TurnClose).Append('\n');
                }

                sb.Append(TurnOpen).Append("user\n");
                sb.Append(userPrompt).Append('\n');
                sb.Append(TurnClose).Append('\n');

                sb.Append(TurnOpen).Append("model\n");
                return sb.ToString();
            }
        }

        private bool IsQwen3Model(string modelFilePath)
        {
            if (string.IsNullOrWhiteSpace(modelFilePath))
                return false;

            string fileName = Path.GetFileName(modelFilePath);
            return fileName.Contains("qwen3", StringComparison.OrdinalIgnoreCase);
        }

        private string BuildQwen3SystemPrompt(string baseSystemPrompt, bool thinkingEnabled)
        {
            string activeModelPath = _database?.GetUserFact("last_model_path") ?? string.Empty;
            if (!IsQwen3Model(activeModelPath) && !IsQwen3Model(_modelName))
                return baseSystemPrompt ?? string.Empty;

            string prompt = (baseSystemPrompt ?? string.Empty).TrimEnd();
            if (prompt.EndsWith("/no_think", StringComparison.OrdinalIgnoreCase))
                prompt = prompt[..^"/no_think".Length].TrimEnd();
            else if (prompt.EndsWith("/think", StringComparison.OrdinalIgnoreCase))
                prompt = prompt[..^"/think".Length].TrimEnd();

            string controlToken = thinkingEnabled ? "/think" : "/no_think";
            _ = BackendLogService.LogEventAsync("Qwen3ThinkSwitch", $"Appended:{controlToken}\nThinkingEnabled:{thinkingEnabled}");
            return string.IsNullOrWhiteSpace(prompt)
                ? controlToken
                : prompt + "\n" + controlToken;
        }

        private static string StripThinkBlocksAndLeadingBlankLines(string content)
        {
            if (string.IsNullOrEmpty(content))
                return string.Empty;

            string cleaned = QwenThinkBlockStripRegex.Replace(content, string.Empty);
            return LeadingBlankLinesRegex.Replace(cleaned, string.Empty);
        }

        private static string StripThinkArtifactsForStreaming(string content)
        {
            if (string.IsNullOrEmpty(content))
                return string.Empty;

            string cleaned = StripThinkBlocksAndLeadingBlankLines(content);
            int lastOpenIndex = cleaned.LastIndexOf("<think", StringComparison.OrdinalIgnoreCase);
            int lastCloseIndex = cleaned.LastIndexOf("</think>", StringComparison.OrdinalIgnoreCase);
            if (lastOpenIndex >= 0 && lastOpenIndex > lastCloseIndex)
                cleaned = cleaned[..lastOpenIndex];

            cleaned = RemoveTrailingPartialTag(cleaned, "<think>");
            cleaned = RemoveTrailingPartialTag(cleaned, "</think>");
            return LeadingBlankLinesRegex.Replace(cleaned, string.Empty);
        }

        private static string RemoveTrailingPartialTag(string content, string fullTag)
        {
            if (string.IsNullOrEmpty(content) || string.IsNullOrEmpty(fullTag))
                return content ?? string.Empty;

            int maxPartialLength = Math.Min(fullTag.Length - 1, content.Length);
            for (int partialLength = maxPartialLength; partialLength > 0; partialLength--)
            {
                string suffix = content[^partialLength..];
                if (fullTag.StartsWith(suffix, StringComparison.OrdinalIgnoreCase))
                    return content[..^partialLength];
            }

            return content;
        }

        private static string SanitizeAssistantContentForInference(string content)
        {
            return StripThinkBlocksAndLeadingBlankLines(CleanOutputTokens(content ?? string.Empty));
        }

        private static ChatMessage CloneMessageForInferenceContext(ChatMessage message)
        {
            string content = string.Equals(message.Role, "assistant", StringComparison.OrdinalIgnoreCase)
                ? SanitizeAssistantContentForInference(message.Content)
                : message.Content ?? string.Empty;

            return new ChatMessage(message.Role, content)
            {
                Id = message.Id,
                Timestamp = message.Timestamp,
                ModelLabel = message.ModelLabel,
                ThinkingContent = message.ThinkingContent,
                ThinkingHeaderText = message.ThinkingHeaderText,
                IsPinned = message.IsPinned,
                Importance = message.Importance,
                IsCompactionProtected = message.IsCompactionProtected,
                IsCompactionMarker = message.IsCompactionMarker,
                CompactionSummaries = message.CompactionSummaries
            };
        }

        /// <summary>
        /// How many tokens to reserve for the model's reply on the LOCAL path. On a small
        /// context window (common when a big model is squeezed onto limited VRAM) a fixed
        /// 2,048-token reserve leaves almost nothing for an attached document — the document
        /// budget then collapses to its floor and the model sees only a sliver of the file.
        /// When a document is attached we deliberately trade some reply headroom for document
        /// room: a "summarize this file" answer is short, but it must actually see the file.
        /// This is the single source of truth shared by the document budget, the inference
        /// MaxTokens, and the prompt fit-guards so all three agree.
        /// </summary>
        private static int ComputeLocalMaxGenerationTokens(int contextSize, bool documentAttached)
        {
            if (contextSize >= 8192)
                return 2048;
            if (contextSize >= 6144)
                return documentAttached ? 1536 : 2048;
            if (contextSize >= 4096)
                return documentAttached ? 1024 : 1536;
            return documentAttached ? 768 : 1024;
        }

        private static InferenceParams CreateGenericInferenceParams(int maxTokens, IEnumerable<string> antiPrompts, float temperature, float minP)
        {
            return new InferenceParams
            {
                MaxTokens = maxTokens,
                AntiPrompts = antiPrompts?.ToList() ?? new List<string>(),
                SamplingPipeline = new DefaultSamplingPipeline
                {
                    Temperature = temperature,
                    MinP = minP,
                    // TopK/TopP were -1 / 1.0, i.e. sample over the ENTIRE vocabulary every
                    // token. On large-vocab models (Gemma's is ~256k) that is a real per-token
                    // cost on both CPU and GPU. A 40-token top-k plus 0.95 nucleus is the
                    // standard quality-neutral default and shrinks the candidate set ~1000x.
                    TopP = 0.95f,
                    TopK = 40,
                    RepeatPenalty = 1.1f
                }
            };
        }

        private InferenceParams CreateSamplingParamsForCurrentModel(bool thinkingEnabled, int maxTokens, IEnumerable<string> antiPrompts)
        {
            if (IsQwen3Model(_modelName))
                return ModelInferenceProfiles.CreateQwen3InferenceParams(thinkingEnabled, maxTokens, antiPrompts);

            return CreateGenericInferenceParams(
                maxTokens,
                antiPrompts,
                (float)TemperatureSlider.Value,
                (float)TopPSlider.Value);
        }

        private void ApplyModelDefaultsForCurrentSelection()
        {
            if (CtxSlider == null)
                return;

            // Reflect the context size that was actually allocated so the slider is accurate.
            if (_activeModelParams != null)
            {
                CtxSlider.Value = Math.Clamp(
                    (double)_activeModelParams.ContextSize,
                    CtxSlider.Minimum,
                    CtxSlider.Maximum);
            }
        }

        private void ShowFirstRunQwen3PromptIfNeeded()
        {
            if (_database == null)
                return;

            string lastModel = _database.GetUserFact("last_model");
            string promptShown = _database.GetSetting("qwen3_first_run_prompt_shown");
            if (!string.IsNullOrWhiteSpace(lastModel) || string.Equals(promptShown, "true", StringComparison.OrdinalIgnoreCase))
                return;

            AddChatMessage("system", $"First run: download and import {ModelInferenceProfiles.DefaultQwen3FileName} from Hugging Face to use the default {ModelInferenceProfiles.DefaultQwen3DisplayName} model. Approximate download size: 2.5 GB.");
            _database.SaveSetting("qwen3_first_run_prompt_shown", "true");
        }

        private static string BuildDefaultAssistantSystemPrompt()
        {
            return "You are Axiom's on-device-first AI assistant and expert software engineer. Give direct, concise, accurate answers, execute requested tasks when possible, and use attached documents, tool outputs, and supplied context as the highest-priority evidence. Do not expose hidden reasoning unless the product explicitly enables visible reasoning for the current mode.";
        }

        private static bool IsCodingRequest(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
                return false;

            return CodingIntentPhrases.Any(signal => message.Contains(signal, StringComparison.OrdinalIgnoreCase));
        }

        private static string AppendSystemInstruction(string systemPrompt, string instruction)
        {
            if (string.IsNullOrWhiteSpace(instruction))
                return systemPrompt ?? string.Empty;

            string prompt = (systemPrompt ?? string.Empty).TrimEnd();
            if (prompt.Contains(instruction, StringComparison.Ordinal))
                return prompt;

            return string.IsNullOrWhiteSpace(prompt)
                ? instruction
                : prompt + "\n\n" + instruction;
        }

        private async void ImportModel_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                OpenFileDialog openFileDialog = new OpenFileDialog { Filter = "GGUF files (*.gguf)|*.gguf" };
                if (openFileDialog.ShowDialog() != true)
                    return;

                if (IsMmprojFile(openFileDialog.FileName))
                {
                    // Don't dispose the loaded model here — importing a projector on top of a
                    // loaded text model enables vision for it.
                    if (_model != null && !_useGemma4LocalCliMode)
                    {
                        await LoadExplicitMmprojAsync(openFileDialog.FileName);
                    }
                    else
                    {
                        AppendSystemMessage("Selected file is an mmproj projector, not a text model. Load the main .gguf model first, then import this projector to enable vision.");
                    }
                    return;
                }

                _modelName = System.IO.Path.GetFileNameWithoutExtension(openFileDialog.FileName);

                DisposeInferenceResources(clearModel: true);

                if (IsGemma4Model(openFileDialog.FileName))
                {
                    if (!LocalGemmaCliRunner.TryResolveExecutable(out _))
                    {
                        AppendSystemMessage("ERROR: Gemma 4 local runtime not found. Install llama.cpp llama-cli and add it to PATH, or set AXIOM_LLAMA_CLI_PATH.");
                        return;
                    }

                    _useGemma4LocalCliMode = true;
                    _gemma4ModelPath = openFileDialog.FileName;
                    ShowTransientStatus("Gemma 4 local CLI mode active.");
                    UpdateHeaderDisplay();
                    PopulateNextMessageModelSelector();
                    _database?.SaveUserFact("last_model", _modelName);
                    _database?.SaveUserFact("last_model_path", openFileDialog.FileName);
                    ShowTransientStatus($"Model ready: {_modelName}");
                    ShowTransientStatus("System ready for chat.");
                    UpdateUIState(true);
                    return;
                }

                _useGemma4LocalCliMode = false;
                _gemma4ModelPath = "";

                if (IsQwen3Model(openFileDialog.FileName))
                {
                    _modelName = ModelInferenceProfiles.DefaultQwen3DisplayName;
                }

                if (_model != null)
                {
                    try
                    {
                        _executor = null;
                        _chatSession = null;
                        _model.Dispose();
                        _model = null;
                    }
                    catch { }
                }

                ShowTransientStatus($"Loading: {openFileDialog.SafeFileName}...");

                // Free any VRAM held by cached council models before planning, otherwise
                // the planner sees that memory as gone and resolves to few/zero GPU layers.
                WorkplaceViewControl?.ReleaseCachedCouncilModels();

                // CreatePlan probes hardware via external processes (powershell/nvidia-smi)
                // — run it off the dispatcher so the import click doesn't freeze the UI.
                uint requestedContext = GetDefaultContextForModel(openFileDialog.FileName);
                var recovery = ResolveCrashRecoveryPlan(openFileDialog.FileName, InferenceBackendService.CurrentMode, requestedContext);
                var plan = await Task.Run(() => InferenceBackendService.CreatePlan(openFileDialog.FileName, recovery.Context, recovery.Mode));
                ShowTransientStatus($"Context: {plan.Parameters.ContextSize} tokens | Backend: {plan.BackendName} | GPU Layers: {plan.Parameters.GpuLayerCount} | {plan.Reason}");

                try
                {
                    await InitializeModelSessionAsync(plan);
                }
                catch (Exception ex) when (plan.UsingGpu)
                {
                    await BackendLogService.LogErrorAsync("MainWindow.GPUInit", ex);
                    ShowTransientStatus($"GPU init failed ({ex.Message}). Retrying with CPU fallback...");
                    var cpuPlan = await Task.Run(() => InferenceBackendService.CreatePlan(openFileDialog.FileName, requestedContext, InferenceComputeMode.CpuOnly));
                    await InitializeModelSessionAsync(cpuPlan);
                    ShowTransientStatus("Fallback mode active: CPU");
                }
                catch (Exception ex)
                {
                    await BackendLogService.LogErrorAsync("MainWindow.ModelInit", ex);
                    ShowTransientStatus($"Model initialization failed ({GetMostRelevantError(ex)}). Retrying in CPU safe mode...");

                    var cpuPlan = await Task.Run(() => InferenceBackendService.CreatePlan(openFileDialog.FileName, requestedContext, InferenceComputeMode.CpuOnly));
                    await InitializeModelSessionAsync(cpuPlan);
                    ShowTransientStatus("Fallback mode active: CPU");
                }

                UpdateHeaderDisplay();
                ApplyModelDefaultsForCurrentSelection();
                PopulateNextMessageModelSelector();
                _database?.SaveUserFact("last_model", _modelName);
                _database?.SaveUserFact("last_model_path", openFileDialog.FileName);

                ShowTransientStatus($"Model ready: {_modelName}");
                ShowTransientStatus("System ready for chat.");
                UpdateUIState(true);
            }
            catch (Exception ex)
            {
                AppendSystemMessage($"ERROR: {ex.Message}");
                if (ex is OutOfMemoryException || ex is IOException)
                {
                    _ = ShowNonIntrusiveErrorAsync($"Model load error: {GetMostRelevantError(ex)}");
                }
                Debug.WriteLine($"Model load error: {ex}");
                UpdateUIState(false);
            }
        }

        private async Task InitializeModelSessionAsync(InferenceBackendPlan plan)
        {
            await InferenceBackendService.RunExclusiveAsync(async () =>
            {
                _activeModelParams = plan.Parameters;
                _model = await Task.Run(() => LLamaWeights.LoadFromFile(plan.Parameters));
                await TryLoadMmprojWeightsAsync(plan.Parameters.ModelPath, plan.UsingGpu);
                var context = await Task.Run(() => LlamaContextFactory.CreateContext(_model, plan.Parameters));
                _executor = CreateLocalExecutor(context);
            });

            // Stamp the loaded model+backend into the decode forensics so a native abort's
            // marker identifies exactly what died — the crash ledger reads it next launch.
            NativeDecodeForensics.SetActiveModel(plan.Parameters.ModelPath, plan.UsingGpu, plan.Parameters.GpuLayerCount);
            RebuildChatSession();
        }

        /// <summary>
        /// If a model previously died under GPU (recorded by the crash ledger), force this load
        /// onto CPU so the app cannot relaunch straight back into the same crash. A clean run
        /// clears the strike; an explicit GPU toggle (ReloadModelWithCurrentModeAsync) counts as
        /// a user retry and clears it too.
        /// </summary>
        // Decides how to (re)load a model that has crashed under GPU before, balancing the user's
        // GPU preference against stability. A native CUDA crash on the 2nd turn is almost always
        // VRAM exhaustion as the KV cache grows, so the first recovery step is NOT to abandon the
        // GPU — it is to retry on the GPU with a SMALLER context window (smaller KV cache, lower
        // per-decode VRAM pressure), which keeps GPU-class speed. Only a model that keeps crashing
        // is dropped to CPU. Returns the compute mode AND the context to request.
        private (InferenceComputeMode Mode, uint Context) ResolveCrashRecoveryPlan(
            string modelPath, InferenceComputeMode requested, uint requestedContext)
        {
            if (requested != InferenceComputeMode.GpuAccelerated || string.IsNullOrWhiteSpace(modelPath))
                return (requested, requestedContext);

            int strikes = NativeCrashLedger.GetGpuStrikes(modelPath);
            if (strikes <= 0)
                return (requested, requestedContext);

            // First/second strike: keep the GPU but shrink the context. A clean GPU turn clears
            // the strike (back to the full context next load); each further crash shrinks it more.
            if (strikes <= 2)
            {
                uint reduced = strikes == 1
                    ? (uint)Math.Max(4096, requestedContext / 2)
                    : (uint)Math.Max(2560, requestedContext / 4);

                if (reduced < requestedContext)
                {
                    AppendSystemMessage(
                        $"⚠ \"{Path.GetFileName(modelPath)}\" crashed during GPU generation before " +
                        $"(at {requestedContext} ctx). Retrying on GPU with a smaller context window " +
                        $"({reduced} tokens) to reduce VRAM pressure. If it keeps crashing it will load on CPU.");
                    return (InferenceComputeMode.GpuAccelerated, reduced);
                }
            }

            // Repeated GPU crashes (or the context is already minimal): the GPU is genuinely
            // unstable for this model on this machine. Load on CPU — stable everywhere — and keep
            // the Settings combo honest so the user can deliberately re-select GPU to force a retry.
            InferenceBackendService.CurrentMode = InferenceComputeMode.CpuOnly;
            SyncProcessingModeComboTo(InferenceComputeMode.CpuOnly);
            AppendSystemMessage(
                $"⚠ \"{Path.GetFileName(modelPath)}\" crashed repeatedly during GPU generation. " +
                "Loading on CPU for stability — select GPU Accelerated in Settings to force a GPU retry.");
            return (InferenceComputeMode.CpuOnly, requestedContext);
        }

        // Reflects the actual loaded compute mode back into the Settings combo WITHOUT triggering a
        // reload. Without this, the combo keeps showing the saved preference (e.g. "GPU Accelerated")
        // after the crash guard quietly loaded on CPU, so re-selecting GPU is a no-op (the index
        // never changes, SelectionChanged never fires) and the user can never escape CPU mode.
        private void SyncProcessingModeComboTo(InferenceComputeMode mode)
        {
            void Apply()
            {
                if (ProcessingModeCombo == null)
                    return;

                int target = mode == InferenceComputeMode.GpuAccelerated ? 1 : 0;
                if (ProcessingModeCombo.SelectedIndex == target)
                    return;

                _suppressProcessingModeComboReload = true;
                try { ProcessingModeCombo.SelectedIndex = target; }
                finally { _suppressProcessingModeComboReload = false; }
            }

            if (Dispatcher.CheckAccess())
                Apply();
            else
                Dispatcher.Invoke(Apply);
        }

        // Creates the chat executor, attaching the multimodal projector when one is loaded
        // so vision-capable GGUF models can analyze image attachments locally.
        private InteractiveExecutor CreateLocalExecutor(LLamaContext context)
        {
            return _mtmdWeights != null
                ? new InteractiveExecutor(context, _mtmdWeights)
                : new InteractiveExecutor(context);
        }

        // User explicitly picked an mmproj file while a text model is loaded: bind it as the
        // vision projector and rebuild the executor so the pairing takes effect.
        private async Task LoadExplicitMmprojAsync(string mmprojPath)
        {
            try
            {
                try { _mtmdWeights?.Dispose(); } catch { }
                _mtmdWeights = null;
                _mmprojPath = "";

                bool useGpu = InferenceBackendService.CurrentMode != InferenceComputeMode.CpuOnly;
                var mtmdParams = MtmdContextParams.Default();
                mtmdParams.UseGpu = useGpu;

                try
                {
                    _mtmdWeights = await Task.Run(() => MtmdWeights.LoadFromFile(mmprojPath, _model, mtmdParams));
                }
                catch (Exception gpuEx) when (useGpu)
                {
                    await BackendLogService.LogErrorAsync("MainWindow.MmprojGpuLoad", gpuEx);
                    mtmdParams.UseGpu = false;
                    _mtmdWeights = await Task.Run(() => MtmdWeights.LoadFromFile(mmprojPath, _model, mtmdParams));
                }

                _mmprojPath = mmprojPath;
                await ResetExecutorContextAsync();
                ShowTransientStatus($"Vision enabled: {Path.GetFileName(mmprojPath)} (image attachments can be analyzed locally)");
            }
            catch (Exception ex)
            {
                await BackendLogService.LogErrorAsync("MainWindow.MmprojExplicitLoad", ex);
                try { _mtmdWeights?.Dispose(); } catch { }
                _mtmdWeights = null;
                _mmprojPath = "";
                AppendSystemMessage($"Projector load failed ({GetMostRelevantError(ex)}). Check that it matches the loaded model.");
            }
        }

        // Looks for an mmproj projector GGUF next to the model file and loads it when found.
        // Failure is non-fatal: the model simply runs text-only.
        private async Task TryLoadMmprojWeightsAsync(string modelPath, bool useGpu)
        {
            try { _mtmdWeights?.Dispose(); } catch { }
            _mtmdWeights = null;
            _mmprojPath = "";

            if (_model == null || string.IsNullOrWhiteSpace(modelPath))
                return;

            try
            {
                string? modelDir = Path.GetDirectoryName(modelPath);
                if (string.IsNullOrWhiteSpace(modelDir) || !Directory.Exists(modelDir))
                    return;

                string mmprojPath = SelectBestMmprojCandidate(modelPath, Directory.EnumerateFiles(modelDir, "*.gguf").Where(IsMmprojFile).ToList());
                if (string.IsNullOrWhiteSpace(mmprojPath))
                    return;

                var mtmdParams = MtmdContextParams.Default();
                mtmdParams.UseGpu = useGpu;

                try
                {
                    _mtmdWeights = await Task.Run(() => MtmdWeights.LoadFromFile(mmprojPath, _model, mtmdParams));
                }
                catch (Exception gpuEx) when (useGpu)
                {
                    await BackendLogService.LogErrorAsync("MainWindow.MmprojGpuLoad", gpuEx);
                    mtmdParams.UseGpu = false;
                    _mtmdWeights = await Task.Run(() => MtmdWeights.LoadFromFile(mmprojPath, _model, mtmdParams));
                }

                _mmprojPath = mmprojPath;
                ShowTransientStatus($"Vision enabled: {Path.GetFileName(mmprojPath)} (image attachments can be analyzed locally)");
            }
            catch (Exception ex)
            {
                await BackendLogService.LogErrorAsync("MainWindow.MmprojLoad", ex);
                try { _mtmdWeights?.Dispose(); } catch { }
                _mtmdWeights = null;
                _mmprojPath = "";
                AppendSystemMessage($"Vision projector found but failed to load ({GetMostRelevantError(ex)}). Continuing text-only.");
            }
        }

        // Prefers the projector whose file name shares the longest prefix with the model name
        // (e.g. "mmproj-Qwen2.5-VL-7B..." next to "Qwen2.5-VL-7B...").
        private static string SelectBestMmprojCandidate(string modelPath, IReadOnlyList<string> candidates)
        {
            if (candidates == null || candidates.Count == 0)
                return string.Empty;
            if (candidates.Count == 1)
                return candidates[0];

            string modelStem = Path.GetFileNameWithoutExtension(modelPath).ToLowerInvariant();
            return candidates
                .OrderByDescending(c =>
                {
                    string stem = Path.GetFileNameWithoutExtension(c).ToLowerInvariant()
                        .Replace("mmproj-", string.Empty)
                        .Replace("mmproj", string.Empty)
                        .Trim('-', '_', '.');
                    int common = 0;
                    while (common < Math.Min(stem.Length, modelStem.Length) && stem[common] == modelStem[common])
                        common++;
                    return common;
                })
                .ThenBy(c => c, StringComparer.OrdinalIgnoreCase)
                .First();
        }

        // Loads current image attachments into the executor's pending multimodal embeds.
        // Returns the number of images queued; the caller must place one image marker per
        // embed inside the user-turn text before inference.
        private int AttachPendingImageEmbedsToExecutor()
        {
            if (_mtmdWeights == null || _executor == null)
                return 0;

            // Drop any embeds left over from a cancelled or failed turn so they don't
            // silently attach to this one.
            try
            {
                foreach (var stale in _executor.Embeds)
                {
                    try { stale?.Dispose(); } catch { }
                }
                _executor.Embeds.Clear();
            }
            catch { }

            int added = 0;
            foreach (var doc in _chatDocuments.Where(d => d.IsImage && !string.IsNullOrWhiteSpace(d.Base64Data)))
            {
                try
                {
                    byte[] bytes = Convert.FromBase64String(doc.Base64Data);
                    var embed = _mtmdWeights.LoadMedia(bytes);
                    if (embed != null)
                    {
                        _executor.Embeds.Add(embed);
                        added++;
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Image embed load failed for {doc.Name}: {ex.Message}");
                    _ = BackendLogService.LogEventAsync("LocalVisionEmbed", $"Failed:{doc.Name}\n{ex.Message}");
                }
            }

            return added;
        }

        // One "<image>" tag per embed — the executor replaces it with the native media marker.
        private static string PrependImageMarkers(string userText, int imageCount)
        {
            if (imageCount <= 0)
                return userText;

            var sb = new StringBuilder();
            for (int i = 0; i < imageCount; i++)
                sb.Append("<image>\n");
            sb.Append(userText);
            return sb.ToString();
        }

        private void RebuildChatSession()
        {
            if (_executor == null || _model == null)
                return;

            string sysPrompt = SystemPromptBox?.Text?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(sysPrompt))
                sysPrompt = "You are a helpful AI assistant and expert software engineer. Provide clear, concise, and accurate responses. Execute tasks directly and provide final output unless explicitly asked for step-by-step guidance.";

            sysPrompt += "\n\nFor complex math or data processing tasks, solve them by writing a Python 3 script internally. " +
                "Use print() for the final answer. Never expose internal Python/tool code, PAUSE directives, or raw calculation traces unless the user explicitly asks for them. " +
                "In workplace/council mode, this can be triggered via [PAUSE: PYTHON_MATH | \"your_code_here\"].";
            sysPrompt = BuildQwen3SystemPrompt(sysPrompt, false);

            _chatSession = new LLama.ChatSession(_executor);
            _chatSession.WithHistoryTransform(new PromptTemplateTransformer(_model, withAssistant: true));
            _chatSession.AddSystemMessage(sysPrompt);
        }

        private async Task RebuildChatSessionWithPromptAsync(string systemPrompt, CancellationToken token = default)
        {
            if (_model == null || _activeModelParams == null)
                return;

            // Free the old KV-cache BEFORE allocating a new one. The previous pattern kept
            // both alive simultaneously, which caused double-peak VRAM usage that crashes
            // on models ≥7B where free VRAM is tight after weight loading.
            //
            // Disposing the old context and creating its replacement are native-context
            // mutations: hold the native-decode gate so a background KV SaveState (fired
            // from the previous turn's persistence) or any other surface cannot be reading
            // this context while it is torn down — concurrent access aborts llama.cpp
            // natively (ucrtbase 0xc0000409). The caller replays history under the same
            // gate afterwards, so no decode runs before this completes.
            await InferenceBackendService.RunScopedExclusiveAsync(InferenceBackendService.NormalChatScope, async () =>
            {
                var oldExecutor = _executor;
                _executor = null;
                _chatSession = null;
                try { oldExecutor?.Context?.Dispose(); } catch { }

                var newContext = await Task.Run(() => LlamaContextFactory.CreateContext(_model, _activeModelParams), token).ConfigureAwait(false);
                var newExecutor = CreateLocalExecutor(newContext);
                _executor = newExecutor;
            }).ConfigureAwait(false);

            if (_executor == null)
                return;

            _chatSession = new LLama.ChatSession(_executor);
            _chatSession.WithHistoryTransform(new PromptTemplateTransformer(_model, withAssistant: true));
            _chatSession.AddSystemMessage(systemPrompt);
        }

        private static bool IsStrictChatMlModel(string modelName)
        {
            if (string.IsNullOrWhiteSpace(modelName))
            {
                return false;
            }

            return modelName.Contains("NVIDIA-Nemotron-3-Nano-4B-Q4_K_M", StringComparison.OrdinalIgnoreCase)
                   || modelName.Contains("Qwen2.5-4B", StringComparison.OrdinalIgnoreCase)
                   || modelName.Contains("Qwen3", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsGemma4Model(string modelName)
        {
            if (string.IsNullOrWhiteSpace(modelName))
                return false;

            return modelName.Contains("gemma-4", StringComparison.OrdinalIgnoreCase)
                   || modelName.Contains("gemma4", StringComparison.OrdinalIgnoreCase)
                   || modelName.Contains("gemma_4", StringComparison.OrdinalIgnoreCase);
        }

        // Returns true for Gemma 1/2/3/3n — these are loaded via LLamaSharp (not CLI runner)
        // and use the chat template embedded in the GGUF file.
        private static bool IsGemmaModel(string modelName)
        {
            if (string.IsNullOrWhiteSpace(modelName))
                return false;
            return modelName.Contains("gemma", StringComparison.OrdinalIgnoreCase)
                   && !IsGemma4Model(modelName);
        }

        private static bool IsLlamaModel(string modelName)
        {
            if (string.IsNullOrWhiteSpace(modelName))
                return false;
            return modelName.Contains("llama", StringComparison.OrdinalIgnoreCase)
                   || modelName.Contains("meta-llama", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsMistralModel(string modelName)
        {
            if (string.IsNullOrWhiteSpace(modelName))
                return false;
            return modelName.Contains("mistral", StringComparison.OrdinalIgnoreCase)
                   || modelName.Contains("mixtral", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsPhiModel(string modelName)
        {
            if (string.IsNullOrWhiteSpace(modelName))
                return false;
            return modelName.Contains("phi-3", StringComparison.OrdinalIgnoreCase)
                   || modelName.Contains("phi-4", StringComparison.OrdinalIgnoreCase)
                   || modelName.Contains("phi3", StringComparison.OrdinalIgnoreCase)
                   || modelName.Contains("phi4", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsDeepSeekModel(string modelName)
        {
            if (string.IsNullOrWhiteSpace(modelName))
                return false;
            return modelName.Contains("deepseek", StringComparison.OrdinalIgnoreCase);
        }

        // Returns the context size to REQUEST for a model before loading it. The request
        // is an upper bound, not an allocation: BuildSafeModelParams shrinks it from real
        // memory math (GGUF KV geometry vs. free VRAM/RAM). The old file-size table here
        // pre-capped large models at 2-3k tokens regardless of available memory — a 9B
        // quant over 8 GB was stranded at 3,072 even on machines that fit 8k+ comfortably.
        private uint GetDefaultContextForModel(string modelFilePath)
        {
            if (IsQwen3Model(modelFilePath)) return 8192u;

            // Prefer the model's trained context from the GGUF header, capped at 16k —
            // beyond that the KV cache dominates desktop memory for little chat benefit.
            GgufModelMetadata? meta = GgufMetadataReader.TryRead(modelFilePath);
            if (meta?.ContextLength is > 0)
                return (uint)Math.Clamp(meta.ContextLength, 1024, 16384);

            return 8192u;
        }

        private static bool IsMmprojFile(string modelPath)
        {
            string file = System.IO.Path.GetFileName(modelPath);
            return file.StartsWith("mmproj", StringComparison.OrdinalIgnoreCase)
                   || file.Contains("mmproj-", StringComparison.OrdinalIgnoreCase);
        }

        private static string BuildStrictChatMlPrompt(string systemPrompt, string userPrompt)
        {
            return BuildStrictChatMlPrompt(systemPrompt, null, userPrompt);
        }

        private static string BuildStrictChatMlPrompt(string systemPrompt, IReadOnlyList<(string Role, string Content)>? historyTurns, string userPrompt)
        {
            var sb = new StringBuilder();
            sb.Append("<|im_start|>system\n").Append(systemPrompt).Append("<|im_end|>\n");

            foreach (var (role, content) in historyTurns ?? [])
                sb.Append("<|im_start|>").Append(role).Append('\n').Append(content).Append("<|im_end|>\n");

            sb.Append("<|im_start|>user\n").Append(userPrompt).Append("<|im_end|>\n<|im_start|>assistant\n");
            return sb.ToString();
        }

        private List<(string Role, string Content)> BuildChatMlHistoryTurns(IEnumerable<ChatMessage> selectedMessages)
        {
            var turns = new List<(string Role, string Content)>();
            foreach (var msg in selectedMessages ?? [])
            {
                if (string.Equals(msg.Role, "user", StringComparison.OrdinalIgnoreCase))
                {
                    if (!string.IsNullOrWhiteSpace(msg.Content))
                        turns.Add(("user", msg.Content));
                    continue;
                }

                if (string.Equals(msg.Role, "assistant", StringComparison.OrdinalIgnoreCase))
                {
                    string cleaned = SanitizeAssistantContentForInference(msg.Content);
                    if (!string.IsNullOrWhiteSpace(cleaned)
                        && turns.Count > 0
                        && string.Equals(turns[^1].Role, "user", StringComparison.OrdinalIgnoreCase))
                        turns.Add(("assistant", cleaned));
                }
            }

            return turns;
        }

        private static bool IsLikelyConversationFollowUp(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
                return false;

            string lower = message.Trim().ToLowerInvariant();
            if (ContainsDocumentFollowUpCue(lower))
                return true;

            if (ConversationalFollowUpPhrases.Any(lower.Contains))
                return true;

            if (lower.Length <= 80 && Regex.IsMatch(lower, @"\b(it|that|this|they|them|those|these|same|again|also)\b", RegexOptions.IgnoreCase))
                return true;

            return false;
        }

        private bool ShouldStartFreshContextWindow(string currentUserMessage, IReadOnlyList<ChatMessage> allMessages)
        {
            if (allMessages == null || allMessages.Count < 4 || string.IsNullOrWhiteSpace(currentUserMessage))
                return false;

            if (IsLikelyConversationFollowUp(currentUserMessage)
                || ShouldInjectPersistentDocumentContext(currentUserMessage)
                || ContainsExplicitWebSearchRequest(currentUserMessage))
            {
                return false;
            }

            HashSet<string> currentWords = ExtractSignificantWords(currentUserMessage);
            if (currentWords.Count < 3)
                return false;

            List<string> recentUserMessages = allMessages
                .Where(m => string.Equals(m.Role, "user", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(m.Content))
                .TakeLast(3)
                .Select(m => m.Content)
                .ToList();

            if (recentUserMessages.Count == 0)
                return false;

            double maxRecentOverlap = recentUserMessages
                .Select(message => ComputeWordOverlapRatio(currentWords, ExtractSignificantWords(message)))
                .DefaultIfEmpty(0)
                .Max();

            return maxRecentOverlap < 0.04 && ScoreQueryComplexity(currentUserMessage) <= 4;
        }

        private async Task<int> StreamIsolatedSingleTurnInferenceAsync(
            string systemPrompt,
            string userPrompt,
            InferenceParams inferenceParams,
            StringBuilder responseBuilder,
            bool thinkingModeEnabled,
            CancellationToken token)
        {
            bool isGemma4 = IsGemma4Model(_modelName);
            bool isStrictChatMl = IsStrictChatMlModel(_modelName);
            systemPrompt = BuildQwen3SystemPrompt(systemPrompt, thinkingModeEnabled);

            if (_useGemma4LocalCliMode)
            {
                string prompt = Gemma4Formatter.BuildPrompt(systemPrompt, new List<(string Role, string Content)>(), userPrompt, false);
                string response = await LocalGemmaCliRunner.InferAsync(
                    _gemma4ModelPath,
                    prompt,
                    inferenceParams.MaxTokens,
                    (float)TemperatureSlider.Value,
                    (float)TopPSlider.Value,
                    new[] { Gemma4Formatter.TurnClose, "<|/channel|>" },
                    token);

                responseBuilder.Append(response);
                if (!thinkingModeEnabled)
                {
                    await Dispatcher.InvokeAsync(() =>
                    {
                        if (_currentStreamingMessage != null)
                        {
                            _currentStreamingMessage.Content = response;
                            ScrollChatToEnd();
                        }
                    });
                }

                return Math.Max(1, response.Length / 4);
            }

            if (_model == null || _activeModelParams == null)
                return 0;

            // Context allocation reserves the full KV cache (potentially GBs of VRAM) —
            // never run it synchronously on the UI thread.
            using var tempContext = await Task.Run(() => LlamaContextFactory.CreateContext(_model, _activeModelParams), token).ConfigureAwait(false);
            var tempExecutor = new InteractiveExecutor(tempContext);

            if (isStrictChatMl || isGemma4)
            {
                string prompt = FitLocalPromptToContextWindow(
                    systemPrompt,
                    new List<(string Role, string Content)>(),
                    userPrompt,
                    isGemma4,
                    inferenceParams);

                int isolatedPromptTokens = CountLocalPromptTokens(prompt);
                EnsureLocalPromptFitsOrThrow(isolatedPromptTokens, "NormalChat.IsolatedStrictPrompt");
                NativeDecodeForensics.BeginDecode("NormalChat.IsolatedStrictPrompt", isolatedPromptTokens, GetLoadedLocalContextSize(), _modelName);
                try
                {
                    return await StreamInferenceAsync(
                        () => tempExecutor.InferAsync(prompt, inferenceParams, token),
                        responseBuilder,
                        thinkingModeEnabled,
                        token);
                }
                finally
                {
                    NativeDecodeForensics.EndDecode();
                }
            }

            // This session decodes system prompt + user turn natively — fit them to the
            // context window first; an oversized decode aborts the entire process.
            ChatSessionPromptPlan isolatedPlan = FitChatSessionPlanToContextWindow(
                systemPrompt,
                new List<ChatMessage>(),
                userPrompt,
                inferenceParams);

            var tempSession = new LLama.ChatSession(tempExecutor);
            tempSession.WithHistoryTransform(new PromptTemplateTransformer(_model, withAssistant: true));
            tempSession.AddSystemMessage(isolatedPlan.SystemPrompt);
            NativeDecodeForensics.BeginDecode("NormalChat.IsolatedSessionTurn", isolatedPlan.EstimatedPromptTokens, GetLoadedLocalContextSize(), _modelName);
            try
            {
                return await StreamInferenceAsync(
                    () => tempSession.ChatAsync(new ChatHistory.Message(AuthorRole.User, isolatedPlan.UserMessage), isolatedPlan.InferenceParams, token),
                    responseBuilder,
                    thinkingModeEnabled,
                    token);
            }
            finally
            {
                NativeDecodeForensics.EndDecode();
            }
        }

        private static string AppendSingleTurnSystemTail(string systemPrompt, string webSourcesBlock, bool thinkingModeEnabled)
        {
            var tail = new StringBuilder((systemPrompt ?? string.Empty).TrimEnd());

            if (!string.IsNullOrWhiteSpace(webSourcesBlock))
            {
                if (tail.Length > 0)
                    tail.Append("\n\n");

                tail.Append(webSourcesBlock.Trim());
                tail.Append("\n");
                tail.Append(BuildSingleTurnWebSearchInstruction());
            }

            if (thinkingModeEnabled)
            {
                if (tail.Length > 0)
                    tail.Append("\n\n");

                tail.Append("Before answering, reason through this step by step. Work through your logic, consider the evidence from any sources provided, and verify your conclusions before writing your final answer.");
                tail.Append("\n");
                tail.Append(ReasoningParser.FinalAnswerDelimiter);
            }

            return tail.ToString();
        }

        private static HashSet<string> ExtractSignificantWords(string text)
        {
            var stopWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "the","and","for","with","that","this","from","into","about","have","has","are",
                "was","were","will","just","what","how","can","does","please","make","create",
                "write","give","need","want","should","could","would","also","like","using","you",
                "your","they","them","their","there","here","then","than"
            };

            return Regex.Matches(text ?? string.Empty, @"\b[a-z][a-z0-9_]{2,}\b", RegexOptions.IgnoreCase)
                .Select(m => m.Value)
                .Where(w => !stopWords.Contains(w))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        private static double ComputeWordOverlapRatio(HashSet<string> leftWords, HashSet<string> rightWords)
        {
            if (leftWords.Count == 0 || rightWords.Count == 0)
                return 0;

            int overlap = leftWords.Intersect(rightWords, StringComparer.OrdinalIgnoreCase).Count();
            return (double)overlap / Math.Max(leftWords.Count, rightWords.Count);
        }

        private static int ScoreQueryComplexity(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
                return 0;

            int score = 0;
            int questionMarks = message.Count(c => c == '?');
            if (questionMarks > 1)
                score += (questionMarks - 1) * 2;

            string lower = message.ToLowerInvariant();
            if (ComplexityReasoningPhrases.Any(lower.Contains))
                score += 2;
            if (ComplexityAnalysisWords.Any(w => Regex.IsMatch(lower, $@"\b{Regex.Escape(w)}\b")))
                score += 2;
            if (ComplexityConditionalWords.Any(w => Regex.IsMatch(lower, $@"\b{Regex.Escape(w)}\b")))
                score += 1;
            if (message.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length > 120)
                score += 1;

            return Math.Clamp(score, 0, 10);
        }

        private static ThinkingGateDecision EvaluateThinkingGate(string message, bool toggleEnabled)
        {
            int score = ScoreQueryComplexity(message);
            if (!toggleEnabled)
            {
                return new ThinkingGateDecision
                {
                    Score = score,
                    UseThinking = false,
                    UseReasoningPhaseCap = false,
                    Decision = "ToggleOff"
                };
            }

            if (score <= 2)
            {
                return new ThinkingGateDecision
                {
                    Score = score,
                    UseThinking = false,
                    UseReasoningPhaseCap = false,
                    Decision = "BypassThinking"
                };
            }

            if (score >= 8)
            {
                return new ThinkingGateDecision
                {
                    Score = score,
                    UseThinking = true,
                    UseReasoningPhaseCap = true,
                    Decision = "ThinkingCapped400"
                };
            }

            return new ThinkingGateDecision
            {
                Score = score,
                UseThinking = true,
                UseReasoningPhaseCap = false,
                Decision = "ThinkingNormal"
            };
        }

        private static string CapOversizedInjectionBlock(string prompt, string label)
        {
            if (string.IsNullOrWhiteSpace(prompt) || string.IsNullOrWhiteSpace(label))
                return prompt;

            string pattern = $@"\[\[{Regex.Escape(label)}\]\]\s*(?<body>[\s\S]*?)\s*\[\[END {Regex.Escape(label)}\]\]";
            return Regex.Replace(prompt, pattern, match =>
            {
                string body = match.Groups["body"].Value.Trim();
                if (body.Length <= 1200)
                    return match.Value;

                int cut = body.LastIndexOf(' ', 1200);
                if (cut <= 0)
                    cut = 1200;

                string truncated = body[..cut].TrimEnd() + "...";
                return $"[[{label}]]\n{truncated}\n[[END {label}]]";
            }, RegexOptions.IgnoreCase);
        }

        private static string CapOversizedInjections(string prompt)
        {
            string result = prompt ?? string.Empty;
            foreach (string label in CappedInjectionLabels)
                result = CapOversizedInjectionBlock(result, label);
            return result;
        }

        private static string PruneStaleToolInjections(string prompt, IReadOnlyList<PromptInjectionBlockInfo> injections, string currentUserMessage)
        {
            if (string.IsNullOrWhiteSpace(prompt) || injections == null || injections.Count == 0)
                return prompt;

            string result = prompt;
            foreach (PromptInjectionBlockInfo injection in injections)
            {
                if (injection.IsCurrentTurn || injection.IsCurrentPreflight || injection.TurnAge <= 2)
                    continue;
                if (!StalePrunableInjectionLabels.Contains(injection.Label))
                    continue;

                string pattern = $@"\[\[{Regex.Escape(injection.Label)}\]\]\s*(?<body>[\s\S]*?)\s*\[\[END {Regex.Escape(injection.Label)}\]\]\s*";
                Match match = Regex.Match(result, pattern, RegexOptions.IgnoreCase);
                if (!match.Success)
                    continue;

                double overlap = WebSearchService.GetWordOverlap(match.Groups["body"].Value, currentUserMessage);
                if (overlap < 0.15)
                    result = result.Remove(match.Index, match.Length);
            }

            return result.Trim();
        }

        private static List<PromptInjectionBlockInfo> CollectPromptInjectionInfos(IEnumerable<ChatMessage> selectedMessages)
        {
            var infos = new List<PromptInjectionBlockInfo>();
            if (selectedMessages == null)
                return infos;

            int userTurnIndex = -1;
            foreach (ChatMessage message in selectedMessages)
            {
                if (string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase))
                    userTurnIndex++;

                if (string.IsNullOrWhiteSpace(message.Content))
                    continue;

                foreach (Match match in LabeledInjectionBlockRegex.Matches(message.Content))
                {
                    infos.Add(new PromptInjectionBlockInfo
                    {
                        Label = match.Groups["label"].Value.Trim(),
                        TurnAge = Math.Max(0, userTurnIndex),
                        IsCurrentTurn = false,
                        IsCurrentPreflight = false
                    });
                }
            }

            if (infos.Count == 0)
                return infos;

            int latestTurn = infos.Max(i => i.TurnAge);
            return infos.Select(i => new PromptInjectionBlockInfo
            {
                Label = i.Label,
                TurnAge = Math.Max(0, latestTurn - i.TurnAge + 1),
                IsCurrentTurn = false,
                IsCurrentPreflight = false
            }).ToList();
        }

        private static string ApplyPreInferenceContextReduction(string prompt, IReadOnlyList<PromptInjectionBlockInfo> injections, string currentUserMessage)
        {
            string capped = CapOversizedInjections(prompt);
            return PruneStaleToolInjections(capped, injections, currentUserMessage);
        }

        private static readonly Regex LocalDocumentContextBlockRegex = new(@"\[\[DOCUMENT CONTEXT\]\]\s*(?<body>[\s\S]*?)\s*\[\[END DOCUMENT CONTEXT\]\]", RegexOptions.Compiled);

        private int CountLocalPromptTokens(string prompt)
        {
            try
            {
                var context = _executor?.Context;
                if (context != null)
                    return context.Tokenize(prompt ?? string.Empty, addBos: true, special: true).Length;
            }
            catch
            {
                // Fall through to the conservative character estimate.
            }

            return (prompt?.Length ?? 0) / 3;
        }

        private static string ShrinkDocumentContextBlock(string systemPrompt)
        {
            Match match = LocalDocumentContextBlockRegex.Match(systemPrompt ?? string.Empty);
            if (!match.Success)
                return systemPrompt ?? string.Empty;

            string body = match.Groups["body"].Value;
            if (body.Length <= 1000)
                return systemPrompt!;

            string truncated = body[..(body.Length / 2)].TrimEnd()
                + "\n[...document context truncated to fit the model's context window]";
            return systemPrompt!.Remove(match.Index, match.Length)
                .Insert(match.Index, $"[[DOCUMENT CONTEXT]]\n{truncated}\n[[END DOCUMENT CONTEXT]]");
        }

        /// <summary>
        /// Guarantees the rendered single-shot prompt fits the loaded model's context window.
        /// An oversized decode does not throw — llama.cpp asserts and aborts the entire process
        /// (ucrtbase 0xc0000409) — so this trims, in order: oldest history turns, the document
        /// context block, then system/user text, until the prompt plus the generation reserve fit.
        /// </summary>
        private string FitLocalPromptToContextWindow(
            string systemPrompt,
            List<(string Role, string Content)> historyTurns,
            string userPrompt,
            bool isGemma4,
            InferenceParams inferenceParams)
        {
            int contextSize = GetLoadedLocalContextSize();
            int generationReserve = Math.Clamp(inferenceParams?.MaxTokens ?? 2048, 256, 2048);
            int promptBudget = contextSize - generationReserve - 64;
            if (promptBudget < 512)
                promptBudget = Math.Max(384, contextSize / 2);

            string system = systemPrompt ?? string.Empty;
            var turns = new List<(string Role, string Content)>(historyTurns ?? []);
            string user = userPrompt ?? string.Empty;

            string BuildPrompt() => isGemma4
                ? Gemma4Formatter.BuildPrompt(system, turns, user, false)
                : BuildStrictChatMlPrompt(system, turns, user);

            string prompt = BuildPrompt();
            int initialTokens = CountLocalPromptTokens(prompt);
            if (initialTokens <= promptBudget)
                return prompt;

            while (turns.Count > 0)
            {
                turns.RemoveAt(0);
                prompt = BuildPrompt();
                if (CountLocalPromptTokens(prompt) <= promptBudget)
                {
                    LogLocalPromptFitGuard(contextSize, promptBudget, initialTokens, prompt, historyTurns?.Count - turns.Count ?? 0);
                    return prompt;
                }
            }

            for (int attempt = 0; attempt < 8; attempt++)
            {
                // The document block now lives in the user turn; shrink it there first, then
                // fall back to the system prompt (older callers may still place it there).
                string shrunkUser = ShrinkDocumentContextBlock(user);
                string shrunkSystem = ShrinkDocumentContextBlock(system);
                if (string.Equals(shrunkUser, user, StringComparison.Ordinal)
                    && string.Equals(shrunkSystem, system, StringComparison.Ordinal))
                    break;

                user = shrunkUser;
                system = shrunkSystem;
                prompt = BuildPrompt();
                if (CountLocalPromptTokens(prompt) <= promptBudget)
                {
                    LogLocalPromptFitGuard(contextSize, promptBudget, initialTokens, prompt, historyTurns?.Count ?? 0);
                    return prompt;
                }
            }

            int tokens = CountLocalPromptTokens(prompt);
            while (tokens > promptBudget && system.Length > 800)
            {
                system = system[..(system.Length * 3 / 4)];
                prompt = BuildPrompt();
                tokens = CountLocalPromptTokens(prompt);
            }

            while (tokens > promptBudget && user.Length > 400)
            {
                user = user[..(user.Length * 3 / 4)];
                prompt = BuildPrompt();
                tokens = CountLocalPromptTokens(prompt);
            }

            LogLocalPromptFitGuard(contextSize, promptBudget, initialTokens, prompt, historyTurns?.Count ?? 0);
            return prompt;
        }

        private void LogLocalPromptFitGuard(int contextSize, int promptBudget, int initialTokens, string finalPrompt, int droppedTurns)
        {
            _ = BackendLogService.LogEventAsync(
                "LocalPromptFitGuard",
                $"Ctx:{contextSize}\nBudget:{promptBudget}\nInitialTokens:{initialTokens}\nFinalTokens:{CountLocalPromptTokens(finalPrompt)}\nHistoryTurnsDropped:{droppedTurns}");
        }

        private sealed class ChatSessionPromptPlan
        {
            public string SystemPrompt = string.Empty;
            public List<ChatMessage> HistoryMessages = new();
            public string UserMessage = string.Empty;
            public InferenceParams InferenceParams = null!;
            public int EstimatedPromptTokens;
        }

        /// <summary>
        /// Tokenizer-verified fit guard for the ChatSession (non-strict-template) path. That
        /// path decodes the system prompt, every replayed history message, and the user turn
        /// natively without ever passing through <see cref="FitLocalPromptToContextWindow"/>,
        /// so an attached-document turn could overflow the context window and abort llama.cpp
        /// — killing the process. Trims, in order: oldest history messages, the document
        /// context block, the system tail, then the user tail; as a last resort it shrinks
        /// the generation reserve instead of refusing the turn.
        /// </summary>
        private ChatSessionPromptPlan FitChatSessionPlanToContextWindow(
            string systemPrompt,
            IEnumerable<ChatMessage> historyMessages,
            string userMessage,
            InferenceParams inferenceParams)
        {
            int contextSize = GetLoadedLocalContextSize();
            int generationReserve = Math.Clamp(inferenceParams?.MaxTokens ?? 2048, 256, 2048);

            List<ChatMessage> history = NormalizeMessagesForChatSession(historyMessages);
            string system = systemPrompt ?? string.Empty;
            string user = userMessage ?? string.Empty;

            // Chat templates wrap every message in role markers the raw token count misses.
            int TemplateOverhead() => (history.Count + 2) * 16 + 128;
            int PromptBudget() => contextSize - generationReserve - TemplateOverhead();
            int TotalTokens() => CountLocalPromptTokens(system)
                + history.Sum(m => CountLocalPromptTokens(m.Content ?? string.Empty))
                + CountLocalPromptTokens(user);

            int initialTokens = TotalTokens();
            int tokens = initialTokens;
            int droppedMessages = 0;

            while (tokens > PromptBudget() && history.Count > 0)
            {
                history.RemoveAt(0);
                droppedMessages++;
                tokens = TotalTokens();
            }

            for (int attempt = 0; attempt < 8 && tokens > PromptBudget(); attempt++)
            {
                // The document block rides in the user turn now; shrink it there first.
                string shrunkUser = ShrinkDocumentContextBlock(user);
                string shrunkSystem = ShrinkDocumentContextBlock(system);
                if (string.Equals(shrunkUser, user, StringComparison.Ordinal)
                    && string.Equals(shrunkSystem, system, StringComparison.Ordinal))
                    break;

                user = shrunkUser;
                system = shrunkSystem;
                tokens = TotalTokens();
            }

            while (tokens > PromptBudget() && system.Length > 800)
            {
                system = system[..(system.Length * 3 / 4)];
                tokens = TotalTokens();
            }

            while (tokens > PromptBudget() && user.Length > 400)
            {
                user = user[..(user.Length * 3 / 4)];
                tokens = TotalTokens();
            }

            InferenceParams fittedParams = inferenceParams!;
            if (tokens > PromptBudget())
            {
                int remainingForGeneration = contextSize - tokens - TemplateOverhead();
                if (remainingForGeneration >= 256)
                    fittedParams = CloneInferenceParams(inferenceParams!, remainingForGeneration);
            }

            if (tokens != initialTokens || droppedMessages > 0)
            {
                _ = BackendLogService.LogEventAsync(
                    "LocalPromptFitGuard",
                    $"Path:ChatSession\nCtx:{contextSize}\nBudget:{PromptBudget()}\nInitialTokens:{initialTokens}\nFinalTokens:{tokens}\nHistoryMessagesDropped:{droppedMessages}\nMaxTokens:{fittedParams?.MaxTokens ?? 0}");
            }

            return new ChatSessionPromptPlan
            {
                SystemPrompt = system,
                HistoryMessages = history,
                UserMessage = user,
                InferenceParams = fittedParams!,
                EstimatedPromptTokens = tokens
            };
        }

        /// <summary>
        /// Final pre-decode gate: an oversized native decode does not throw — llama.cpp
        /// asserts and aborts the entire process — so refuse it here with a managed
        /// exception the send pipeline turns into an in-chat error message instead.
        /// </summary>
        private void EnsureLocalPromptFitsOrThrow(int promptTokens, string stage)
        {
            int contextSize = GetLoadedLocalContextSize();
            if (promptTokens <= contextSize - 192)
                return;

            _ = BackendLogService.LogEventAsync(
                "LocalDecodeRefused",
                $"Stage:{stage}\nPromptTokens:{promptTokens}\nCtx:{contextSize}\nModel:{_modelName}");

            throw new InvalidOperationException(
                $"This request needs ~{promptTokens:N0} prompt tokens but the loaded model's context window is {contextSize:N0}. " +
                "The decode was stopped before it could crash the local backend — try a New Chat, remove or shrink attachments, " +
                "or load the model with a larger context size.");
        }

        private static List<ChatMessage> NormalizeMessagesForChatSession(IEnumerable<ChatMessage> messages)
        {
            var normalized = new List<ChatMessage>();
            if (messages == null)
                return normalized;

            ChatMessage? pendingUser = null;
            foreach (ChatMessage message in messages)
            {
                if (message == null)
                    continue;

                ChatMessage normalizedMessage = CloneMessageForInferenceContext(message);
                if (string.IsNullOrWhiteSpace(normalizedMessage.Content))
                    continue;

                if (string.Equals(normalizedMessage.Role, "user", StringComparison.OrdinalIgnoreCase))
                {
                    pendingUser = normalizedMessage;
                    continue;
                }

                if (string.Equals(normalizedMessage.Role, "assistant", StringComparison.OrdinalIgnoreCase))
                {
                    if (pendingUser != null)
                    {
                        normalized.Add(pendingUser);
                        pendingUser = null;
                    }

                    if (normalized.Count > 0 && string.Equals(normalized[^1].Role, "user", StringComparison.OrdinalIgnoreCase))
                        normalized.Add(normalizedMessage);
                }
            }

            if (pendingUser != null)
                normalized.Add(pendingUser);

            return normalized;
        }

        private List<ChatMessage> SelectRelevantChatHistory(string currentUserMessage)
        {
            var allMessages = _chatMessages
                .Where(m => string.Equals(m.Role, "user", StringComparison.OrdinalIgnoreCase)
                         || string.Equals(m.Role, "assistant", StringComparison.OrdinalIgnoreCase))
                .Where(m => !string.Equals(m.Role, "assistant", StringComparison.OrdinalIgnoreCase)
                         || !string.IsNullOrWhiteSpace(m.Content))
                .Select(CloneMessageForInferenceContext)
                .ToList();

            if (allMessages.Count > 0
                && string.Equals(allMessages[^1].Role, "user", StringComparison.OrdinalIgnoreCase)
                && string.Equals(allMessages[^1].Content?.Trim(), currentUserMessage?.Trim(), StringComparison.Ordinal))
            {
                allMessages.RemoveAt(allMessages.Count - 1);
            }

            if (ShouldStartFreshContextWindow(currentUserMessage, allMessages))
            {
                _ = BackendLogService.LogEventAsync(
                    "ContextSelection",
                    $"Selected:0\nDiscarded:{allMessages.Count}\nReason:TopicShiftReset\nPrompt:{currentUserMessage}");
                return new List<ChatMessage>();
            }

            int totalTokens = allMessages.Sum(m => Math.Max(0, (m.Content?.Length ?? 0) / 4));
            if (totalTokens <= ContextSelectionThresholdTokens)
                return allMessages;

            var userIndices = new List<int>();
            for (int i = 0; i < allMessages.Count; i++)
            {
                if (string.Equals(allMessages[i].Role, "user", StringComparison.OrdinalIgnoreCase))
                    userIndices.Add(i);
            }

            if (userIndices.Count <= 1)
                return allMessages;

            HashSet<string> currentWords = ExtractSignificantWords(currentUserMessage);
            var candidates = new List<ChatTurnContextCandidate>();
            for (int i = 0; i < userIndices.Count - 1; i++)
            {
                int startIndex = userIndices[i];
                int endIndex = (i + 1 < userIndices.Count) ? userIndices[i + 1] - 1 : allMessages.Count - 1;
                HashSet<string> turnWords = ExtractSignificantWords(allMessages[startIndex].Content);
                candidates.Add(new ChatTurnContextCandidate
                {
                    UserMessageIndex = i,
                    StartIndex = startIndex,
                    EndIndex = endIndex,
                    Score = ComputeWordOverlapRatio(currentWords, turnWords)
                });
            }

            int firstThirdCount = Math.Max(1, candidates.Count / 3);
            ChatTurnContextCandidate? anchor = candidates
                .Take(firstThirdCount)
                .OrderByDescending(c => c.Score)
                .ThenBy(c => c.StartIndex)
                .FirstOrDefault();

            if (anchor == null || anchor.Score <= 0)
                anchor = candidates.FirstOrDefault();

            var selectedRanges = new HashSet<int>();
            void IncludeRange(ChatTurnContextCandidate? candidate)
            {
                if (candidate == null)
                    return;

                for (int i = candidate.StartIndex; i <= candidate.EndIndex; i++)
                    selectedRanges.Add(i);
            }

            IncludeRange(anchor);

            int recentUserCount = Math.Min(2, userIndices.Count);
            for (int i = userIndices.Count - recentUserCount; i < userIndices.Count; i++)
            {
                if (i < 0)
                    continue;

                int startIndex = userIndices[i];
                int endIndex = i + 1 < userIndices.Count ? userIndices[i + 1] - 1 : allMessages.Count - 1;
                for (int msgIndex = startIndex; msgIndex <= endIndex; msgIndex++)
                    selectedRanges.Add(msgIndex);
            }

            int contextBudget = Math.Max(ContextSelectionThresholdTokens, (int)(CtxSlider?.Value ?? 2048));
            int selectedTokens = selectedRanges.Sum(i => Math.Max(0, (allMessages[i].Content?.Length ?? 0) / 4));
            foreach (var candidate in candidates
                .Where(c => anchor == null || c.StartIndex != anchor.StartIndex)
                .OrderByDescending(c => c.Score)
                .ThenByDescending(c => c.StartIndex))
            {
                bool alreadyIncluded = Enumerable.Range(candidate.StartIndex, candidate.EndIndex - candidate.StartIndex + 1)
                    .All(selectedRanges.Contains);
                if (alreadyIncluded)
                    continue;

                int candidateTokens = 0;
                for (int i = candidate.StartIndex; i <= candidate.EndIndex; i++)
                    candidateTokens += Math.Max(0, (allMessages[i].Content?.Length ?? 0) / 4);

                if (selectedTokens + candidateTokens > contextBudget && selectedRanges.Count > 0)
                    continue;

                for (int i = candidate.StartIndex; i <= candidate.EndIndex; i++)
                    selectedRanges.Add(i);
                selectedTokens += candidateTokens;
            }

            var selected = allMessages
                .Where((m, index) => selectedRanges.Contains(index))
                .ToList();

            _ = BackendLogService.LogEventAsync(
                "ContextSelection",
                $"Selected:{selected.Count}\nDiscarded:{allMessages.Count - selected.Count}\nAnchorTurn:{(anchor?.UserMessageIndex ?? 0) + 1}\nAnchorMessageIndex:{anchor?.StartIndex ?? 0}");

            return selected;
        }

        private List<ChatMessage> SelectRelevantChatHistory(string currentUserMessage, IReadOnlyList<ChatMessage> sourceMessages, int contextBudget, IReadOnlyCollection<ChatDocumentAttachment> chatDocuments)
        {
            var allMessages = (sourceMessages ?? [])
                .Where(m => string.Equals(m.Role, "user", StringComparison.OrdinalIgnoreCase)
                         || string.Equals(m.Role, "assistant", StringComparison.OrdinalIgnoreCase))
                .Where(m => !string.Equals(m.Role, "assistant", StringComparison.OrdinalIgnoreCase)
                         || !string.IsNullOrWhiteSpace(m.Content))
                .Select(CloneMessageForInferenceContext)
                .ToList();

            if (allMessages.Count > 0
                && string.Equals(allMessages[^1].Role, "user", StringComparison.OrdinalIgnoreCase)
                && string.Equals(allMessages[^1].Content?.Trim(), currentUserMessage?.Trim(), StringComparison.Ordinal))
            {
                allMessages.RemoveAt(allMessages.Count - 1);
            }

            bool shouldStartFresh = false;
            if (allMessages.Count >= 4 && !string.IsNullOrWhiteSpace(currentUserMessage))
            {
                if (!IsLikelyConversationFollowUp(currentUserMessage)
                    && !ShouldInjectPersistentDocumentContext(currentUserMessage, chatDocuments, sourceMessages)
                    && !ContainsExplicitWebSearchRequest(currentUserMessage))
                {
                    HashSet<string> currentWords = ExtractSignificantWords(currentUserMessage);
                    if (currentWords.Count >= 3)
                    {
                        List<string> recentUserMessages = allMessages
                            .Where(m => string.Equals(m.Role, "user", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(m.Content))
                            .TakeLast(3)
                            .Select(m => m.Content)
                            .ToList();

                        if (recentUserMessages.Count > 0)
                        {
                            double maxRecentOverlap = recentUserMessages
                                .Select(message => ComputeWordOverlapRatio(currentWords, ExtractSignificantWords(message)))
                                .DefaultIfEmpty(0)
                                .Max();

                            shouldStartFresh = maxRecentOverlap < 0.08 && ScoreQueryComplexity(currentUserMessage) <= 6;
                        }
                    }
                }
            }

            if (shouldStartFresh)
            {
                _ = BackendLogService.LogEventAsync(
                    "ContextSelection",
                    $"Selected:0\nDiscarded:{allMessages.Count}\nReason:TopicShiftReset\nPrompt:{currentUserMessage}");
                return new List<ChatMessage>();
            }

            // Scale the keep-everything threshold with the context window: a fixed 1500-token
            // gate throws away history that an 8k+ context could easily hold.
            int keepAllThreshold = Math.Max(ContextSelectionThresholdTokens, (int)(contextBudget * 0.35));
            int totalTokens = allMessages.Sum(m => Math.Max(0, (m.Content?.Length ?? 0) / 4));
            if (totalTokens <= keepAllThreshold)
                return allMessages;

            var userIndices = new List<int>();
            for (int i = 0; i < allMessages.Count; i++)
            {
                if (string.Equals(allMessages[i].Role, "user", StringComparison.OrdinalIgnoreCase))
                    userIndices.Add(i);
            }

            if (userIndices.Count <= 1)
                return allMessages;

            HashSet<string> currentWordsForSelection = ExtractSignificantWords(currentUserMessage);
            var candidates = new List<ChatTurnContextCandidate>();
            for (int i = 0; i < userIndices.Count - 1; i++)
            {
                int startIndex = userIndices[i];
                int endIndex = (i + 1 < userIndices.Count) ? userIndices[i + 1] - 1 : allMessages.Count - 1;
                HashSet<string> turnWords = ExtractSignificantWords(allMessages[startIndex].Content);
                candidates.Add(new ChatTurnContextCandidate
                {
                    UserMessageIndex = i,
                    StartIndex = startIndex,
                    EndIndex = endIndex,
                    Score = ComputeWordOverlapRatio(currentWordsForSelection, turnWords)
                });
            }

            int firstThirdCount = Math.Max(1, candidates.Count / 3);
            ChatTurnContextCandidate? anchor = candidates
                .Take(firstThirdCount)
                .OrderByDescending(c => c.Score)
                .ThenBy(c => c.StartIndex)
                .FirstOrDefault();

            if (anchor == null || anchor.Score <= 0)
                anchor = candidates.FirstOrDefault();

            var selectedRanges = new HashSet<int>();
            void IncludeRange(ChatTurnContextCandidate? candidate)
            {
                if (candidate == null)
                    return;

                for (int i = candidate.StartIndex; i <= candidate.EndIndex; i++)
                    selectedRanges.Add(i);
            }

            IncludeRange(anchor);

            int recentUserCount = Math.Min(2, userIndices.Count);
            for (int i = userIndices.Count - recentUserCount; i < userIndices.Count; i++)
            {
                if (i < 0)
                    continue;

                int startIndex = userIndices[i];
                int endIndex = i + 1 < userIndices.Count ? userIndices[i + 1] - 1 : allMessages.Count - 1;
                for (int msgIndex = startIndex; msgIndex <= endIndex; msgIndex++)
                    selectedRanges.Add(msgIndex);
            }

            int selectedTokens = selectedRanges.Sum(i => Math.Max(0, (allMessages[i].Content?.Length ?? 0) / 4));
            foreach (var candidate in candidates
                .Where(c => anchor == null || c.StartIndex != anchor.StartIndex)
                .OrderByDescending(c => c.Score)
                .ThenByDescending(c => c.StartIndex))
            {
                bool alreadyIncluded = Enumerable.Range(candidate.StartIndex, candidate.EndIndex - candidate.StartIndex + 1)
                    .All(selectedRanges.Contains);
                if (alreadyIncluded)
                    continue;

                int candidateTokens = 0;
                for (int i = candidate.StartIndex; i <= candidate.EndIndex; i++)
                    candidateTokens += Math.Max(0, (allMessages[i].Content?.Length ?? 0) / 4);

                if (selectedTokens + candidateTokens > contextBudget && selectedRanges.Count > 0)
                    continue;

                for (int i = candidate.StartIndex; i <= candidate.EndIndex; i++)
                    selectedRanges.Add(i);
                selectedTokens += candidateTokens;
            }

            var selected = allMessages
                .Where((m, index) => selectedRanges.Contains(index))
                .ToList();

            _ = BackendLogService.LogEventAsync(
                "ContextSelection",
                $"Selected:{selected.Count}\nDiscarded:{allMessages.Count - selected.Count}\nAnchorTurn:{(anchor?.UserMessageIndex ?? 0) + 1}\nAnchorMessageIndex:{anchor?.StartIndex ?? 0}");

            return selected;
        }

        private async Task RebuildChatSessionFromSelectedMessagesAsync(IEnumerable<ChatMessage> selectedMessages, string systemPrompt, CancellationToken token)
        {
            if (_executor == null || _useGemma4LocalCliMode)
                return;

            await Task.Run(async () =>
            {
                await RebuildChatSessionWithPromptAsync(systemPrompt, token).ConfigureAwait(false);
                if (_chatSession == null)
                    return;

                List<ChatMessage> selectedList = NormalizeMessagesForChatSession(selectedMessages);
                string latestUserMessage = selectedList.LastOrDefault(m => string.Equals(m.Role, "user", StringComparison.OrdinalIgnoreCase))?.Content ?? string.Empty;
                IReadOnlyList<PromptInjectionBlockInfo> injectionInfos = CollectPromptInjectionInfos(selectedList);

                // Replaying history decodes every message — hold the decode gate so this
                // cannot overlap a council or chat inference running on another surface.
                await InferenceBackendService.RunScopedExclusiveAsync(InferenceBackendService.NormalChatScope, async () =>
                {
                    bool lastAcceptedWasUser = false;
                    foreach (var msg in selectedList)
                    {
                        token.ThrowIfCancellationRequested();
                        string reducedContent = ApplyPreInferenceContextReduction(msg.Content ?? string.Empty, injectionInfos, latestUserMessage);

                        if (string.Equals(msg.Role, "user", StringComparison.OrdinalIgnoreCase))
                        {
                            if (!string.IsNullOrWhiteSpace(reducedContent))
                            {
                                await _chatSession.AddAndProcessUserMessage(reducedContent, token).ConfigureAwait(false);
                                lastAcceptedWasUser = true;
                            }
                        }
                        else if (string.Equals(msg.Role, "assistant", StringComparison.OrdinalIgnoreCase))
                        {
                            if (lastAcceptedWasUser && !string.IsNullOrWhiteSpace(reducedContent))
                            {
                                await _chatSession.AddAndProcessAssistantMessage(reducedContent, token).ConfigureAwait(false);
                                lastAcceptedWasUser = false;
                            }
                        }
                    }
                }).ConfigureAwait(false);
            }, token).ConfigureAwait(false);
        }

        private async Task<int> StreamInferenceAsync(Func<IAsyncEnumerable<string>> streamFactory, StringBuilder responseBuilder, bool thinkingModeEnabled, CancellationToken token)
        {
            return await InferenceBackendService.RunScopedExclusiveAsync(InferenceBackendService.NormalChatScope, async () =>
                await Task.Run(async () =>
                {
                    var localBatch = new StringBuilder();
                    var displayedContentBuilder = new StringBuilder();
                    int localBatchTokenCount = 0;
                    int localTokenCount = 0;
                    bool suppressThinkLeak = !thinkingModeEnabled && IsQwen3Model(_modelName);
                    long lastUiFlushAt = Environment.TickCount64;

                    await foreach (var tokenPiece in streamFactory().WithCancellation(token))
                    {
                        responseBuilder.Append(tokenPiece);
                        localTokenCount++;
                        localBatch.Append(tokenPiece);
                        localBatchTokenCount++;

                        bool shouldFlushToUi = localBatchTokenCount >= StreamFlushTokenThreshold
                            || (Environment.TickCount64 - lastUiFlushAt) >= StreamUiFlushIntervalMs;

                        if (shouldFlushToUi)
                        {
                            string flush = localBatch.ToString();
                            localBatch.Clear();
                            localBatchTokenCount = 0;

                            if (!thinkingModeEnabled && !string.IsNullOrEmpty(flush))
                            {
                                if (!suppressThinkLeak)
                                    displayedContentBuilder.Append(flush);

                                string uiContent = suppressThinkLeak
                                    ? StripThinkArtifactsForStreaming(CleanOutputTokens(responseBuilder.ToString()))
                                    : displayedContentBuilder.ToString();

                                await ApplyStreamingMessageContentAsync(uiContent).ConfigureAwait(false);
                                lastUiFlushAt = Environment.TickCount64;
                            }
                        }
                    }

                    if (localBatch.Length > 0 && !thinkingModeEnabled)
                    {
                        string remaining = localBatch.ToString();
                        if (!suppressThinkLeak)
                            displayedContentBuilder.Append(remaining);

                        string uiContent = suppressThinkLeak
                            ? StripThinkArtifactsForStreaming(CleanOutputTokens(responseBuilder.ToString()))
                            : displayedContentBuilder.ToString();

                        await ApplyStreamingMessageContentAsync(uiContent).ConfigureAwait(false);
                    }

                    return localTokenCount;
                }, token).ConfigureAwait(false));
        }

        private async Task ApplyStreamingMessageContentAsync(string content)
        {
            await Dispatcher.InvokeAsync(() =>
            {
                if (_currentStreamingMessage != null)
                {
                    _currentStreamingMessage.SetStreamingContent(content);
                    ScrollChatToEnd();
                }
            }, System.Windows.Threading.DispatcherPriority.Background);
        }

        private async Task<int> ExecuteNormalChatInferenceAsync(
            List<ChatMessage> selectedHistoryMessages,
            List<PromptInjectionBlockInfo> historyInjectionInfos,
            bool useIsolatedWebTurn,
            string effectiveSystemPrompt,
            string userMsg,
            string modelUserMsg,
            string personaContext,
            InferenceParams inferenceParams,
            StringBuilder responseBuilder,
            bool thinkingModeEnabled,
            CancellationToken token)
        {
            bool isGemma4 = IsGemma4Model(_modelName);
            bool isStrictChatMl = IsStrictChatMlModel(_modelName);

            // Strict-ChatML and Gemma4 models render the full transcript into a single prompt
            // and reset the executor context below — replaying history into the chat session
            // first would decode every message twice for no benefit.
            ChatSessionPromptPlan? sessionPlan = null;
            if (_chatSession != null && !_useGemma4LocalCliMode && !useIsolatedWebTurn && !isStrictChatMl && !isGemma4)
            {
                string plannedUserText = string.IsNullOrWhiteSpace(personaContext)
                    ? modelUserMsg
                    : "[USER CONTEXT]\n" + personaContext + "\n[/USER CONTEXT]\n\n" + modelUserMsg;

                sessionPlan = FitChatSessionPlanToContextWindow(
                    effectiveSystemPrompt,
                    selectedHistoryMessages,
                    plannedUserText,
                    inferenceParams);

                NativeDecodeForensics.BeginDecode(
                    "NormalChat.SessionHistoryReplay",
                    sessionPlan.EstimatedPromptTokens,
                    GetLoadedLocalContextSize(),
                    _modelName);
                try
                {
                    await RebuildChatSessionFromSelectedMessagesAsync(sessionPlan.HistoryMessages, sessionPlan.SystemPrompt, token);
                }
                finally
                {
                    NativeDecodeForensics.EndDecode();
                }
            }

            if ((isStrictChatMl || isGemma4) && !_useGemma4LocalCliMode)
            {
                await ResetExecutorContextAsync(token).ConfigureAwait(false);
            }

            if (useIsolatedWebTurn)
            {
                return await StreamIsolatedSingleTurnInferenceAsync(
                    effectiveSystemPrompt,
                    modelUserMsg,
                    inferenceParams,
                    responseBuilder,
                    thinkingModeEnabled,
                    token);
            }

            if (_useGemma4LocalCliMode)
            {
                var historyTurns = BuildGemma4HistoryTurns(selectedHistoryMessages);
                if (historyTurns.Count > 0 && string.Equals(historyTurns[^1].Role, "user", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(historyTurns[^1].Content, userMsg, StringComparison.Ordinal))
                {
                    historyTurns.RemoveAt(historyTurns.Count - 1);
                }

                string prompt = Gemma4Formatter.BuildPrompt(effectiveSystemPrompt, historyTurns, modelUserMsg, false);
                string response = await LocalGemmaCliRunner.InferAsync(
                    _gemma4ModelPath,
                    prompt,
                    2048,
                    (float)TemperatureSlider.Value,
                    (float)TopPSlider.Value,
                    new[] { Gemma4Formatter.TurnClose, "<|/channel|>" },
                    token);

                responseBuilder.Append(response);
                await Dispatcher.InvokeAsync(() =>
                {
                    if (_currentStreamingMessage != null)
                    {
                        _currentStreamingMessage.Content = StripThinkArtifactsForStreaming(CleanOutputTokens(responseBuilder.ToString()));
                        ScrollChatToEnd();
                    }
                });

                return Math.Max(1, response.Length / 4);
            }

            if (isStrictChatMl || isGemma4)
            {
                int queuedImages = AttachPendingImageEmbedsToExecutor();
                string visionUserMsg = PrependImageMarkers(modelUserMsg, queuedImages);

                List<(string Role, string Content)> promptHistoryTurns = isGemma4
                    ? BuildGemma4HistoryTurns(selectedHistoryMessages)
                    : BuildChatMlHistoryTurns(selectedHistoryMessages);
                if (promptHistoryTurns.Count > 0 && string.Equals(promptHistoryTurns[^1].Role, "user", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(promptHistoryTurns[^1].Content, userMsg, StringComparison.Ordinal))
                {
                    promptHistoryTurns.RemoveAt(promptHistoryTurns.Count - 1);
                }

                // Fit-guard the rendered prompt against the real context window: an oversized
                // decode aborts llama.cpp natively and crashes the whole app.
                string prompt = FitLocalPromptToContextWindow(effectiveSystemPrompt, promptHistoryTurns, visionUserMsg, isGemma4, inferenceParams);

                prompt = ApplyPreInferenceContextReduction(prompt, historyInjectionInfos, userMsg);

                int strictPromptTokens = CountLocalPromptTokens(prompt);
                EnsureLocalPromptFitsOrThrow(strictPromptTokens, "NormalChat.StrictPrompt");
                NativeDecodeForensics.BeginDecode("NormalChat.StrictPrompt", strictPromptTokens, GetLoadedLocalContextSize(), _modelName);
                try
                {
                    return await StreamInferenceAsync(
                        () => _executor.InferAsync(prompt, inferenceParams, token),
                        responseBuilder,
                        thinkingModeEnabled,
                        token);
                }
                finally
                {
                    NativeDecodeForensics.EndDecode();
                }
            }

            int sessionQueuedImages = AttachPendingImageEmbedsToExecutor();
            string sessionUserText = sessionPlan?.UserMessage
                ?? (!useIsolatedWebTurn && !string.IsNullOrWhiteSpace(personaContext)
                    ? "[USER CONTEXT]\n" + personaContext + "\n[/USER CONTEXT]\n\n" + modelUserMsg
                    : modelUserMsg);
            var message = new ChatHistory.Message(AuthorRole.User, PrependImageMarkers(sessionUserText, sessionQueuedImages));

            if (_chatSession != null && _chatSession.History.Messages.Count > 0)
            {
                var lastIndex = _chatSession.History.Messages.Count - 1;
                var lastMessage = _chatSession.History.Messages[lastIndex];
                string reduced = ApplyPreInferenceContextReduction(lastMessage.Content ?? string.Empty, historyInjectionInfos, userMsg);
                if (!string.Equals(reduced, lastMessage.Content ?? string.Empty, StringComparison.Ordinal))
                    _chatSession.History.Messages[lastIndex] = new ChatHistory.Message(lastMessage.AuthorRole, reduced);
            }

            InferenceParams sessionParams = sessionPlan?.InferenceParams ?? inferenceParams;
            int sessionPromptTokens = sessionPlan?.EstimatedPromptTokens ?? CountLocalPromptTokens(message.Content);
            EnsureLocalPromptFitsOrThrow(sessionPromptTokens, "NormalChat.SessionTurn");
            NativeDecodeForensics.BeginDecode("NormalChat.SessionTurn", sessionPromptTokens, GetLoadedLocalContextSize(), _modelName);
            try
            {
                return await StreamInferenceAsync(
                    () => _chatSession.ChatAsync(message, sessionParams, token),
                    responseBuilder,
                    thinkingModeEnabled,
                    token);
            }
            finally
            {
                NativeDecodeForensics.EndDecode();
            }
        }

        private async Task<(ReasoningParser.ParsedResponse Parsed, string Answer, bool EmptyAfterStrip)> FinalizeAssistantResponseAsync(
            string rawOutput,
            bool thinkingModeEnabled,
            bool isGemma4Model,
            bool calculatorUsed,
            SandboxPreparation sandboxPreparation,
            string userMsg,
            CancellationToken token,
            bool replaceCorrectedCodeBlocks = false)
        {
            string cleanedOutput = CleanOutputTokens(rawOutput);
            string strippedOutput = StripThinkBlocksAndLeadingBlankLines(cleanedOutput);
            string parsingSource = thinkingModeEnabled ? cleanedOutput : strippedOutput;
            bool shouldRepairPythonCode = sandboxPreparation.IsEligible
                || IsPythonCodingRequest(userMsg)
                || cleanedOutput.Contains("```python", StringComparison.OrdinalIgnoreCase);

            var parsed = thinkingModeEnabled && !IsQwen3Model(_modelName)
                ? ReasoningParser.ParseFinalAnswerDelimited(parsingSource)
                : ReasoningParser.Parse(parsingSource, isGemma4Model);

            string sanitizedAnswer = StripThinkBlocksAndLeadingBlankLines(SanitizeAssistantOutput(parsed.Answer, calculatorUsed));
            if (shouldRepairPythonCode && !string.IsNullOrWhiteSpace(sanitizedAnswer))
            {
                sanitizedAnswer = await AppendPythonExecutionResultsToAssistantMessageAsync(
                    sanitizedAnswer,
                    sandboxPreparation.PythonPreamble,
                    userMsg,
                    token,
                    true,
                    replaceCorrectedCodeBlocks || shouldRepairPythonCode,
                    sandboxPreparation.IsEligible);
                sanitizedAnswer = StripThinkBlocksAndLeadingBlankLines(sanitizedAnswer);
            }

            return (parsed, sanitizedAnswer, string.IsNullOrWhiteSpace(sanitizedAnswer));
        }

        private static InferenceParams CloneInferenceParams(InferenceParams source, int? maxTokens = null)
        {
            var clone = new InferenceParams
            {
                MaxTokens = maxTokens ?? source.MaxTokens,
                AntiPrompts = source.AntiPrompts?.ToList() ?? new List<string>(),
                SamplingPipeline = source.SamplingPipeline
            };
            return clone;
        }

        private async Task<string> ExecuteHighComplexityThinkingPhaseAsync(
            IReadOnlyList<ChatMessage> selectedHistoryMessages,
            string systemPrompt,
            string modelUserMsg,
            InferenceParams baseInferenceParams,
            CancellationToken token)
        {
            string thinkingSystemPrompt = (systemPrompt ?? string.Empty).TrimEnd()
                + "\n\n[THINKING PHASE] Think through the problem carefully and produce reasoning notes only. Do not provide the final answer yet.";
            thinkingSystemPrompt = BuildQwen3SystemPrompt(thinkingSystemPrompt, true);
            var cappedParams = CloneInferenceParams(baseInferenceParams, 400);
            var builder = new StringBuilder();

            if (_useGemma4LocalCliMode)
            {
                var historyTurns = BuildGemma4HistoryTurns(selectedHistoryMessages);
                string prompt = Gemma4Formatter.BuildPrompt(thinkingSystemPrompt, historyTurns, modelUserMsg, true);
                prompt = ApplyPreInferenceContextReduction(prompt, CollectPromptInjectionInfos(selectedHistoryMessages), modelUserMsg);
                string raw = await LocalGemmaCliRunner.InferAsync(
                    _gemma4ModelPath,
                    prompt,
                    400,
                    0.2f,
                    0.05f,
                    new[] { Gemma4Formatter.TurnClose, "<|/channel|>" },
                    token);
                var parsed = ReasoningParser.Parse(CleanOutputTokens(raw), true);
                return string.IsNullOrWhiteSpace(parsed.ThinkingContent)
                    ? parsed.Answer?.Trim() ?? string.Empty
                    : parsed.ThinkingContent.Trim();
            }

            if (_model == null || _activeModelParams == null)
                return string.Empty;

            using var tempContext = await Task.Run(() => LlamaContextFactory.CreateContext(_model, _activeModelParams), token).ConfigureAwait(false);
            var tempExecutor = new InteractiveExecutor(tempContext);
            bool isGemma4 = IsGemma4Model(_modelName);
            bool isStrictChatMl = IsStrictChatMlModel(_modelName);

            if (isGemma4 || isStrictChatMl)
            {
                string prompt = isGemma4
                    ? Gemma4Formatter.BuildPrompt(thinkingSystemPrompt, BuildGemma4HistoryTurns(selectedHistoryMessages), modelUserMsg, true)
                    : BuildStrictChatMlPrompt(thinkingSystemPrompt, BuildChatMlHistoryTurns(selectedHistoryMessages), modelUserMsg);
                prompt = ApplyPreInferenceContextReduction(prompt, CollectPromptInjectionInfos(selectedHistoryMessages), modelUserMsg);

                // The thinking phase is optional — skip it rather than risk an oversized
                // native decode (which aborts the process) or fail the whole turn.
                int thinkingPromptTokens = CountLocalPromptTokens(prompt);
                if (thinkingPromptTokens > GetLoadedLocalContextSize() - 192)
                {
                    _ = BackendLogService.LogEventAsync(
                        "LocalDecodeRefused",
                        $"Stage:NormalChat.ThinkingPhase\nPromptTokens:{thinkingPromptTokens}\nCtx:{GetLoadedLocalContextSize()}\nModel:{_modelName}");
                    return string.Empty;
                }

                NativeDecodeForensics.BeginDecode("NormalChat.ThinkingPhase", thinkingPromptTokens, GetLoadedLocalContextSize(), _modelName);
                try
                {
                    await InferenceBackendService.RunScopedExclusiveAsync(InferenceBackendService.NormalChatScope, () => Task.Run(async () =>
                    {
                        await foreach (var tokenPiece in tempExecutor.InferAsync(prompt, cappedParams, token))
                            builder.Append(tokenPiece);
                    }, token));
                }
                finally
                {
                    NativeDecodeForensics.EndDecode();
                }

                var parsed = ReasoningParser.Parse(CleanOutputTokens(builder.ToString()), isGemma4);
                return string.IsNullOrWhiteSpace(parsed.ThinkingContent)
                    ? parsed.Answer?.Trim() ?? string.Empty
                    : parsed.ThinkingContent.Trim();
            }

            // This session decodes system prompt + replayed history + user turn natively —
            // fit the whole plan to the context window first (oversized decodes abort the
            // process), mirroring the main chat-session path.
            ChatSessionPromptPlan thinkingPlan = FitChatSessionPlanToContextWindow(
                thinkingSystemPrompt,
                selectedHistoryMessages,
                modelUserMsg,
                cappedParams);

            var tempSession = new LLama.ChatSession(tempExecutor);
            tempSession.WithHistoryTransform(new PromptTemplateTransformer(_model, withAssistant: true));
            tempSession.AddSystemMessage(thinkingPlan.SystemPrompt);

            string reducedUserPrompt = ApplyPreInferenceContextReduction(thinkingPlan.UserMessage, CollectPromptInjectionInfos(selectedHistoryMessages), modelUserMsg);

            // History priming decodes the whole prompt — keep it inside the decode gate
            // and off the UI thread together with the final generation pass.
            NativeDecodeForensics.BeginDecode("NormalChat.ThinkingSession", thinkingPlan.EstimatedPromptTokens, GetLoadedLocalContextSize(), _modelName);
            try
            {
                await InferenceBackendService.RunScopedExclusiveAsync(InferenceBackendService.NormalChatScope, () => Task.Run(async () =>
                {
                    bool lastAcceptedWasUser = false;
                    foreach (var msg in thinkingPlan.HistoryMessages)
                    {
                        token.ThrowIfCancellationRequested();
                        if (string.Equals(msg.Role, "user", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(msg.Content))
                        {
                            await tempSession.AddAndProcessUserMessage(msg.Content, token).ConfigureAwait(false);
                            lastAcceptedWasUser = true;
                        }
                        else if (string.Equals(msg.Role, "assistant", StringComparison.OrdinalIgnoreCase) && lastAcceptedWasUser && !string.IsNullOrWhiteSpace(msg.Content))
                        {
                            await tempSession.AddAndProcessAssistantMessage(msg.Content, token).ConfigureAwait(false);
                            lastAcceptedWasUser = false;
                        }
                    }

                    await foreach (var tokenPiece in tempSession.ChatAsync(new ChatHistory.Message(AuthorRole.User, reducedUserPrompt), thinkingPlan.InferenceParams, token))
                        builder.Append(tokenPiece);
                }, token));
            }
            finally
            {
                NativeDecodeForensics.EndDecode();
            }

            return CleanOutputTokens(builder.ToString()).Trim();
        }

        private List<(string Role, string Content)> BuildGemma4HistoryTurns(IEnumerable<ChatMessage> selectedMessages)
        {
            var turns = new List<(string Role, string Content)>();
            foreach (var msg in selectedMessages)
            {
                if (string.Equals(msg.Role, "user", StringComparison.OrdinalIgnoreCase))
                {
                    if (!string.IsNullOrWhiteSpace(msg.Content))
                        turns.Add(("user", msg.Content));
                    continue;
                }

                if (string.Equals(msg.Role, "assistant", StringComparison.OrdinalIgnoreCase))
                {
                    string cleanedAssistant = SanitizeAssistantContentForInference(msg.Content);
                    cleanedAssistant = ReasoningParser.Parse(cleanedAssistant, true).Answer;
                    if (!string.IsNullOrWhiteSpace(cleanedAssistant)
                        && turns.Count > 0
                        && string.Equals(turns[^1].Role, "user", StringComparison.OrdinalIgnoreCase))
                        turns.Add(("model", cleanedAssistant));
                }
            }

            return turns;
        }

        private void NormalThinkingToggle_Click(object sender, RoutedEventArgs e)
        {
            _normalThinkingModeEnabled = !_normalThinkingModeEnabled;
            RefreshNormalThinkingToggleUi();

            ShowTransientStatus(_normalThinkingModeEnabled
                ? "Thinking mode enabled for normal chat."
                : "Thinking mode disabled for normal chat.");
        }

        private async Task<string> GenerateSingleTurnResponseAsync(string systemPrompt, string userPrompt, int maxTokens, CancellationToken token)
        {
            bool isGemma4 = IsGemma4Model(_modelName);
            var antiPrompts = new List<string> { "<|im_end|>", "<|endoftext|>", "<|im_start|>", "<|eot_id|>", "<|start_header_id|>" };
            if (isGemma4)
            {
                antiPrompts.Add(Gemma4Formatter.TurnClose);
                antiPrompts.Add("<|/channel|>");
            }

            systemPrompt = BuildQwen3SystemPrompt(systemPrompt, false);
            var inferenceParams = IsQwen3Model(_modelName)
                ? ModelInferenceProfiles.CreateQwen3InferenceParams(false, maxTokens, antiPrompts)
                : new InferenceParams
                {
                    MaxTokens = maxTokens,
                    AntiPrompts = antiPrompts,
                    SamplingPipeline = new DefaultSamplingPipeline
                    {
                        Temperature = 0.2f,
                        MinP = 0.05f,
                        TopP = 1.0f,
                        TopK = -1,
                        RepeatPenalty = 1.05f
                    }
                };

            if (_useGemma4LocalCliMode)
            {
                string prompt = Gemma4Formatter.BuildPrompt(systemPrompt, new List<(string Role, string Content)>(), userPrompt + BuildPriorComputationResultsBlock(), false);
                return await LocalGemmaCliRunner.InferAsync(_gemma4ModelPath, prompt, maxTokens, 0.2f, 0.05f, new[] { Gemma4Formatter.TurnClose, "<|/channel|>" }, token);
            }

            if (_model == null || _activeModelParams == null)
                return string.Empty;

            var builder = new StringBuilder();
            using var tempContext = await Task.Run(() => LlamaContextFactory.CreateContext(_model, _activeModelParams), token).ConfigureAwait(false);
            var tempExecutor = new InteractiveExecutor(tempContext);

            if (isGemma4 || IsStrictChatMlModel(_modelName))
            {
                string prompt = isGemma4
                    ? Gemma4Formatter.BuildPrompt(systemPrompt, new List<(string Role, string Content)>(), userPrompt + BuildPriorComputationResultsBlock(), false)
                    : BuildStrictChatMlPrompt(systemPrompt, userPrompt + BuildPriorComputationResultsBlock());

                await InferenceBackendService.RunScopedExclusiveAsync(InferenceBackendService.NormalChatScope, () => Task.Run(async () =>
                {
                    await foreach (var tokenPiece in tempExecutor.InferAsync(prompt, inferenceParams, token))
                        builder.Append(tokenPiece);
                }, token));
            }
            else
            {
                var tempSession = new LLama.ChatSession(tempExecutor);
                tempSession.WithHistoryTransform(new PromptTemplateTransformer(_model, withAssistant: true));
                tempSession.AddSystemMessage(systemPrompt);
                await InferenceBackendService.RunScopedExclusiveAsync(InferenceBackendService.NormalChatScope, () => Task.Run(async () =>
                {
                    await foreach (var tokenPiece in tempSession.ChatAsync(new ChatHistory.Message(AuthorRole.User, userPrompt + BuildPriorComputationResultsBlock()), inferenceParams, token))
                        builder.Append(tokenPiece);
                }, token));
            }

            return CleanOutputTokens(builder.ToString()).Trim();
        }

        private static string CleanOutputTokens(string raw)
        {
            return raw
                .Replace("<|im_end|>", "", StringComparison.OrdinalIgnoreCase)
                .Replace("<|im_start|>", "", StringComparison.OrdinalIgnoreCase)
                .Replace("<|endoftext|>", "", StringComparison.OrdinalIgnoreCase)
                .Replace("<|eot_id|>", "", StringComparison.OrdinalIgnoreCase)
                .Replace("<|start_header_id|>", "", StringComparison.OrdinalIgnoreCase)
                .Replace("<|end_header_id|>", "", StringComparison.OrdinalIgnoreCase)
                .Replace("<|begin_of_text|>", "", StringComparison.OrdinalIgnoreCase)
                .Replace("<|turn>", "", StringComparison.OrdinalIgnoreCase)
                .Replace("<turn|>", "", StringComparison.OrdinalIgnoreCase)
                .Trim();
        }

        private async Task ResetExecutorContextAsync(CancellationToken token = default)
        {
            if (_model == null || _activeModelParams == null)
                return;

            try
            {
                // Hold the native-decode gate: disposing the context while another surface
                // is mid-decode (e.g. New Chat clicked during generation) crashes llama.cpp
                // natively with no managed exception. The gate serializes the reset behind
                // any in-flight stream. No caller holds this gate when invoking the reset.
                await InferenceBackendService.RunScopedExclusiveAsync(InferenceBackendService.NormalChatScope, async () =>
                {
                    // Dispose old context first to free VRAM before the new allocation.
                    var oldExecutor = _executor;
                    _executor = null;
                    try { oldExecutor?.Context?.Dispose(); } catch { }

                    var newContext = await Task.Run(() => LlamaContextFactory.CreateContext(_model, _activeModelParams), token).ConfigureAwait(false);
                    _executor = CreateLocalExecutor(newContext);
                }).ConfigureAwait(false);

                // RebuildChatSession reads SystemPromptBox (a WPF control); after
                // ConfigureAwait(false) we are on a threadpool thread, and a cross-thread
                // read throws — previously swallowed below, leaving _chatSession bound to
                // the disposed context. Marshal back to the dispatcher explicitly.
                await Dispatcher.InvokeAsync(RebuildChatSession);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"ResetExecutorContext error: {ex.Message}");
                await BackendLogService.LogErrorAsync("MainWindow.ResetExecutorContext", ex);
            }
        }

        private static string SanitizeAssistantOutput(string output, bool calculatorUsed)
        {
            if (string.IsNullOrWhiteSpace(output))
                return string.Empty;

            string sanitized = output;
            sanitized = Regex.Replace(sanitized,
                @"\[\[CALCULATOR TOOL RESULTS\]\][\s\S]*?\[\[END CALCULATOR TOOL RESULTS\]\]",
                string.Empty,
                RegexOptions.IgnoreCase);
            sanitized = Regex.Replace(sanitized,
                @"\[PAUSE:\s*PYTHON_MATH\s*\|[\s\S]*?\]",
                string.Empty,
                RegexOptions.IgnoreCase);

            if (calculatorUsed)
            {
                sanitized = Regex.Replace(sanitized,
                    @"```python[\s\S]*?```",
                    string.Empty,
                    RegexOptions.IgnoreCase);
            }

            return sanitized.Trim();
        }

        private void InitializeProcessingModeAsync_Shim() { }

        private async Task InitializeProcessingModeAsync()
        {
            try
            {
                _detectedHardware = await Task.Run(() => HardwareProfiler.Capture());
                string recommendation = InferenceBackendService.GetRecommendedModeLabel(_detectedHardware);
                string savedMode = _database?.GetSetting("processing_mode") ?? "";

                bool preferGpu;
                if (!string.IsNullOrEmpty(savedMode))
                {
                    // Honor saved preference only when the hardware can actually support it
                    preferGpu = savedMode == "gpu"
                        && _detectedHardware.HasNvidiaGpu
                        && NativeBackendInit.GpuConfigured;
                }
                else
                {
                    preferGpu = recommendation.StartsWith("GPU", StringComparison.OrdinalIgnoreCase);
                }

                Dispatcher.Invoke(() =>
                {
                    if (ProcessingRecommendationBlock != null)
                    {
                        ProcessingRecommendationBlock.Text = $"Recommended: {recommendation} | GPU: {_detectedHardware.PrimaryGpuName}";
                    }

                    if (ProcessingModeCombo != null)
                    {
                        ProcessingModeCombo.SelectedIndex = preferGpu ? 1 : 0;
                    }
                });

                InferenceBackendService.CurrentMode = preferGpu
                    ? InferenceComputeMode.GpuAccelerated
                    : InferenceComputeMode.CpuOnly;
            }
            catch (Exception ex)
            {
                await BackendLogService.LogErrorAsync("MainWindow.HardwareProbe", ex);
                Dispatcher.Invoke(() =>
                {
                    if (ProcessingRecommendationBlock != null)
                    {
                        ProcessingRecommendationBlock.Text = "Recommended: CPU Only (hardware probe failed)";
                    }

                    if (ProcessingModeCombo != null)
                    {
                        ProcessingModeCombo.SelectedIndex = 0;
                    }
                });

                InferenceBackendService.CurrentMode = InferenceComputeMode.CpuOnly;
            }
        }

        private void ProcessingModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // Ignore programmatic syncs (SyncProcessingModeComboTo) — the model is already being
            // loaded in that mode, so a second reload here would be redundant and could race it.
            if (_suppressProcessingModeComboReload)
                return;

            if (ProcessingModeCombo?.SelectedIndex == 1)
            {
                InferenceBackendService.CurrentMode = InferenceComputeMode.GpuAccelerated;
                _database?.SaveSetting("processing_mode", "gpu");
                ShowTransientStatus("Processing mode set to GPU Accelerated.");
            }
            else
            {
                InferenceBackendService.CurrentMode = InferenceComputeMode.CpuOnly;
                _database?.SaveSetting("processing_mode", "cpu");
                ShowTransientStatus("Processing mode set to CPU Only.");
            }

            _ = ReloadModelWithCurrentModeAsync();
        }

        private async Task ReloadModelWithCurrentModeAsync()
        {
            string? modelPath = _database?.GetUserFact("last_model_path");
            if (string.IsNullOrWhiteSpace(modelPath) || !File.Exists(modelPath) || _model == null)
                return;

            // An explicit switch to GPU is the user deliberately retrying — clear any prior
            // crash strike so the planner/guard doesn't immediately bounce them back to CPU.
            // Clear the council role models too: their strikes (ResolveCouncilCrashRecovery)
            // never auto-clear, so this explicit retry is their only reset path.
            if (InferenceBackendService.CurrentMode == InferenceComputeMode.GpuAccelerated)
            {
                NativeCrashLedger.RegisterCleanRun(modelPath);
                WorkplaceViewControl?.ClearCouncilGpuStrikes();
            }

            string modeName = InferenceBackendService.CurrentMode == InferenceComputeMode.GpuAccelerated
                ? "GPU Accelerated"
                : "CPU Only";

            try
            {
                ShowTransientStatus($"Reloading model for {modeName} mode...");
                UpdateUIState(false);

                DisposeInferenceResources(clearModel: true);

                // Free any VRAM held by cached council models before planning, otherwise
                // the planner sees that memory as gone and resolves to few/zero GPU layers.
                WorkplaceViewControl?.ReleaseCachedCouncilModels();

                uint requestedContext = GetDefaultContextForModel(modelPath);
                var plan = await Task.Run(() => InferenceBackendService.CreatePlan(modelPath, requestedContext, InferenceBackendService.CurrentMode));
                ShowTransientStatus($"Context: {plan.Parameters.ContextSize} tokens | Backend: {plan.BackendName} | GPU Layers: {plan.Parameters.GpuLayerCount} | {plan.Reason}");

                try
                {
                    await InitializeModelSessionAsync(plan);
                }
                catch (Exception ex) when (plan.UsingGpu)
                {
                    await BackendLogService.LogErrorAsync("MainWindow.GPUReload", ex);
                    ShowTransientStatus($"GPU init failed ({ex.Message}). Falling back to CPU...");
                    InferenceBackendService.CurrentMode = InferenceComputeMode.CpuOnly;
                    _database?.SaveSetting("processing_mode", "cpu");
                    Dispatcher.Invoke(() => { if (ProcessingModeCombo != null) ProcessingModeCombo.SelectedIndex = 0; });
                    var cpuPlan = await Task.Run(() => InferenceBackendService.CreatePlan(modelPath, requestedContext, InferenceComputeMode.CpuOnly));
                    await InitializeModelSessionAsync(cpuPlan);
                    ShowTransientStatus("GPU unavailable - running on CPU.");
                }

                UpdateHeaderDisplay();
                ShowTransientStatus($"Model ready ({plan.BackendName})");
                UpdateUIState(true);
            }
            catch (Exception ex)
            {
                await BackendLogService.LogErrorAsync("MainWindow.ModeSwitch", ex);
                AppendSystemMessage($"Reload failed: {ex.Message}");
                UpdateUIState(false);
            }
        }

        // Frees the Normal-Chat model so a LOCAL council run can load its own role models. On a
        // single GPU both subsystems cannot keep an 8B-class model resident at once: the council
        // would load a SECOND copy on top of the chat model, overflow VRAM (plan drops to CPU),
        // then overflow RAM on the CPU copy — the "Failed to load model" relay error. Invoked via
        // WorkplaceView.ReleaseHostChatModelAsync at the start of every local relay/study run.
        private async Task ReleaseChatModelForCouncilAsync(CancellationToken token)
        {
            // Gemma-4 CLI mode keeps no in-process weights, and there's nothing to free if no
            // model is loaded.
            if (_useGemma4LocalCliMode || (_model == null && _executor == null && _chatSession == null))
                return;

            try
            {
                // Dispose under the native-decode gate so we never tear the context down while a
                // chat decode is in flight. Marshal the actual disposal to the UI thread because
                // the chat fields are owned there.
                await InferenceBackendService.RunScopedExclusiveAsync(InferenceBackendService.NormalChatScope, async () =>
                {
                    await Dispatcher.InvokeAsync(() => DisposeInferenceResources(clearModel: true));
                }).ConfigureAwait(false);

                _chatModelReleasedForCouncil = true;
                await BackendLogService.LogEventAsync(
                    "ChatModelReleasedForCouncil",
                    "Freed the Normal-Chat model so the local council can load its role models on the shared GPU.");
            }
            catch (Exception ex)
            {
                await BackendLogService.LogErrorAsync("MainWindow.ReleaseChatModelForCouncil", ex);
            }
        }

        // Reloads the Normal-Chat model after a local council run freed it (see
        // ReleaseChatModelForCouncilAsync). Called when the user returns to the chat view.
        private async Task RestoreChatModelAfterCouncilAsync()
        {
            if (!_chatModelReleasedForCouncil)
                return;

            _chatModelReleasedForCouncil = false;

            // Nothing to restore if the model is somehow already back, or chat is in a mode that
            // doesn't hold in-process weights.
            if (_model != null || _cloudModeActive || _useGemma4LocalCliMode)
                return;

            string? modelPath = _database?.GetUserFact("last_model_path");
            if (string.IsNullOrWhiteSpace(modelPath) || !File.Exists(modelPath))
                return;

            try
            {
                UpdateUIState(false);
                ShowTransientStatus("Reloading chat model...");

                // Hand the GPU back to the chat surface: drop any council weights still cached.
                WorkplaceViewControl?.ReleaseCachedCouncilModels();

                uint requestedContext = GetDefaultContextForModel(modelPath);
                var recovery = ResolveCrashRecoveryPlan(modelPath, InferenceBackendService.CurrentMode, requestedContext);
                var plan = await Task.Run(() => InferenceBackendService.CreatePlan(modelPath, recovery.Context, recovery.Mode));
                await InitializeModelSessionAsync(plan);

                UpdateHeaderDisplay();
                ShowTransientStatus($"Chat model ready ({plan.BackendName}).");
                UpdateUIState(true);
            }
            catch (Exception ex)
            {
                await BackendLogService.LogErrorAsync("MainWindow.RestoreChatModel", ex);
                AppendSystemMessage($"Couldn't reload the chat model automatically ({ex.Message}). Re-import it with the model button.");
                UpdateUIState(false);
            }
        }

        private async Task<NormalChatRequestContext> PrepareNormalChatRequestContextAsync(string userMsg, CancellationToken token)
        {
            NormalChatUiSnapshot uiSnapshot = await CaptureNormalChatUiSnapshotAsync(userMsg);

            ThinkingGateDecision thinkingGate = EvaluateThinkingGate(userMsg, true);
            await BackendLogService.LogEventAsync("ThinkingGate", $"Score:{thinkingGate.Score}\nDecision:{thinkingGate.Decision}\nToggle:Automatic\nPrompt:{userMsg}");

            SandboxPreparation sandboxPreparation = PrepareSandboxContext(userMsg);
            string personaContext = await _personaMemoryService.GetRelevantContextAsync(userMsg, 150, 200);
            string webContext = await TryBuildWebContextAsync(userMsg, false, token, uiSnapshot.ChatMessages);

            return await Task.Run(() =>
            {
                bool thinkingModeEnabled = thinkingGate.UseThinking;
                bool hasWebContext = !string.IsNullOrWhiteSpace(webContext);
                string effectiveSystemPrompt = string.IsNullOrWhiteSpace(personaContext)
                    ? uiSnapshot.SystemPromptText
                    : (uiSnapshot.SystemPromptText + "\n\n[USER CONTEXT]\n" + personaContext + "\n[/USER CONTEXT]");

                if (!string.IsNullOrWhiteSpace(uiSnapshot.HippocampusContext))
                    effectiveSystemPrompt += "\n\n[FROM PRIOR RESEARCH SESSIONS]\n" + uiSnapshot.HippocampusContext + "\n[/FROM PRIOR RESEARCH SESSIONS]";

                if (!string.IsNullOrWhiteSpace(uiSnapshot.AttachedDocumentMemory))
                    effectiveSystemPrompt += "\n\n" + uiSnapshot.AttachedDocumentMemory;

                if (sandboxPreparation.IsEligible && !string.IsNullOrWhiteSpace(sandboxPreparation.SystemPromptInjection))
                    effectiveSystemPrompt += "\n\n" + sandboxPreparation.SystemPromptInjection;

                effectiveSystemPrompt = AppendSingleTurnSystemTail(effectiveSystemPrompt, webContext, thinkingModeEnabled);

                effectiveSystemPrompt = AppendSystemInstruction(effectiveSystemPrompt, LocalMathLatexInstruction);
                // NOTE: the attached-document CONTENT is deliberately NOT appended to the system
                // prompt here. Several local chat templates (Gemma has no system role at all)
                // silently drop system content, which made the model insist the file was missing.
                // The document rides in the user turn below — the one channel every template renders.
                effectiveSystemPrompt = BuildQwen3SystemPrompt(effectiveSystemPrompt, thinkingModeEnabled);

                List<ChatMessage> selectedHistoryMessages = SelectRelevantChatHistory(userMsg, uiSnapshot.ChatMessages, uiSnapshot.ContextSize, uiSnapshot.ChatDocuments);
                List<PromptInjectionBlockInfo> historyInjectionInfos = CollectPromptInjectionInfos(selectedHistoryMessages);
                bool useIsolatedWebTurn = hasWebContext;
                if (hasWebContext)
                {
                    selectedHistoryMessages = selectedHistoryMessages
                        .Where(m => string.Equals(m.Role, "user", StringComparison.OrdinalIgnoreCase))
                        .ToList();
                    historyInjectionInfos.Clear();
                }

                bool isGemma4Model = IsGemma4Model(uiSnapshot.ModelName);
                // Cross-family stop tokens. The executor stops on the model's native EOG token,
                // but a mismatched template can leave a model generating past its turn end —
                // off-distribution gibberish that fills context and can end in a native abort.
                // Listing every family's turn-terminator is a cheap, harmless safety net.
                var antiPrompts = new List<string>
                {
                    "<|im_end|>", "<|endoftext|>", "<|im_start|>", "<|eot_id|>", "<|start_header_id|>",
                    "<end_of_turn>",          // Gemma 1/2/3
                    "<|end_of_text|>",        // Llama 3 / Granite
                    "<|end_of_role|>",        // Granite role turns
                    "<|endofturn|>"           // misc ChatML variants
                };
                if (isGemma4Model)
                {
                    antiPrompts.Add(Gemma4Formatter.TurnClose);
                    antiPrompts.Add("<|/channel|>");
                }

                bool documentAttached = !string.IsNullOrWhiteSpace(uiSnapshot.DocumentContext);
                int maxGenerationTokens = ComputeLocalMaxGenerationTokens(GetLoadedLocalContextSize(), documentAttached);

                InferenceParams inferenceParams = IsQwen3Model(uiSnapshot.ModelName)
                    ? ModelInferenceProfiles.CreateQwen3InferenceParams(thinkingModeEnabled, maxGenerationTokens, antiPrompts)
                    : CreateGenericInferenceParams(maxGenerationTokens, antiPrompts, uiSnapshot.Temperature, uiSnapshot.MinP);

                string calculatorContext = sandboxPreparation.CalculatorContext;
                string modelUserMsg = userMsg + calculatorContext;
                if (!string.IsNullOrWhiteSpace(sandboxPreparation.PreInferencePythonContext))
                    modelUserMsg += "\n\n" + sandboxPreparation.PreInferencePythonContext;

                if (hasWebContext)
                    modelUserMsg += "\n\n" + BuildWebGroundedUserTurnInstruction();

                // Document content goes in the user turn so it survives every chat template.
                // It is placed BEFORE the question (better recall) and clearly delimited.
                if (documentAttached)
                {
                    modelUserMsg = AttachedDocumentRequiredReferenceInstruction
                        + "\n\n" + uiSnapshot.DocumentContext.Trim()
                        + "\n\n--- End of attached document content ---\n\n"
                        + "Question: " + modelUserMsg;
                }

                return new NormalChatRequestContext
                {
                    SystemPrompt = uiSnapshot.SystemPromptText,
                    ThinkingGate = thinkingGate,
                    SandboxPreparation = sandboxPreparation,
                    PersonaContext = personaContext,
                    WebContext = webContext,
                    HasWebContext = hasWebContext,
                    EffectiveSystemPrompt = effectiveSystemPrompt,
                    DocumentContext = uiSnapshot.DocumentContext,
                    SelectedHistoryMessages = selectedHistoryMessages,
                    HistoryInjectionInfos = historyInjectionInfos,
                    UseIsolatedWebTurn = useIsolatedWebTurn,
                    InferenceParams = inferenceParams,
                    CalculatorContext = calculatorContext,
                    ModelUserMessage = modelUserMsg,
                    IsGemma4Model = isGemma4Model
                };
            }, token).ConfigureAwait(false);
        }

        private async Task<NormalChatUiSnapshot> CaptureNormalChatUiSnapshotAsync(string userMsg, bool isCloudMode = false)
        {
            return await Dispatcher.InvokeAsync(() =>
            {
                return new NormalChatUiSnapshot
                {
                    UserMessage = userMsg,
                    SystemPromptText = SystemPromptBox?.Text ?? string.Empty,
                    ModelName = _modelName,
                    ChatDocumentsHaveTextContent = _chatDocuments.Any(doc => doc.HasTextContent),
                    AttachedDocumentMemory = BuildAttachedDocumentMemoryBlock(),
                    DocumentContext = BuildPersistentDocumentContextBlock(userMsg, isCloudMode),
                    HasVisionAttachmentForCloudTurn = HasVisionAttachmentForCloudTurn(),
                    ContextSize = (int)Math.Max(512, CtxSlider?.Value ?? 2048),
                    Temperature = (float)TemperatureSlider.Value,
                    MinP = (float)TopPSlider.Value,
                    ChatMessages = _chatMessages.Select(CloneMessageForInferenceContext).ToList(),
                    CurrentStreamingMessageId = _currentStreamingMessage?.Id,
                    ChatDocuments = _chatDocuments.Select(d => new ChatDocumentAttachment
                    {
                        Name = d.Name,
                        Content = d.Content,
                        Kind = d.Kind,
                        MimeType = d.MimeType,
                        Base64Data = d.Base64Data,
                        FileSizeBytes = d.FileSizeBytes,
                        ImportedAt = d.ImportedAt
                    }).ToList(),
                    HippocampusContext = BuildHippocampusContextForNormalChat(userMsg)
                };
            }, System.Windows.Threading.DispatcherPriority.Background);
        }

        private string BuildHippocampusContextForNormalChat(string query)
        {
            try
            {
                var entries = WorkplaceViewControl?.QueryHippocampus(query, 3);
                if (entries == null || entries.Count == 0)
                    return string.Empty;
                return SessionHippocampus.BuildPromptContext(entries, 240);
            }
            catch
            {
                return string.Empty;
            }
        }

        private void DisposeInferenceResources(bool clearModel)
        {
            try
            {
                if (_chatSession is IDisposable sessionDisposable)
                    sessionDisposable.Dispose();
            }
            catch { }
            finally
            {
                _chatSession = null;
            }

            try
            {
                _executor?.Context?.Dispose();
            }
            catch { }
            finally
            {
                _executor = null;
            }

            if (clearModel)
            {
                // The projector references the text model, so release it first.
                try
                {
                    _mtmdWeights?.Dispose();
                }
                catch { }
                finally
                {
                    _mtmdWeights = null;
                    _mmprojPath = "";
                }

                try
                {
                    _model?.Dispose();
                }
                catch { }
                finally
                {
                    _model = null;
                    _activeModelParams = null;
                }
            }
        }

        private void SmartCompactionToggle_Changed(object sender, RoutedEventArgs e)
        {
            _compactionEngine.SetEnabled(SmartCompactionToggle.IsChecked == true);
            UpdateTokenUsageIndicator();
        }

        private async Task RunSmartCompactionIfNeededAsync(CancellationToken token)
        {
            if (_cloudModeActive)
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    CompactionHealthGrid.Visibility = Visibility.Collapsed;
                    CompactionStatusLabel.Visibility = Visibility.Collapsed;
                    _compactionEngine.ClearCompactionPending();
                }, System.Windows.Threading.DispatcherPriority.Background);

                if (_compactionEngine.IsEnabled)
                    await RunCloudModeCompactionIfNeededAsync(token);
                return;
            }

            if (!_compactionEngine.IsEnabled)
                return;

            var snapshot = await Dispatcher.InvokeAsync(() => new
            {
                ContextSize = (int)Math.Max(512, CtxSlider?.Value ?? 2048),
                Messages = _chatMessages.Select(CloneMessageForInferenceContext).ToList(),
                CurrentStreamingMessageId = _currentStreamingMessage?.Id,
                PinnedTopics = _pinnedMessages.Select(p => p.Content).ToList()
            }, System.Windows.Threading.DispatcherPriority.Background);

            int contextSize = snapshot.ContextSize;
            int estimatedTokens = snapshot.Messages.Sum(m => Math.Max(0, (m.Content?.Length ?? 0) / 4));

            if (!_compactionEngine.ShouldCompact(estimatedTokens, contextSize, _modelName))
                return;

            // Show subtle indicator
            await Dispatcher.InvokeAsync(() =>
            {
                CompactionStatusLabel.Text = "Optimizing context...";
                CompactionStatusLabel.Visibility = Visibility.Visible;
            }, System.Windows.Threading.DispatcherPriority.Background);

            try
            {
                int tokensBefore = estimatedTokens;

                var compactionResult = await Task.Run(async () =>
                {
                    token.ThrowIfCancellationRequested();

                    var messageList = snapshot.Messages;
                    for (int i = 0; i < messageList.Count; i++)
                    {
                        var msg = messageList[i];
                        msg.Importance = SmartContextCompactionEngine.ClassifyImportance(msg.Role, msg.Content, msg.IsPinned || msg.IsCompactionProtected);

                        if (i == 0 && string.Equals(msg.Role, "user", StringComparison.OrdinalIgnoreCase))
                        {
                            msg.IsCompactionProtected = true;
                            msg.Importance = MessageImportance.High;
                        }
                    }

                    var lowCandidates = new List<(int Index, string Content)>();
                    var highContents = new List<string>();
                    for (int i = 0; i < messageList.Count; i++)
                    {
                        var msg = messageList[i];
                        if (snapshot.CurrentStreamingMessageId.HasValue && msg.Id == snapshot.CurrentStreamingMessageId.Value)
                            continue;
                        if (msg.IsCompactionMarker)
                            continue;

                        if (msg.Importance == MessageImportance.High || msg.IsCompactionProtected || msg.IsPinned)
                        {
                            highContents.Add(msg.Content ?? string.Empty);
                        }
                        else
                        {
                            lowCandidates.Add((i, msg.Content ?? string.Empty));
                        }
                    }

                    if (lowCandidates.Count < 3)
                        return (Summaries: new List<CompactionSummaryEntry>(), IndicesToRemove: new HashSet<int>(), MissingTopics: new List<string>(), TokensAfter: tokensBefore);

                    var groups = SmartContextCompactionEngine.GroupByTopic(lowCandidates);
                    if (groups.Count == 0)
                        return (Summaries: new List<CompactionSummaryEntry>(), IndicesToRemove: new HashSet<int>(), MissingTopics: new List<string>(), TokensAfter: tokensBefore);

                    var summaries = new List<CompactionSummaryEntry>();
                    var indicesToRemove = new HashSet<int>();

                    foreach (var group in groups)
                    {
                        token.ThrowIfCancellationRequested();
                        if (group.Count < 2)
                            continue;

                        var groupMessages = group.Select(idx => messageList[idx].Content ?? string.Empty).ToList();
                        string topicLabel = SmartContextCompactionEngine.GenerateTopicLabel(groupMessages);
                        string summary;

                        if (_chatSession != null && _executor != null)
                        {
                            try
                            {
                                string prompt = SmartContextCompactionEngine.BuildCompressionPrompt(groupMessages);
                                var sb = new StringBuilder();
                                var inferParams = new InferenceParams
                                {
                                    MaxTokens = 256,
                                    AntiPrompts = new List<string> { "<|im_end|>", "<|endoftext|>" },
                                    SamplingPipeline = new DefaultSamplingPipeline { Temperature = 0.3f, MinP = 0.05f }
                                };

                                var tempSession = new LLama.ChatSession(_executor);
                                tempSession.WithHistoryTransform(new PromptTemplateTransformer(_model, withAssistant: true));
                                tempSession.AddSystemMessage("You are a context compressor. Produce concise factual summaries.");

                                var compressMsg = new LLama.Common.ChatHistory.Message(LLama.Common.AuthorRole.User, prompt);
                                await InferenceBackendService.RunScopedExclusiveAsync(InferenceBackendService.NormalChatScope, async () =>
                                {
                                    await foreach (var piece in tempSession.ChatAsync(compressMsg, inferParams, token).ConfigureAwait(false))
                                    {
                                        sb.Append(piece);
                                        if (sb.Length > 1200)
                                            break;
                                    }
                                });

                                summary = sb.ToString().Trim();
                                if (string.IsNullOrWhiteSpace(summary) || summary.Length < 20)
                                    summary = SmartContextCompactionEngine.BuildFallbackSummary(groupMessages);
                            }
                            catch
                            {
                                summary = SmartContextCompactionEngine.BuildFallbackSummary(groupMessages);
                            }
                        }
                        else
                        {
                            summary = SmartContextCompactionEngine.BuildFallbackSummary(groupMessages);
                        }

                        summaries.Add(new CompactionSummaryEntry
                        {
                            TopicLabel = topicLabel,
                            OriginalMessageCount = group.Count,
                            Summary = summary
                        });

                        foreach (int idx in group)
                            indicesToRemove.Add(idx);
                    }

                    if (indicesToRemove.Count == 0)
                        return (Summaries: summaries, IndicesToRemove: indicesToRemove, MissingTopics: new List<string>(), TokensAfter: tokensBefore);

                    var missingTopics = SmartContextCompactionEngine.ValidateCompaction(
                        snapshot.PinnedTopics, new List<string>(), highContents, summaries);

                    int tokensAfter = messageList
                        .Where((_, index) => !indicesToRemove.Contains(index))
                        .Sum(m => Math.Max(0, (m.Content?.Length ?? 0) / 4));

                    tokensAfter += summaries.Sum(s => Math.Max(0, ($"[Compressed: {s.TopicLabel}] {s.Summary}".Length) / 4));
                    tokensAfter += missingTopics.Sum(topic => Math.Max(0, ($"[Prior context note] {topic}".Length) / 4));

                    return (Summaries: summaries, IndicesToRemove: indicesToRemove, MissingTopics: missingTopics, TokensAfter: tokensAfter);
                }, token).ConfigureAwait(false);

                if (compactionResult.IndicesToRemove.Count == 0)
                    return;

                // Apply compaction on UI thread
                await Dispatcher.InvokeAsync(() =>
                {
                    // Find insertion point (after last removed message)
                    int insertAt = compactionResult.IndicesToRemove.Max();

                    // Remove messages in reverse order
                    foreach (int idx in compactionResult.IndicesToRemove.OrderByDescending(i => i))
                    {
                        if (idx < _chatMessages.Count)
                            _chatMessages.RemoveAt(idx);
                    }

                    // Insert compressed summaries as system messages
                    int adjustedInsert = Math.Min(_chatMessages.Count, insertAt - compactionResult.IndicesToRemove.Count(i => i < insertAt));
                    adjustedInsert = Math.Max(0, adjustedInsert);

                    foreach (var entry in compactionResult.Summaries)
                    {
                        var summaryMsg = new ChatMessage("system", $"[Compressed: {entry.TopicLabel}] {entry.Summary}");
                        summaryMsg.Importance = MessageImportance.Low;
                        summaryMsg.IsCompactionProtected = true;
                        if (adjustedInsert <= _chatMessages.Count)
                            _chatMessages.Insert(adjustedInsert++, summaryMsg);
                        else
                            _chatMessages.Add(summaryMsg);
                    }

                    // Add missing topic stubs
                    foreach (var topic in compactionResult.MissingTopics)
                    {
                        var stub = new ChatMessage("system", $"[Prior context note] {topic}");
                        stub.Importance = MessageImportance.High;
                        stub.IsCompactionProtected = true;
                        _chatMessages.Add(stub);
                    }

                    // Insert compaction marker
                    var marker = new ChatMessage("system",
                        $"── Context optimized at this point to maintain conversation coherence ── ({compactionResult.IndicesToRemove.Count} messages → {compactionResult.Summaries.Count} summaries)");
                    marker.IsCompactionMarker = true;
                    marker.CompactionSummaries = compactionResult.Summaries;
                    _chatMessages.Add(marker);

                    UpdateTokenUsageIndicator();
                    ScrollChatToEnd();
                    SaveCurrentChat();
                }, System.Windows.Threading.DispatcherPriority.Background);

                Debug.WriteLine($"Smart Compaction: {tokensBefore} → {compactionResult.TokensAfter} tokens, {compactionResult.IndicesToRemove.Count} messages compressed into {compactionResult.Summaries.Count} summaries.");
            }
            finally
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    CompactionStatusLabel.Visibility = Visibility.Collapsed;
                    _compactionEngine.ClearCompactionPending();
                }, System.Windows.Threading.DispatcherPriority.Background);
            }
        }

        private async Task RunCloudModeCompactionIfNeededAsync(CancellationToken token)
        {
            const int MinMessagesForCloudCompaction = 20;
            const int RecentMessagesToPreserve = 12;

            var snapshot = await Dispatcher.InvokeAsync(() => new
            {
                Messages = _chatMessages.Select(CloneMessageForInferenceContext).ToList(),
                CurrentStreamingMessageId = _currentStreamingMessage?.Id,
                PinnedTopics = _pinnedMessages.Select(p => p.Content).ToList()
            }, System.Windows.Threading.DispatcherPriority.Background);

            if (snapshot.Messages.Count < MinMessagesForCloudCompaction)
                return;

            if (!_openRouterChatService.HasValidKey)
                return;

            // Classify importance for all messages
            for (int i = 0; i < snapshot.Messages.Count; i++)
            {
                var msg = snapshot.Messages[i];
                msg.Importance = SmartContextCompactionEngine.ClassifyImportance(msg.Role, msg.Content, msg.IsPinned || msg.IsCompactionProtected);
                if (i == 0 && string.Equals(msg.Role, "user", StringComparison.OrdinalIgnoreCase))
                {
                    msg.IsCompactionProtected = true;
                    msg.Importance = MessageImportance.High;
                }
            }

            // Only compact messages outside the most recent window
            int cutoffIndex = Math.Max(0, snapshot.Messages.Count - RecentMessagesToPreserve);
            var lowCandidates = new List<(int Index, string Content)>();
            for (int i = 0; i < cutoffIndex; i++)
            {
                var msg = snapshot.Messages[i];
                if (msg.IsCompactionMarker || msg.IsCompactionProtected || msg.IsPinned) continue;
                if (snapshot.CurrentStreamingMessageId.HasValue && msg.Id == snapshot.CurrentStreamingMessageId.Value) continue;
                if (msg.Importance == MessageImportance.Low)
                    lowCandidates.Add((i, msg.Content ?? string.Empty));
            }

            if (lowCandidates.Count < 3) return;

            await Dispatcher.InvokeAsync(() =>
            {
                CompactionStatusLabel.Text = "Optimizing context...";
                CompactionStatusLabel.Visibility = Visibility.Visible;
            }, System.Windows.Threading.DispatcherPriority.Background);

            try
            {
                var groups = SmartContextCompactionEngine.GroupByTopic(lowCandidates);
                if (groups.Count == 0) return;

                var summaries = new List<CompactionSummaryEntry>();
                var indicesToRemove = new HashSet<int>();

                foreach (var group in groups)
                {
                    token.ThrowIfCancellationRequested();
                    if (group.Count < 2) continue;

                    var groupMessages = group.Select(idx => snapshot.Messages[idx].Content ?? string.Empty).ToList();
                    string topicLabel = SmartContextCompactionEngine.GenerateTopicLabel(groupMessages);

                    string summary;
                    try
                    {
                        string compressionPrompt = SmartContextCompactionEngine.BuildCompressionPrompt(groupMessages);
                        OpenRouterChatResponse compactionResponse = await _openRouterChatService.SendConversationAsync(
                            new List<OpenRouterMessage> { new("user", compressionPrompt) },
                            "You are a context compressor. Produce a single concise paragraph. Preserve all key facts, decisions, and code. Output only the summary paragraph — no labels or headings.",
                            false,
                            _selectedOpenRouterModelId,
                            null,
                            token);

                        summary = (compactionResponse.Text ?? string.Empty).Trim();
                        if (string.IsNullOrWhiteSpace(summary) || summary.Length < 20)
                            summary = SmartContextCompactionEngine.BuildFallbackSummary(groupMessages);
                    }
                    catch
                    {
                        summary = SmartContextCompactionEngine.BuildFallbackSummary(groupMessages);
                    }

                    summaries.Add(new CompactionSummaryEntry
                    {
                        TopicLabel = topicLabel,
                        OriginalMessageCount = group.Count,
                        Summary = summary
                    });

                    foreach (int idx in group)
                        indicesToRemove.Add(idx);
                }

                if (indicesToRemove.Count == 0) return;

                await Dispatcher.InvokeAsync(() =>
                {
                    int insertAt = indicesToRemove.Max();
                    foreach (int idx in indicesToRemove.OrderByDescending(i => i))
                    {
                        if (idx < _chatMessages.Count)
                            _chatMessages.RemoveAt(idx);
                    }

                    int adjustedInsert = Math.Max(0, Math.Min(_chatMessages.Count,
                        insertAt - indicesToRemove.Count(i => i < insertAt)));

                    foreach (var entry in summaries)
                    {
                        var summaryMsg = new ChatMessage("system", $"[Compressed: {entry.TopicLabel}] {entry.Summary}");
                        summaryMsg.Importance = MessageImportance.Low;
                        summaryMsg.IsCompactionProtected = true;
                        if (adjustedInsert <= _chatMessages.Count)
                            _chatMessages.Insert(adjustedInsert++, summaryMsg);
                        else
                            _chatMessages.Add(summaryMsg);
                    }

                    var marker = new ChatMessage("system",
                        $"── Cloud context optimized ({indicesToRemove.Count} messages → {summaries.Count} summaries) ──");
                    marker.IsCompactionMarker = true;
                    marker.CompactionSummaries = summaries;
                    _chatMessages.Add(marker);

                    CompactionStatusLabel.Visibility = Visibility.Collapsed;
                    UpdateTokenUsageIndicator();
                    ScrollChatToEnd();
                    SaveCurrentChat();
                }, System.Windows.Threading.DispatcherPriority.Background);

                Debug.WriteLine($"Cloud Compaction: {indicesToRemove.Count} messages → {summaries.Count} summaries.");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Cloud compaction error: {ex.Message}");
                await Dispatcher.InvokeAsync(() =>
                    CompactionStatusLabel.Visibility = Visibility.Collapsed,
                    System.Windows.Threading.DispatcherPriority.Background);
            }
        }

        private string BuildPriorComputationResultsBlock()
        {
            string priorOutput = _pythonExecutionService.GetPersistentStdoutHistoryBlock();
            if (string.IsNullOrWhiteSpace(priorOutput))
                return string.Empty;

            return "\n\n[Prior computation results]\n" + priorOutput.Trim() + "\n[/Prior computation results]";
        }

        private void RefreshNormalThinkingToggleUi()
        {
            _normalThinkingToggleButton ??= FindName("NormalThinkingToggleButton") as Button;

            if (_normalThinkingToggleButton == null)
                return;

            _normalThinkingToggleButton.Visibility = Visibility.Collapsed;
            _normalThinkingToggleButton.Opacity = _normalThinkingModeEnabled ? 1.0 : 0.45;
            _normalThinkingToggleButton.ToolTip = _normalThinkingModeEnabled
                ? "Thinking mode enabled"
                : "Thinking mode disabled";
        }

        private void ThinkingToggle_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button || button.Tag is not ChatMessage message)
                return;

            if (!message.ShouldShowThinkingHeader)
                return;

            message.IsThinkingExpanded = !message.IsThinkingExpanded;
        }

        private void CodeBlockToggle_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button || button.Tag is not ChatMessage message)
                return;

            message.IsCodeBlockCollapsed = !message.IsCodeBlockCollapsed;
        }

        private void CopyCodeBlock_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button || button.Tag is not ChatMessage message || !message.HasCodeBlocks)
                return;

            try
            {
                string combinedCode = string.Join(Environment.NewLine + Environment.NewLine, message.CodeBlocks.Select(block => block.Code));
                if (string.IsNullOrWhiteSpace(combinedCode))
                    return;

                Clipboard.SetText(combinedCode);
                ShowTransientStatus(message.CodeBlocks.Count == 1
                    ? "Copied code block."
                    : $"Copied {message.CodeBlocks.Count} code blocks.");
            }
            catch (Exception ex)
            {
                ShowTransientStatus($"Code copy failed: {ex.Message}");
            }
        }

        private void InferenceSettingsSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            CacheLocalInferenceSettings();
        }

        private void Stop_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_cancellationTokenSource != null && !_cancellationTokenSource.IsCancellationRequested)
                {
                    _cancellationTokenSource.Cancel();
                    StopToolActivityIndicator();
                }
            }
            catch (Exception ex)
            {
                StopToolActivityIndicator();
                AppendSystemMessage($"Error stopping generation: {ex.Message}");
            }
        }

        private void InputBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter || e.Key == Key.Return)
            {
                if ((Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift)
                {
                    e.Handled = false;
                }
                else
                {
                    if (SendButton.IsEnabled)
                    {
                        Send_Click(null, null);
                    }
                    e.Handled = true;
                }
            }
        }

        private void InputBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            UpdateUIState(!_isProcessing);
        }

        private void InputBox_GotFocus(object sender, RoutedEventArgs e)
        {
            AnimateInputContainer(1.01, 1.0);
        }

        private void InputBox_LostFocus(object sender, RoutedEventArgs e)
        {
            AnimateInputContainer(1.0, 1.0);
        }

        private void AnimateSendMicroFeedback()
        {
            var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };

            var pulseOpacity = new DoubleAnimation
            {
                Duration = TimeSpan.FromMilliseconds(160),
                From = 1,
                To = 0.9,
                AutoReverse = true,
                EasingFunction = ease
            };

            var sendScaleX = new DoubleAnimation
            {
                Duration = TimeSpan.FromMilliseconds(160),
                From = 1,
                To = 0.98,
                AutoReverse = true,
                EasingFunction = ease
            };

            var sendScaleY = new DoubleAnimation
            {
                Duration = TimeSpan.FromMilliseconds(160),
                From = 1,
                To = 0.98,
                AutoReverse = true,
                EasingFunction = ease
            };

            SendButton.RenderTransformOrigin = new Point(0.5, 0.5);
            if (SendButton.RenderTransform is not ScaleTransform)
            {
                SendButton.RenderTransform = new ScaleTransform(1, 1);
            }

            SendButton.BeginAnimation(UIElement.OpacityProperty, pulseOpacity);
            ((ScaleTransform)SendButton.RenderTransform).BeginAnimation(ScaleTransform.ScaleXProperty, sendScaleX);
            ((ScaleTransform)SendButton.RenderTransform).BeginAnimation(ScaleTransform.ScaleYProperty, sendScaleY);

            AnimateInputContainer(1.01, 0.96);
        }

        private void UpdateResponseLoadingIndicator(bool isVisible)
        {
            var spinner = FindName("InlineLoadingSpinnerCanvas") as Canvas;
            var indicator = FindName("InlineLoadingIndicator") as Border;
            if (spinner == null || indicator == null)
                return;

            spinner.RenderTransform = _inlineSpinnerRotate;
            indicator.Visibility = isVisible ? Visibility.Visible : Visibility.Collapsed;

            if (isVisible)
            {
                var spin = new DoubleAnimation
                {
                    Duration = TimeSpan.FromMilliseconds(900),
                    From = 0,
                    To = 360,
                    RepeatBehavior = RepeatBehavior.Forever
                };
                _inlineSpinnerRotate.BeginAnimation(RotateTransform.AngleProperty, spin);
            }
            else
            {
                _inlineSpinnerRotate.BeginAnimation(RotateTransform.AngleProperty, null);
            }
        }

        private void AnimateInputContainer(double targetScale, double targetOpacity)
        {
            var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };

            var scaleAnim = new DoubleAnimation
            {
                Duration = TimeSpan.FromMilliseconds(160),
                To = targetScale,
                EasingFunction = ease
            };

            var opacityAnim = new DoubleAnimation
            {
                Duration = TimeSpan.FromMilliseconds(160),
                To = targetOpacity,
                EasingFunction = ease
            };

            if (InputContainerBorder.RenderTransform is ScaleTransform scale)
            {
                scale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleAnim);
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleAnim);
            }

            InputContainerBorder.BeginAnimation(UIElement.OpacityProperty, opacityAnim);
        }

        private void UpdateHeaderDisplay()
        {
            if (this.FindName("HeaderModelName") is TextBlock headerBlock)
                headerBlock.Text = GetActiveModelDisplayName();

            UpdateNormalChatChrome();
        }

        private void UpdateNormalChatChrome()
        {
            if (FindName("ChatHeaderBorder") is Border headerBorder)
            {
                headerBorder.Visibility = _chatMessages.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            }

            if (FindName("InputContainerBorder") is Border inputBorder)
            {
                inputBorder.VerticalAlignment = _chatMessages.Count == 0 ? VerticalAlignment.Center : VerticalAlignment.Bottom;
                inputBorder.Width = _chatMessages.Count == 0 ? 1080 : double.NaN;
                inputBorder.MaxWidth = _chatMessages.Count == 0 ? 1080 : 1220;
                inputBorder.HorizontalAlignment = _chatMessages.Count == 0 ? HorizontalAlignment.Center : HorizontalAlignment.Stretch;
                inputBorder.Margin = _chatMessages.Count == 0
                    ? new Thickness(24, 0, 24, 118)
                    : new Thickness(24, 0, 24, 28);
            }
        }

        private async void Send_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                AnimateSendMicroFeedback();

                if (_isProcessing)
                {
                    AppendSystemMessage("⚠ Already processing. Please wait.");
                    return;
                }

                if (!_cloudModeActive && !_useGemma4LocalCliMode && _chatSession == null)
                {
                    AppendSystemMessage("ERROR: No model loaded. Import a model first.");
                    return;
                }

                if (string.IsNullOrWhiteSpace(InputBox.Text))
                {
                    AppendSystemMessage("ERROR: Input is empty.");
                    return;
                }

                if (_currentChatId < 0)
                    NewChat_Click(null, null);

                if (!_cloudModeActive && !_useGemma4LocalCliMode && _chatSession == null)
                {
                    AppendSystemMessage("ERROR: Failed to initialize chat session. Re-import the model.");
                    return;
                }

                _isProcessing = true;
                UpdateUIState(false);
                UpdateResponseLoadingIndicator(true);

                string userMsg = InputBox.Text.Trim();
                InputBox.Clear();
                _nextMessageModelOverride = "Default";

                // The staged attachments belong to this message now — clear their input chips
                // (they remain in _chatDocuments as context for follow-up questions).
                bool hadPendingAttachments = false;
                foreach (ChatDocumentAttachment doc in _chatDocuments)
                {
                    if (doc.IsPending)
                    {
                        doc.IsPending = false;
                        hadPendingAttachments = true;
                    }
                }
                if (hadPendingAttachments)
                    RefreshAttachmentTray();

                AddChatMessage("user", userMsg);

                // Classify importance for the new user message and run compaction if needed
                if (_chatMessages.Count > 0)
                {
                    var lastMsg = _chatMessages[^1];
                    lastMsg.Importance = SmartContextCompactionEngine.ClassifyImportance(lastMsg.Role, lastMsg.Content, lastMsg.IsPinned);
                    // Auto-pin first user message in conversation
                    if (_chatMessages.Count(m => string.Equals(m.Role, "user", StringComparison.OrdinalIgnoreCase)) == 1)
                    {
                        lastMsg.IsCompactionProtected = true;
                        lastMsg.Importance = MessageImportance.High;
                    }
                }
                _cancellationTokenSource = new CancellationTokenSource();
                await RunSmartCompactionIfNeededAsync(_cancellationTokenSource.Token);

                if (_cloudModeActive)
                {
                    _systemPrompt = SystemPromptBox?.Text ?? "";
                    _tokenCount = 0;
                    _inferenceTimer.Restart();

                    try
                    {
                        await HandleCloudChatRequestAsync(userMsg, _cancellationTokenSource.Token);
                        _inferenceTimer.Stop();
                        SaveChatAdvancedState();
                        SaveCurrentChat();
                    }
                    catch (OperationCanceledException)
                    {
                        _inferenceTimer.Stop();
                        AddChatMessage("system", "Generation stopped by user.");
                        SaveCurrentChat();
                    }
                    catch (Exception ex)
                    {
                        _inferenceTimer.Stop();
                        string trimmed = TrimForInlineError(ex.Message, 150);
                        AddChatMessage("system", $"Error: {trimmed}");
                        _ = ShowNonIntrusiveErrorAsync(trimmed);
                        Debug.WriteLine($"Cloud generation error: {ex}");
                        SaveCurrentChat();
                    }

                    return;
                }

                NormalChatRequestContext requestContext = await PrepareNormalChatRequestContextAsync(userMsg, _cancellationTokenSource.Token);
                _systemPrompt = requestContext.SystemPrompt;
                bool thinkingModeEnabled = requestContext.ThinkingGate.UseThinking;

                _tokenCount = 0;
                _inferenceTimer.Restart();

                _currentStreamingMessage = new ChatMessage("assistant", thinkingModeEnabled ? "Thinking..." : "");
                _currentStreamingMessage.ModelLabel = string.IsNullOrWhiteSpace(_nextMessageModelOverride) || _nextMessageModelOverride == "Default"
                    ? _modelName
                    : _nextMessageModelOverride;
                _currentStreamingMessage.IsThinkingInProgress = thinkingModeEnabled;
                _currentStreamingMessage.IsStreaming = true;
                _chatMessages.Add(_currentStreamingMessage);

                var responseBuilder = new StringBuilder();
                string calculatorContext = requestContext.CalculatorContext;
                var sandboxPreparation = requestContext.SandboxPreparation;

                try
                {
                    if (!string.IsNullOrWhiteSpace(sandboxPreparation.CalculatorSignal))
                    {
                        await AddChatMessageAsync("system", sandboxPreparation.CalculatorSignal);
                    }

                    string modelUserMsg = requestContext.ModelUserMessage;

                    if (sandboxPreparation.IsEligible && !string.IsNullOrWhiteSpace(sandboxPreparation.ExplicitPythonCode))
                    {
                        string preInferencePython = await ExecutePythonWithSingleRetryAsync(
                            sandboxPreparation.ExplicitPythonCode,
                            sandboxPreparation.PythonPreamble,
                            userMsg,
                            _cancellationTokenSource.Token);
                        if (!string.IsNullOrWhiteSpace(preInferencePython))
                            modelUserMsg += "\n\n" + preInferencePython;
                    }

                    if (sandboxPreparation.IsEligible)
                        await _pythonExecutionService.StartPersistentSessionAsync(_cancellationTokenSource.Token);

                    _tokenCount = await ExecuteNormalChatInferenceAsync(
                        requestContext.SelectedHistoryMessages,
                        requestContext.HistoryInjectionInfos,
                        requestContext.UseIsolatedWebTurn,
                        requestContext.EffectiveSystemPrompt,
                        userMsg,
                        modelUserMsg,
                        requestContext.PersonaContext,
                        requestContext.InferenceParams,
                        responseBuilder,
                        thinkingModeEnabled,
                        _cancellationTokenSource.Token);

                    _inferenceTimer.Stop();

                    if (_tokenCount == 0 && responseBuilder.Length == 0)
                    {
                        _chatMessages.Remove(_currentStreamingMessage);
                        _currentStreamingMessage = null;
                        AddChatMessage("system", "⚠ No response generated — the prompt likely exceeded the context window. " +
                            "Try starting a New Chat, removing imported documents, or increasing context length in Settings.");
                        await ResetExecutorContextAsync(_cancellationTokenSource?.Token ?? CancellationToken.None);
                    }
                    else
                    {
                        bool calculatorUsed = !string.IsNullOrWhiteSpace(calculatorContext);
                        var finalizedResponse = await FinalizeAssistantResponseAsync(
                            responseBuilder.ToString(),
                            thinkingModeEnabled,
                            requestContext.IsGemma4Model,
                            calculatorUsed,
                            sandboxPreparation,
                            userMsg,
                            _cancellationTokenSource.Token);

                        if (finalizedResponse.EmptyAfterStrip)
                        {
                            await BackendLogService.LogEventAsync("Qwen3EmptyResponseAfterStrip", $"Retrying after empty response strip. Prompt:{userMsg}");
                            responseBuilder.Clear();
                            if (_currentStreamingMessage != null)
                            {
                                _currentStreamingMessage.Content = string.Empty;
                                ScrollChatToEnd();
                            }

                            _tokenCount = await ExecuteNormalChatInferenceAsync(
                                requestContext.SelectedHistoryMessages,
                                requestContext.HistoryInjectionInfos,
                                requestContext.UseIsolatedWebTurn,
                                requestContext.EffectiveSystemPrompt,
                                userMsg,
                                modelUserMsg,
                                requestContext.PersonaContext,
                                requestContext.InferenceParams,
                                responseBuilder,
                                thinkingModeEnabled,
                                _cancellationTokenSource.Token);

                            _inferenceTimer.Stop();
                            finalizedResponse = await FinalizeAssistantResponseAsync(
                                responseBuilder.ToString(),
                                thinkingModeEnabled,
                                requestContext.IsGemma4Model,
                                calculatorUsed,
                                sandboxPreparation,
                                userMsg,
                                _cancellationTokenSource.Token);
                        }

                        _currentStreamingMessage.FinalizeStreamingContent(finalizedResponse.EmptyAfterStrip
                            ? EmptyStrippedResponseInlineHtml
                            : finalizedResponse.Answer);
                        _currentStreamingMessage.ThinkingContent = finalizedResponse.Parsed.HasThinking ? finalizedResponse.Parsed.ThinkingContent : string.Empty;
                        _currentStreamingMessage.IsStreaming = false;
                        _currentStreamingMessage.ModelLabel = string.IsNullOrWhiteSpace(_nextMessageModelOverride) || _nextMessageModelOverride == "Default"
                            ? _modelName
                            : _nextMessageModelOverride;

                        var activeBranch = _branches.FirstOrDefault(b => b.Id == _activeBranchId);
                        if (activeBranch != null)
                        {
                            activeBranch.Messages.Add(new ChatMessageState
                            {
                                Id = _currentStreamingMessage.Id,
                                Role = _currentStreamingMessage.Role,
                                Content = _currentStreamingMessage.Content,
                                ThinkingContent = _currentStreamingMessage.ThinkingContent,
                            ThinkingHeaderText = _currentStreamingMessage.ThinkingHeaderText,
                                ModelLabel = _currentStreamingMessage.ModelLabel,
                                Timestamp = _currentStreamingMessage.Timestamp
                            });
                        }

                        if (finalizedResponse.Parsed.HasThinking)
                        {
                            Debug.WriteLine("Reasoning content detected in model response.");
                        }

                        double speedMetric = _tokenCount / Math.Max(_inferenceTimer.Elapsed.TotalSeconds, 0.001);
                        ShowTransientStatus($"Tokens: {_tokenCount}  •  Speed: {speedMetric:F2} tok/s");
                        _currentStreamingMessage = null;

                        // NOTE: a clean turn deliberately does NOT clear the crash strike here.
                        // A model that crashed on GPU is now recovered by loading on GPU with a
                        // SMALLER context (ResolveCrashRecoveryPlan); a clean turn at that reduced
                        // context only proves the *reduced* setting works, so auto-clearing would
                        // bounce the next load back to the full context and re-crash (an endless
                        // full↔reduced oscillation). The strike (and thus the safe reduced context)
                        // stays put until the user explicitly re-selects GPU Accelerated in
                        // Settings, which is the deliberate "retry at full settings" signal and
                        // clears it (ReloadModelWithCurrentModeAsync).
                    }
                    _nextMessageModelOverride = "Default";
                    UpdateTokenUsageIndicator();
                    SaveChatAdvancedState();

                    SaveCurrentChat();
                }
                catch (OperationCanceledException)
                {
                    _inferenceTimer.Stop();
                    if (_currentStreamingMessage != null)
                        _currentStreamingMessage.IsStreaming = false;
                    _chatMessages.Remove(_currentStreamingMessage);
                    _currentStreamingMessage = null;
                    await AddChatMessageAsync("system", "Generation stopped by user.");
                    // The turn's token is already cancelled — using it here would abort the
                    // context rebuild and leave the executor in a half-reset state.
                    await ResetExecutorContextAsync(CancellationToken.None);
                    SaveCurrentChat();
                }
                catch (Exception ex)
                {
                    _inferenceTimer.Stop();
                    if (_currentStreamingMessage != null)
                        _currentStreamingMessage.IsStreaming = false;
                    _chatMessages.Remove(_currentStreamingMessage);
                    _currentStreamingMessage = null;
                    await AddChatMessageAsync("system", $"Error: {ex.Message}");
                    await ResetExecutorContextAsync(CancellationToken.None);
                    if (ex is OutOfMemoryException || ex is IOException
                        || (ex.Message != null && ex.Message.Contains("NoKvSlot", StringComparison.OrdinalIgnoreCase)))
                    {
                        _ = ShowNonIntrusiveErrorAsync($"Inference error: {GetMostRelevantError(ex)}");
                    }
                    Debug.WriteLine($"Generation error: {ex}");
                    SaveCurrentChat();
                }
                finally
                {
                    if (sandboxPreparation.IsEligible)
                        await _pythonExecutionService.EndPersistentSessionAsync(CancellationToken.None);
                }
            }

            catch (Exception ex)
            {
                AppendSystemMessage($"CRITICAL ERROR: {ex.Message}");
                Debug.WriteLine($"Critical error: {ex}");
            }
            finally
            {
                UpdateResponseLoadingIndicator(false);
                _isProcessing = false;
                UpdateUIState(true);
                _cancellationTokenSource?.Dispose();
                _cancellationTokenSource = null;
            }

        }
    }
}
