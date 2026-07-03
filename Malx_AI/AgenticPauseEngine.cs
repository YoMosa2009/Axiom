using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using LLama;
using LLama.Common;

namespace Malx_AI
{
    /// <summary>
    /// Intercepts the LLaMA.net token stream mid-generation to allow a small-parameter
    /// model to pause, request real data from the C# backend, and seamlessly resume.
    /// Council / Workplace ONLY — never used in normal chat mode.
    /// </summary>
    internal sealed class AgenticPauseEngine
    {
        // ── Pause syntax constants ────────────────────────────────────────────
        private const string PauseOpen   = "[PAUSE:";
        private const string PauseClose  = "]";
        private const int    MaxPausesPerTurn = 3;
        private const string BudgetExceededMsg =
            "[SYSTEM OVERRIDE: Tool budget exceeded. Complete your response natively.]";

        // ── Tool names ────────────────────────────────────────────────────────
        private const string ToolHippocampus = "SEARCH_HIPPOCAMPUS";
        private const string ToolCalculate   = "CALCULATE";
        private const string ToolSandbox     = "RUN_SANDBOX";
        private const string ToolWebSearch   = "WEB_SEARCH";
        private const string ToolPythonMath  = "PYTHON_MATH";
        private const string ToolReadFile    = "READ_FILE";
        private const string ToolSearchCodebase = "SEARCH_CODEBASE";
        private const string ToolListFiles   = "LIST_FILES";

        // ── Injected backend callbacks ────────────────────────────────────────
        private readonly SessionHippocampus _hippocampus;
        private readonly Func<string, string, Task<string>> _sandboxExecute;
        private readonly Func<string, CancellationToken, Task<string>> _webSearchExecute;
        private readonly Func<string, CancellationToken, Task<string>> _pythonMathExecute;
        private readonly Func<string, string, StructuredToolResult> _workspaceReadExecute;
        private readonly Action<string> _activityLogger;

        // ── Live UI status callback (dispatcher-safe, may be null) ────────────
        private readonly Action<string> _onStatusUpdate;

        // ── Pause budget tracker (reset per turn by the caller) ───────────────
        private int _pauseCount;

        public AgenticPauseEngine(
            SessionHippocampus hippocampus,
            Func<string, string, Task<string>> sandboxExecute,
            Func<string, CancellationToken, Task<string>> webSearchExecute,
            Func<string, CancellationToken, Task<string>> pythonMathExecute,
            Func<string, string, StructuredToolResult>? workspaceReadExecute,
            Action<string> activityLogger,
            Action<string> onStatusUpdate)
        {
            _hippocampus    = hippocampus ?? throw new ArgumentNullException(nameof(hippocampus));
            _sandboxExecute = sandboxExecute ?? throw new ArgumentNullException(nameof(sandboxExecute));
            _webSearchExecute = webSearchExecute ?? throw new ArgumentNullException(nameof(webSearchExecute));
            _pythonMathExecute = pythonMathExecute ?? throw new ArgumentNullException(nameof(pythonMathExecute));
            _workspaceReadExecute = workspaceReadExecute ?? ((_, _) => StructuredToolResult.Fail("Codebase read tools are unavailable."));
            _activityLogger = activityLogger ?? (_ => { });
            _onStatusUpdate = onStatusUpdate ?? (_ => { });
        }

        /// <summary>
        /// Resets the per-turn pause counter.  Call once before each council role inference.
        /// </summary>
        public void ResetBudget() => _pauseCount = 0;

        /// <summary>
        /// Executes a tool selected by the Builder's grammar-constrained decision step.
        /// This deliberately reuses the same allowlist and routing callbacks as the legacy
        /// inline pause protocol so the two paths cannot drift into different capabilities.
        /// </summary>
        internal async Task<StructuredToolResult> ExecuteStructuredToolAsync(
            string tool,
            string query,
            CancellationToken token)
        {
            string normalizedTool = (tool ?? string.Empty).Trim().ToUpperInvariant();
            string normalizedQuery = (query ?? string.Empty).Trim();
            if (normalizedQuery.Length == 0)
                return StructuredToolResult.Fail("Tool query is empty.");

            var command = new PauseCommand(normalizedTool, normalizedQuery);
            UpdateStatus(command, 1);
            try
            {
                ToolDispatchResult result = await DispatchToolAsync(command, token);
                return result.IsSuccess
                    ? StructuredToolResult.Ok(result.Data)
                    : StructuredToolResult.Fail(result.ErrorMessage);
            }
            finally
            {
                _onStatusUpdate(string.Empty);
            }
        }

