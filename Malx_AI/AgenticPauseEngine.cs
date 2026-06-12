using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
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

        // ── Injected backend callbacks ────────────────────────────────────────
        private readonly SessionHippocampus _hippocampus;
        private readonly Func<string, string, Task<string>> _sandboxExecute;
        private readonly Func<string, CancellationToken, Task<string>> _webSearchExecute;
        private readonly Func<string, CancellationToken, Task<string>> _pythonMathExecute;
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
            Action<string> activityLogger,
            Action<string> onStatusUpdate)
        {
            _hippocampus    = hippocampus ?? throw new ArgumentNullException(nameof(hippocampus));
            _sandboxExecute = sandboxExecute ?? throw new ArgumentNullException(nameof(sandboxExecute));
            _webSearchExecute = webSearchExecute ?? throw new ArgumentNullException(nameof(webSearchExecute));
            _pythonMathExecute = pythonMathExecute ?? throw new ArgumentNullException(nameof(pythonMathExecute));
            _activityLogger = activityLogger ?? (_ => { });
            _onStatusUpdate = onStatusUpdate ?? (_ => { });
        }

        /// <summary>
        /// Resets the per-turn pause counter.  Call once before each council role inference.
        /// </summary>
        public void ResetBudget() => _pauseCount = 0;

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

                    var (fragment, pauseCommand) = await InterceptStreamAsync(
                        stream,
                        fullOutput,
                        delta =>
                        {
                            emittedTokenCount += delta;
                            onTokenCounted(delta);
                        },
                        outerToken,
                        onTextProgress);

                    if (pauseCommand == null)
                    {
                        // Normal completion — clear any lingering pause status
                        _onStatusUpdate(string.Empty);
                        break;
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

                        string overrideMessage =
                            $"[SYSTEM OVERRIDE: The planned action failed with error: {toolResult.ErrorMessage}. Do not repeat your previous logic. Formulate an alternative approach natively.]";

                        if (chatSession != null)
                        {
                            chatSession.History.AddMessage(AuthorRole.System, overrideMessage);
                        }

                        currentPayload = overrideMessage + "\nContinue your response.";
                        _onStatusUpdate($"▶ Resuming generation  ·  {_pauseCount}/{MaxPausesPerTurn} pauses used");
                        continue;
                    }

                    // Success path: inject result and continue
                    string injectedResult = pauseCommand.Tool == ToolWebSearch
                        ? $"[RESULT: Web Search Data: {toolResult.Data}]"
                        : $"[RESULT: {toolResult.Data}]";

                    // Append result to session history as a system observation so the model
                    // reads it as authoritative grounded fact when inference resumes.
                    if (chatSession != null)
                    {
                        chatSession.History.AddMessage(AuthorRole.System, injectedResult);
                    }

                    // Build the continuation payload: what the model sees as the new "user turn"
                    // is just the injection marker so it continues naturally.
                    currentPayload = injectedResult + "\nContinue your response.";

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
            Action<string>? onTextProgress = null)
        {
            var tokenBuffer = new StringBuilder();   // speculative hold-back buffer
            bool inSpeculative = false;              // are we buffering a potential [PAUSE:?
            int lastProgressLength = 0;              // chars already pushed to onTextProgress

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

                if (output.Length + tokenBuffer.Length > 30_000)
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

                if (cmd.Tool == "INVALID_PAUSE")
                    return ToolDispatchResult.Fail(cmd.Query);

                return ToolDispatchResult.Fail($"Unknown tool: {cmd.Tool}");
            }
            catch (Exception ex)
            {
                return ToolDispatchResult.Fail($"Tool error: {ex.Message}");
            }
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

            string result = await _sandboxExecute(code, language);
            if (IsSandboxFailure(result))
                return ToolDispatchResult.Fail(result);

            return ToolDispatchResult.Ok(result);
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
                _               => $"Tool '{cmd.Tool}': {Truncate(cmd.Query, 48)}"
            };
            _onStatusUpdate($"⏸  Agentic Pause {currentPauseCount}/{MaxPausesPerTurn}  ·  {toolLabel}");
        }

        private static string Truncate(string s, int max) =>
            s.Length <= max ? s : s[..max] + "…";

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
    }
}