        /// <summary>
        /// Runs the full agentic loop:
        ///   1. Streams tokens from <paramref name="streamFactory"/>, watching for [PAUSE:…].
        ///   2. On pause: executes the requested tool, injects [RESULT: …] into the session,
        ///      and re-invokes inference.
        ///   3. Respects a max-3 pause budget; a 4th attempt gets a hardcoded override.
        /// Returns the fully-accumulated final text (no pause markers or result tags included).
        /// </summary>
        public async Task<string> RunAsync(
            Func<string, IAsyncEnumerable<string>> streamFactory,
            LLama.ChatSession? chatSession,
            InteractiveExecutor? executor,
            InferenceParams inferenceParams,
            string systemPrompt,
            string initialUserPayload,
            Action<int> onTokenCounted,
            CancellationToken outerToken,
            Action<string>? onTextProgress = null)
        {
            ResetBudget();

            // Safety-truncation ceiling for one role turn. Scales with the caller's real token
            // budget: reasoning-tuned models legitimately stream a thinking phase PLUS a full
            // deliverable, and the old flat 30k-char cut truncated valid long outputs mid-file.
            int maxTokens = inferenceParams?.MaxTokens ?? 0;
            int streamCharGuard = Math.Max(30_000, maxTokens * 6);

            string currentPayload = initialUserPayload;
            var fullOutput = new StringBuilder();
            LLamaContext.State? rollbackCheckpoint = null;
            int emittedTokenCount = 0;
            int rollbackCheckpointTokenLength = 0;
            bool budgetOverrideIssued = false;

            try
            {
                while (true)
                {
                    // Get a new stream for this (re-)invocation
                    var stream = streamFactory(currentPayload);
                    int invocationOutputStart = fullOutput.Length;

                    var (_, pauseCommand) = await InterceptStreamAsync(
                        stream,
                        fullOutput,
                        delta =>
                        {
                            emittedTokenCount += delta;
                            onTokenCounted(delta);
                        },
                        outerToken,
                        onTextProgress,
                        streamCharGuard);

                    if (pauseCommand == null)
                    {
                        // Normal completion — clear any lingering pause status
                        _onStatusUpdate(string.Empty);
                        break;
                    }

                    if (pauseCommand.Tool == ToolWebSearch && fullOutput.Length > invocationOutputStart)
                    {
                        // Anything emitted before a WEB_SEARCH pause is speculative: the model has
                        // already admitted it needs live evidence. Remove that partial text so stale
                        // or hallucinated preamble cannot survive beside the grounded continuation.
                        fullOutput.Remove(invocationOutputStart, fullOutput.Length - invocationOutputStart);
                        onTextProgress?.Invoke(fullOutput.ToString());
                    }

                    // Pre-tool rollback snapshot
                    if (executor?.Context != null)
                    {
                        rollbackCheckpoint?.Dispose();
                        rollbackCheckpoint = executor.Context.GetState();
                        rollbackCheckpointTokenLength = emittedTokenCount;
                    }

                    // ── Pause detected ──────────────────────────────────────────
                    _pauseCount++;
                    if (_pauseCount > MaxPausesPerTurn)
                    {
                        // Budget exhausted. Previously this appended the [SYSTEM OVERRIDE...]
                        // marker to the VISIBLE output (leaking pipeline noise into chat/canvas)
                        // and stopped mid-sentence at the pause point. Instead, resume generation
                        // once with the override injected so the model finishes its answer
                        // natively; a further pause attempt after that ends the turn cleanly.
                        if (budgetOverrideIssued)
                        {
                            _activityLogger("Agentic pause budget exhausted again after native-completion override; ending turn with accumulated output.");
                            _onStatusUpdate(string.Empty);
                            break;
                        }

                        budgetOverrideIssued = true;
                        _onStatusUpdate("[!] Tool budget exhausted — completing natively");
                        if (chatSession != null)
                        {
                            chatSession.History.AddMessage(AuthorRole.System, BudgetExceededMsg);
                        }
                        currentPayload = BudgetExceededMsg + "\nContinue and complete your response without any further tool use.";
                        continue;
                    }

                    // Show accurate status now that pause count is confirmed within budget
                    UpdateStatus(pauseCommand, _pauseCount);

                    // Execute the tool
                    ToolDispatchResult toolResult = await DispatchToolAsync(pauseCommand, outerToken);

                    if (!toolResult.IsSuccess)
                    {
                        // Roll back failed tool branch to prevent context poisoning
                        if (executor?.Context != null && rollbackCheckpoint != null)
                        {
                            executor.Context.LoadState(rollbackCheckpoint);
                            emittedTokenCount = rollbackCheckpointTokenLength;
                        }

                        string overrideMessage = pauseCommand.Tool == ToolWebSearch
                            ? $"[SYSTEM OVERRIDE: Web search failed with error: {toolResult.ErrorMessage}. Do not invent current facts, source-backed claims, URLs, dates, release details, prices, policies, or documentation details. If the answer depends on current web evidence, say the web lookup did not return usable evidence and answer only the parts that are already supported by provided context.]"
                            : $"[SYSTEM OVERRIDE: The planned action failed with error: {toolResult.ErrorMessage}. Do not repeat your previous logic. Formulate an alternative approach natively.]";

                        if (chatSession != null)
                        {
                            chatSession.History.AddMessage(AuthorRole.System, overrideMessage);
                        }

                        currentPayload = pauseCommand.Tool == ToolWebSearch
                            ? overrideMessage + "\nDo not continue any unsupported current/source-backed answer. Answer current/source-backed portions only with facts already supported by the prompt, or state that web evidence was unavailable; stable non-current background context is allowed when clearly separate from unsupported current claims."
                            : overrideMessage + "\nContinue your response.";
                        _onStatusUpdate($"▶ Resuming generation  ·  {_pauseCount}/{MaxPausesPerTurn} pauses used");
                        continue;
                    }

                    // Success path: inject result and continue
                    string injectedResult = pauseCommand.Tool == ToolWebSearch
                        ? BuildWebSearchResultInjection(toolResult.Data)
                        : $"[RESULT: {toolResult.Data}]";

                    // Append result to session history as a system observation so the model
                    // reads it as authoritative grounded fact when inference resumes.
                    if (chatSession != null)
                    {
                        chatSession.History.AddMessage(AuthorRole.System, injectedResult);
                    }

                    // Build the continuation payload: what the model sees as the new "user turn"
                    // is just the injection marker so it continues naturally.
                    currentPayload = pauseCommand.Tool == ToolWebSearch
                        ? injectedResult + "\nContinue your response using the web evidence for current/source-backed claims it actually covers. If a needed current/source-backed detail is not confirmed by the evidence, state that gap instead of guessing; stable non-current background context may be used when not contradicted by the evidence."
                        : injectedResult + "\nContinue your response.";

                    // Show resuming status — visible while model generates the continuation
                    _onStatusUpdate($"▶ Resuming generation  ·  {_pauseCount}/{MaxPausesPerTurn} pauses used");
                }
            }
            finally
            {
                rollbackCheckpoint?.Dispose();
            }

            return fullOutput.ToString();
        }

        // ── Stream interception state machine ─────────────────────────────────
        // Buffers tokens while we might be seeing a [PAUSE: sequence.
        // If confirmed: suspends, discards trigger tokens from output, returns the command.
        // If not a pause: flushes buffer to output normally.
        private async Task<(string fragment, PauseCommand? command)> InterceptStreamAsync(
            IAsyncEnumerable<string> stream,
            StringBuilder output,
            Action<int> onTokenCounted,
            CancellationToken token,
            Action<string>? onTextProgress = null,
            int streamCharGuard = 30_000)
        {
            var tokenBuffer = new StringBuilder();   // speculative hold-back buffer
            bool inSpeculative = false;              // are we buffering a potential [PAUSE:?
            int lastProgressLength = 0;              // chars already pushed to onTextProgress
            int lastLoopCheckLength = 0;             // output length at last runaway-loop check

            // Live-progress pump: pushes accumulated visible text to the UI callback at most
            // once per small growth step, so callers can render a streaming card without
            // paying a ToString() per token.
            void PushProgress(bool force = false)
            {
                if (onTextProgress == null)
                    return;
                if (!force && output.Length - lastProgressLength < 48)
                    return;
                lastProgressLength = output.Length;
                onTextProgress(output.ToString());
            }

            // ConfigureAwait(false): per-token continuations must not bounce through the
            // WPF dispatcher — at 30+ tokens/sec that saturates the UI thread and freezes
            // the app during long council generations. RunAsync's own awaits still resume
            // on the captured context, so tool dispatch keeps running on the UI thread.
            await foreach (var piece in stream.WithCancellation(token).ConfigureAwait(false))
            {
                onTokenCounted(1);

                // Runaway-generation guard: a council role can get stuck echoing/looping its own
                // (large) system prompt — environment briefing, capability list, pause rule — and
                // would otherwise stream until the token cap, minutes of garbage the user must
                // manually Stop. Detect a long block repeating in the recent output and end the
                // stream early; the role's contract check then retries or falls back, no hang.
                if (output.Length - lastLoopCheckLength >= 200)
                {
                    lastLoopCheckLength = output.Length;
                    if (LooksLikeRunawayRepetition(output))
                    {
                        _activityLogger("Council role stream stopped early — runaway/echo repetition detected.");
                        break;
                    }
                }

                if (output.Length + tokenBuffer.Length > streamCharGuard)
                {
                    // Safety truncation
                    if (tokenBuffer.Length > 0)
                    {
                        output.Append(tokenBuffer);
                        tokenBuffer.Clear();
                    }
                    break;
                }

                if (!inSpeculative)
                {
                    // Look for the opening [
                    if (piece.Contains('['))
                    {
                        int bracketIdx = piece.IndexOf('[');
                        // Flush everything before the bracket
                        if (bracketIdx > 0)
                            output.Append(piece[..bracketIdx]);

                        // Start buffering from the bracket
                        tokenBuffer.Append(piece[bracketIdx..]);
                        inSpeculative = true;
                    }
                    else
                    {
                        output.Append(piece);
                        PushProgress();
                    }
                }
                else
                {
                    // We're buffering a candidate [PAUSE: sequence
                    tokenBuffer.Append(piece);

                    string buffered = tokenBuffer.ToString();

                    // Check if we definitely have enough chars to know
                    if (buffered.Length >= PauseOpen.Length)
                    {
                        if (buffered.StartsWith(PauseOpen, StringComparison.Ordinal))
                        {
                            // It IS a pause sequence — wait for the closing ]
                            if (buffered.Contains(PauseClose, StringComparison.Ordinal))
                            {
                                // Full [PAUSE: TOOL | query] captured
                                int closeIdx = buffered.LastIndexOf(PauseClose, StringComparison.Ordinal);
                                string inner = buffered.Substring(PauseOpen.Length, closeIdx - PauseOpen.Length);

                                var cmd = ParsePauseCommand(inner);
                                if (cmd != null)
                                {
                                    // A council role frequently ECHOES its own system prompt, whose
                                    // AGENTIC PAUSE RULE lists literal [PAUSE: ...] examples. Running
                                    // those echoed examples as real tool calls and resuming generation
                                    // is a primary amplifier of the Builder "loops forever" bug, so a
                                    // pause that merely repeats a rule example is treated as plain text.
                                    if (IsEchoedRuleExample(cmd))
                                    {
                                        output.Append(buffered);
                                        tokenBuffer.Clear();
                                        inSpeculative = false;
                                        PushProgress();
                                        continue;
                                    }

                                    // Discard trigger tokens from output (token masking).
                                    // Status is updated in RunAsync after budget check.
                                    return (string.Empty, cmd);
                                }

                                // Malformed pause command. Do not leak raw tool code into visible output.
                                return (string.Empty, new PauseCommand("INVALID_PAUSE", "Malformed pause command."));

                                // Unparseable syntax — flush as normal text
                            }
                            // else: still waiting for ] — keep buffering
                        }
                        else
                        {
                            // Not a [PAUSE: — flush buffer as normal text
                            output.Append(buffered);
                            tokenBuffer.Clear();
                            inSpeculative = false;
                            PushProgress();
                        }
                    }
                    // else: buffer too short to decide — keep accumulating
                }
            }

            // Stream ended — flush any remaining buffer
            if (tokenBuffer.Length > 0)
            {
                output.Append(tokenBuffer);
                tokenBuffer.Clear();
            }
            PushProgress(force: true);

            return (string.Empty, null);
        }

        // ── Tool router ───────────────────────────────────────────────────────
        private async Task<ToolDispatchResult> DispatchToolAsync(PauseCommand cmd, CancellationToken token)
        {
            try
            {
                if (cmd.Tool == ToolHippocampus)
                    return RouteHippocampus(cmd.Query);

                if (cmd.Tool == ToolCalculate)
                    return RouteCalculate(cmd.Query);

                if (cmd.Tool == ToolSandbox)
                    return await RouteSandboxAsync(cmd.Query, token);

                if (cmd.Tool == ToolWebSearch)
                    return await RouteWebSearchAsync(cmd.Query, token);

                if (cmd.Tool == ToolPythonMath)
                    return await RoutePythonMathAsync(cmd.Query, token);

                if (cmd.Tool is ToolReadFile or ToolSearchCodebase or ToolListFiles)
                    return RouteWorkspaceRead(cmd.Tool, cmd.Query);

                if (cmd.Tool == "INVALID_PAUSE")
                    return ToolDispatchResult.Fail(cmd.Query);

                return ToolDispatchResult.Fail($"Unknown tool: {cmd.Tool}");
            }
            catch (Exception ex)
            {
                return ToolDispatchResult.Fail($"Tool error: {ex.Message}");
            }
        }

        private static string BuildWebSearchResultInjection(string data)
        {
            return "[RESULT: Web Search Data]\n" +
                "[WEB SEARCH GROUNDING RULE]\n" +
                "Current UTC date: " + DateTime.UtcNow.ToString("yyyy-MM-dd") + ".\n" +
                "Treat the web search data below as authoritative for current, online, source-backed, or recently changed claims that it actually covers.\n" +
                "Use source titles/hosts, URLs, and dates from the data when making current/source-backed factual claims.\n" +
                "Do not add unsupported current/source-backed facts from memory, model training data, prior council output, or guesses.\n" +
                "Do not treat off-topic web results as support for the user's named entities.\n" +
                "Stable non-current background context is allowed when not contradicted by the web evidence.\n" +
                "If the data is partial or mismatched, use the confirmed facts and clearly state what the web data does not confirm.\n" +
                "If sources conflict, report the conflict instead of resolving it by guessing.\n" +
                "[[WEB SEARCH DATA FROM TOOL]]\n" +
                (data ?? string.Empty).Trim() +
                "\n[[END WEB SEARCH DATA FROM TOOL]]\n" +
                "[END RESULT]";
        }

        private ToolDispatchResult RouteHippocampus(string query)
        {
            var entries = _hippocampus.Query(query, 4);
            if (entries.Count == 0)
            {
                _ = BackendLogService.LogEventAsync("ToolFailReadOnly", $"Tool:Hippocampus\nQuery:{query}\nStatus:No relevant knowledge found");
                return ToolDispatchResult.Fail("No relevant knowledge found in session hippocampus.");
            }

            var sb = new StringBuilder();
            foreach (var entry in entries)
                sb.AppendLine(entry.Content.Trim());
            return ToolDispatchResult.Ok(sb.ToString().Trim());
        }

        private static ToolDispatchResult RouteCalculate(string query)
        {
            if (CalculatorToolAgent.TryBuildContext(query, out string contextBlock, out _))
                return ToolDispatchResult.Ok(contextBlock);

            return ToolDispatchResult.Fail("Could not evaluate expression: " + query);
        }

        private async Task<ToolDispatchResult> RouteSandboxAsync(string query, CancellationToken token)
        {
            // Detect language hint in query, e.g. "python: print(1+1)" or just raw code
            string language = "python";
            string code = query;

            int colonIdx = query.IndexOf(':');
            if (colonIdx > 0 && colonIdx < 10)
            {
                string langCandidate = query[..colonIdx].Trim().ToLowerInvariant();
                if (langCandidate is "python" or "java" or "html")
                {
                    language = langCandidate;
                    code = query[(colonIdx + 1)..].Trim();
                }
            }

            // The sandbox is the council's math/equation/formula runner. Models often pass a bare
            // expression ("sqrt(2)+factorial(5)") instead of a full program; normalize it so it
            // actually prints a value and so common math names resolve without an explicit import.
            if (language == "python")
                code = PrepareSandboxMathCode(code);

            string result = await _sandboxExecute(code, language);
            if (IsSandboxFailure(result))
                return ToolDispatchResult.Fail(result);

            return ToolDispatchResult.Ok(result);
        }

        private ToolDispatchResult RouteWorkspaceRead(string tool, string query)
        {
            StructuredToolResult result = _workspaceReadExecute(tool, query);
            return result.IsSuccess
                ? ToolDispatchResult.Ok(result.Data)
                : ToolDispatchResult.Fail(result.ErrorMessage);
        }

        // Math-aware normalization for the Python sandbox. A single bare expression is wrapped in
        // print() with the math namespace imported; code that calls math functions but forgot the
        // import gets a safe star-import prepended. Full programs (assignments, defs, loops, existing
        // prints/imports) are left untouched.
        private static readonly Regex SandboxStatementRegex =
            new(@"\b(import|def|class|for|while|if|elif|else|return|with|try|except|lambda|print|yield|raise|assert)\b",
                RegexOptions.Compiled);
        private static readonly Regex SandboxMathImportRegex =
            new(@"\bimport\s+math\b|\bfrom\s+math\s+import\b", RegexOptions.Compiled);
        private static readonly Regex SandboxMathNameRegex =
            new(@"\b(sqrt|cbrt|sin|cos|tan|asin|acos|atan|sinh|cosh|tanh|log|log2|log10|exp|pi|tau|e|factorial|floor|ceil|pow|gcd|lcm|hypot|degrees|radians|comb|perm|prod)\b",
                RegexOptions.Compiled);

        private static string PrepareSandboxMathCode(string code)
        {
            string trimmed = (code ?? string.Empty).Trim();
            if (trimmed.Length == 0)
                return trimmed;

            // Strip a surrounding markdown code fence so bare-expression detection sees real code.
            if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                int firstNewline = trimmed.IndexOf('\n');
                int lastFence = trimmed.LastIndexOf("```", StringComparison.Ordinal);
                if (firstNewline > 0 && lastFence > firstNewline)
                    trimmed = trimmed[(firstNewline + 1)..lastFence].Trim();
            }

            bool isMultiLine = trimmed.Contains('\n');
            bool hasPrint = trimmed.Contains("print(", StringComparison.Ordinal);
            bool looksLikeStatement = isMultiLine
                || trimmed.Contains('=')
                || SandboxStatementRegex.IsMatch(trimmed);

            if (!hasPrint && !looksLikeStatement)
                return "from math import *\nprint(" + trimmed + ")";

            if (!SandboxMathImportRegex.IsMatch(trimmed) && SandboxMathNameRegex.IsMatch(trimmed))
                return "from math import *\n" + trimmed;

            return trimmed;
        }

        private async Task<ToolDispatchResult> RoutePythonMathAsync(string code, CancellationToken token)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            string result;
            try
            {
                result = await _pythonMathExecute(code, token);
            }
            catch (Exception ex)
            {
                sw.Stop();
                await BackendLogService.LogEventAsync("AgenticPause.PythonMath",
                    $"CODE:\n{code}\nDURATION_MS:{sw.ElapsedMilliseconds}\nERROR:{ex.Message}");
                _activityLogger($"Python Math failed ({sw.ElapsedMilliseconds} ms).");
                return ToolDispatchResult.Fail($"Python math failed: {ex.Message}");
            }

            sw.Stop();
            await BackendLogService.LogEventAsync("AgenticPause.PythonMath",
                $"CODE:\n{code}\nDURATION_MS:{sw.ElapsedMilliseconds}\nRESULT:\n{result}");
            _activityLogger($"Python Math executed ({sw.ElapsedMilliseconds} ms).");

            if (string.IsNullOrWhiteSpace(result))
                return ToolDispatchResult.Fail("Python math produced no output. Use print() to emit final answer.");

            if (result.StartsWith("Python execution failed", StringComparison.OrdinalIgnoreCase)
                || result.StartsWith("Python execution timed out", StringComparison.OrdinalIgnoreCase)
                || result.StartsWith("Python code is empty", StringComparison.OrdinalIgnoreCase)
                || result.StartsWith("Python completed with no printed output", StringComparison.OrdinalIgnoreCase)
                || result.Contains("Traceback", StringComparison.OrdinalIgnoreCase))
            {
                return ToolDispatchResult.Fail(result);
            }

            return ToolDispatchResult.Ok(result);
        }

        private static bool IsSandboxFailure(string result)
        {
            if (string.IsNullOrWhiteSpace(result))
                return true;

            string lower = result.ToLowerInvariant();
            return lower.Contains("compile error")
                   || lower.Contains("compilation failed")
                   || lower.Contains("exception")
                   || lower.Contains("traceback")
                   || lower.Contains("runtime error")
                   || lower.StartsWith("error:")
                   || lower.Contains("sandbox error");
        }

        private async Task<ToolDispatchResult> RouteWebSearchAsync(string query, CancellationToken token)
        {
            string result = await _webSearchExecute(query, token);
            if (string.IsNullOrWhiteSpace(result))
            {
                await BackendLogService.LogEventAsync("ToolFailReadOnly", $"Tool:WebSearch\nQuery:{query}\nStatus:Empty result");
                return ToolDispatchResult.Fail("Web search returned no data.");
            }

            string lower = result.ToLowerInvariant();
            if (lower.Contains("no web results")
                || lower.Contains("web search unavailable")
                || lower.Contains("no usable results")
                || lower.Contains("timed out")
                || lower.StartsWith("error:"))
            {
                await BackendLogService.LogEventAsync("ToolFailReadOnly", $"Tool:WebSearch\nQuery:{query}\nStatus:{result}");
                return ToolDispatchResult.Fail(result);
            }

            return ToolDispatchResult.Ok(result);
        }

        // ── Status helper ─────────────────────────────────────────────────────
        private void UpdateStatus(PauseCommand cmd, int currentPauseCount)
        {
            string toolLabel = cmd.Tool switch
            {
                ToolHippocampus => $"Searching memory — \"{Truncate(cmd.Query, 48)}\"",
                ToolCalculate   => $"Calculating: {Truncate(cmd.Query, 48)}",
                ToolSandbox     => $"Running sandbox: {Truncate(cmd.Query, 48)}",
                ToolWebSearch   => $"Searching the web for '{Truncate(cmd.Query, 48)}'...",
                ToolPythonMath  => "Running embedded Python math...",
                ToolReadFile    => $"Reading file: {Truncate(cmd.Query, 48)}",
                ToolSearchCodebase => $"Searching codebase: {Truncate(cmd.Query, 48)}",
                ToolListFiles   => $"Listing files: {Truncate(cmd.Query, 48)}",
                _               => $"Tool '{cmd.Tool}': {Truncate(cmd.Query, 48)}"
            };
            _onStatusUpdate($"⏸  Agentic Pause {currentPauseCount}/{MaxPausesPerTurn}  ·  {toolLabel}");
        }

        private static string Truncate(string s, int max) =>
            s.Length <= max ? s : s[..max] + "…";

        // The exact example queries baked into AgenticPauseRule. A model echoing its system
        // prompt reproduces these verbatim; running them as real tool calls is never intended.
        private static readonly HashSet<string> RuleExampleQueries = new(StringComparer.OrdinalIgnoreCase)
        {
            "45 * 2",
            "boiling point of water",
            "latest .net 10 release notes",
            "print((42 * 17) / 3)"
        };

        private static bool IsEchoedRuleExample(PauseCommand cmd)
            => RuleExampleQueries.Contains(cmd.Query.Trim());

        // Detects a council role stuck repeating itself — either a tight short-cycle loop
        // (echoing a fragment of its own system prompt) or a whole-answer rewrite (it finishes a
        // response, then starts re-typing it from the top). Both would otherwise stream to the
        // token cap: minutes of garbage the user must Stop, and the relay never reaches the Critic.
        // Thresholds are deliberately conservative so ordinary repetition (lists, boilerplate,
        // code) is not mistaken for a runaway loop.
        private static bool LooksLikeRunawayRepetition(StringBuilder output)
        {
            int len = output.Length;
            if (len < 480)
                return false;

            // ── Tier 1: tight short-cycle loop ──────────────────────────────────
            // A small block echoed several times in close succession.
            const int tightWindow = 120;   // length of the repeating-block probe
            const int tightSpan = 2400;    // how far back to scan
            if (len >= tightWindow * 4)
            {
                int start = Math.Max(0, len - tightSpan);
                string tail = output.ToString(start, len - start);
                if (tail.Length >= tightWindow * 4)
                {
                    string probe = tail[^tightWindow..];
                    int count = 0, idx = 0;
                    while ((idx = tail.IndexOf(probe, idx, StringComparison.Ordinal)) >= 0)
                    {
                        if (++count >= 3)        // last 120 chars recur 3x within ~2.4k → looping
                            return true;
                        idx += tightWindow;      // non-overlapping
                    }
                }
            }

            // ── Tier 2: whole-answer rewrite ────────────────────────────────────
            // The model retypes its output from the top. Each cycle is far larger than the
            // Tier-1 span and may have repeated only once, so Tier 1 never sees 3 hits inside its
            // small window. Signature: a long, mostly-alphabetic trailing block that has already
            // appeared verbatim earlier. A 256-char exact recurrence almost never happens in
            // genuine output, so even a single earlier copy is enough to call it a loop.
            const int blockWindow = 256;
            const int blockSpan = 9000;
            if (len >= blockWindow * 2)
            {
                string probe = output.ToString(len - blockWindow, blockWindow);

                // Skip low-information probes (separator rules, whitespace, fence runs) that can
                // legitimately recur — require the block to be mostly real words.
                int letters = 0;
                for (int i = 0; i < probe.Length; i++)
                    if (char.IsLetter(probe[i])) letters++;

                if (letters >= blockWindow * 3 / 8)
                {
                    int searchStart = Math.Max(0, len - blockSpan);
                    int searchCount = (len - blockWindow) - searchStart;   // region BEFORE the probe
                    if (searchCount > 0 &&
                        output.ToString(searchStart, searchCount).IndexOf(probe, StringComparison.Ordinal) >= 0)
                        return true;
                }
            }

            return false;
        }

        // ── Pause command parser ──────────────────────────────────────────────
        private static PauseCommand? ParsePauseCommand(string inner)
        {
            // inner = " TOOL_NAME | query text "
            int pipeIdx = inner.IndexOf('|');
            if (pipeIdx < 0) return null;

            string tool  = inner[..pipeIdx].Trim().ToUpperInvariant();
            string query = inner[(pipeIdx + 1)..].Trim();

            if (string.IsNullOrWhiteSpace(tool) || string.IsNullOrWhiteSpace(query))
                return null;

            return new PauseCommand(tool, query);
        }

        private sealed record PauseCommand(string Tool, string Query);

        private sealed record ToolDispatchResult(bool IsSuccess, string Data, string ErrorMessage)
        {
            public static ToolDispatchResult Ok(string data) => new(true, data, string.Empty);
            public static ToolDispatchResult Fail(string error) => new(false, string.Empty, error);
        }

        internal sealed record StructuredToolResult(bool IsSuccess, string Data, string ErrorMessage)
        {
            public static StructuredToolResult Ok(string data) => new(true, data, string.Empty);
            public static StructuredToolResult Fail(string error) => new(false, string.Empty, error);
        }
    }
}
