using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using LLama.Sampling;

namespace Malx_AI
{
    public partial class WorkplaceView
    {
        private const int BuilderToolDecisionMaxQueryChars = 4000;
        private const int BuilderToolResultMaxChars = 8000;
        private const int MaxBuilderPreflightTools = 2;

        private sealed record BuilderToolDecision(string Action, string Tool, string Query);

        private async Task<ReasoningParser.ParsedResponse> ExecuteLocalBuilderWithToolDecisionAsync(
            string systemPrompt,
            string userPayload,
            CancellationToken token,
            float? temperatureOverride,
            CouncilBaseStateVault? baseStateVault,
            bool loadBaseState,
            bool allowBatchRecovery,
            bool showLiveCard,
            int? maxGenerationTokensOverride,
            int? contextSizeOverride,
            LocalModelCapabilityProfile? builderCapability = null)
        {
            var toolContext = new StringBuilder();
            var usedToolCalls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var observations = new List<BuilderToolObservation>();

            // Tool-routing trust scales with model size AND recorded behavior. A sub-1B model
            // forced through the decision grammar emits VALID JSON for an arbitrary tool with an
            // invented query, then echoes the observation as its deliverable ("PYTHON_MATH /
            // execution output: ..."), so it never routes tools itself — the deterministic
            // router (regex over the actual request, hallucination-free) is its only preflight
            // source. Compact 1-4B models start with one model-chosen call and can EARN a second
            // when the first validates and succeeds; the reliability ledger further adjusts the
            // budget from this model's actual routing history.
            bool deterministicOnly = builderCapability?.IsSubOneB == true;
            int modelChosenToolBudget = builderCapability?.MaxModelChosenPreflightTools ?? MaxBuilderPreflightTools;
            string builderModelPath = GetEffectiveRoleConfig(CouncilRole.Builder).ModelPath ?? string.Empty;

            if ((deterministicOnly || builderCapability?.IsCompactClass == true)
                && TryBuildDeterministicBuilderToolDecision(userPayload, out BuilderToolDecision? forcedDecision)
                && forcedDecision != null)
            {
                await RunBuilderPreflightToolAsync(forcedDecision, usedToolCalls, toolContext, observations, token);
            }

            if (deterministicOnly)
            {
                LogActivity(usedToolCalls.Count > 0
                    ? "Builder tool routing: deterministic preflight only (sub-1B local model)."
                    : "Builder tool routing: no deterministic match; sub-1B model goes straight to the deliverable.");
            }
            else if (modelChosenToolBudget > 0)
            {
                int ledgerBudget = ToolReliabilityLedger.GetTrustAdjustedToolBudget(builderModelPath, modelChosenToolBudget);
                if (ledgerBudget != modelChosenToolBudget)
                {
                    LogActivity(ledgerBudget == 0
                        ? $"Builder tool routing: ledger stepped this model down to deterministic-only ({ToolReliabilityLedger.DescribeModel(builderModelPath)})."
                        : $"Builder tool routing: ledger granted an extended budget of {ledgerBudget} ({ToolReliabilityLedger.DescribeModel(builderModelPath)}).");
                }
                modelChosenToolBudget = ledgerBudget;

                string groundingContext = BuildToolDecisionGroundingContext(userPayload);
                IReadOnlyCollection<string>? knownWorkspaceFiles = _connectedWorkspace.CodebaseEditAccessEnabled
                    ? _connectedWorkspace.ConnectedFiles
                    : null;

                Grammar grammar = CreateBuilderToolDecisionGrammar();
                int modelChosenCalls = 0;
                for (int round = 0; round <= MaxBuilderPreflightTools; round++)
                {
                    if (usedToolCalls.Count >= MaxBuilderPreflightTools || modelChosenCalls >= modelChosenToolBudget)
                        break;

                    BuilderToolDecision? decision = null;
                    string lastInvalidDecision = string.Empty;
                    try
                    {
                        for (int attempt = 0; attempt < 2 && decision == null; attempt++)
                        {
                            string decisionSystem = BuildBuilderToolDecisionSystemPrompt(attempt > 0, usedToolCalls.Count);
                            string decisionPayload = BuildBuilderToolDecisionPayload(
                                userPayload,
                                lastInvalidDecision,
                                toolContext.ToString(),
                                usedToolCalls);
                            ReasoningParser.ParsedResponse decisionResponse = await ExecuteCouncilRoleAsync(
                                CouncilRole.Builder,
                                decisionSystem,
                                decisionPayload,
                                token,
                                temperatureOverride: 0.0f,
                                baseStateVault: null,
                                loadBaseState: false,
                                allowBatchRecovery: allowBatchRecovery,
                                showLiveCard: false,
                                maxGenerationTokensOverride: 192,
                                contextSizeOverride: contextSizeOverride,
                                useBuilderToolDecision: false,
                                outputGrammar: grammar,
                                allowAgenticPauses: false,
                                internalInferenceStep: true);

                            lastInvalidDecision = decisionResponse.Answer.Trim();
                            if (!TryParseBuilderToolDecision(lastInvalidDecision, out decision, out string parseError))
                            {
                                LogActivity($"Builder tool decision rejected (attempt {attempt + 1}/2): {parseError}");
                                lastInvalidDecision += "\nRejected because: " + parseError;
                                continue;
                            }

                            // The grammar guarantees valid JSON, not a sensible call. Semantic
                            // validation catches invented queries (numbers/terms from nowhere,
                            // paths that match no workspace file) and turns them into a SPECIFIC
                            // repair instruction instead of a hallucinated tool run.
                            if (decision != null && decision.Action == "tool"
                                && !LocalToolIntentRouter.TryValidateToolQuery(
                                    decision.Tool,
                                    decision.Query,
                                    groundingContext,
                                    LocalToolIntentRouter.ValidationStrictness.Grounded,
                                    knownWorkspaceFiles,
                                    out string validationError))
                            {
                                ToolReliabilityLedger.RecordDecision(builderModelPath, decision.Tool, valid: false);
                                LogActivity($"Builder tool decision failed semantic validation (attempt {attempt + 1}/2): {validationError}");
                                lastInvalidDecision += "\nRejected because: " + validationError;
                                decision = null;
                                continue;
                            }

                            if (decision != null)
                                ToolReliabilityLedger.RecordDecision(builderModelPath, decision.Action == "tool" ? decision.Tool : "NONE", valid: true);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        // Tool routing is an enhancement, not a reason to lose the deliverable.
                        LogActivity($"Builder constrained tool decision unavailable; continuing without another tool: {ex.Message}");
                        break;
                    }

                    if (decision == null || decision.Action == "final" || usedToolCalls.Count >= MaxBuilderPreflightTools)
                        break;

                    BuilderToolObservation? observation = await RunBuilderPreflightToolAsync(decision, usedToolCalls, toolContext, observations, token);
                    if (observation == null)
                        break;

                    ToolReliabilityLedger.RecordExecution(builderModelPath, decision.Tool, observation.Success);
                    modelChosenCalls++;

                    // Earned trust: a compact model whose first chosen call validated AND returned
                    // a successful observation may take the second call a larger model would get.
                    if (modelChosenCalls == modelChosenToolBudget
                        && modelChosenToolBudget < MaxBuilderPreflightTools
                        && observation.Success)
                    {
                        modelChosenToolBudget++;
                        LogActivity("Builder earned one additional tool call (first call validated and succeeded).");
                    }
                }
            }

            string finalSystemPrompt = systemPrompt
                .Replace(
                    AgenticPauseRule,
                    "\n\nTOOL PREFLIGHT COMPLETE:\nDo not emit tool calls, [PAUSE:] markers, or JSON tool envelopes. Generate the requested Builder deliverable normally. Code and prose are unrestricted by the tool-decision grammar.\n",
                    StringComparison.Ordinal)
                // The codebase read-tools addendum rides after the pause rule; strip it too so
                // the final pass is not told to emit [PAUSE:] lines that nothing intercepts.
                .Replace(AgenticPauseCodebaseToolsAddendum, string.Empty, StringComparison.Ordinal);

            // Small models copy raw observation envelopes back out as their "answer"; the same
            // information as bare FACT lines gets USED instead of echoed. Failed observations are
            // dropped entirely for small models — they cannot reason about a failure report and
            // will parrot it. Larger models keep the full envelopes (they cite them correctly and
            // benefit from the query/status detail).
            bool digestForSmallModel = builderCapability != null
                && (builderCapability.IsSubOneB || builderCapability.IsCompactClass);
            string observationBlock;
            if (digestForSmallModel)
            {
                observationBlock = string.Join("\n\n", observations
                    .Where(o => o.Success)
                    .Select(o => LocalToolIntentRouter.DigestObservation(o.Tool, o.Query, o.Result))
                    .Where(digest => digest.Length > 0));
                if (observations.Any(o => !o.Success))
                    LogActivity("Builder preflight: failed tool observations omitted from the small-model payload.");
            }
            else
            {
                observationBlock = toolContext.ToString().TrimEnd();
            }

            string finalPayload = observationBlock.Length == 0
                ? userPayload
                // Keep the role's "produce the deliverable now" closing anchor as the final text
                // the model reads. Appending observations after it encourages small models to echo
                // tool output instead of implementing the request.
                : observationBlock + "\n\n" + userPayload;

            ReasoningParser.ParsedResponse finalResponse = await ExecuteCouncilRoleAsync(
                CouncilRole.Builder,
                finalSystemPrompt,
                finalPayload,
                token,
                temperatureOverride,
                baseStateVault,
                loadBaseState,
                allowBatchRecovery,
                showLiveCard,
                maxGenerationTokensOverride,
                contextSizeOverride,
                useBuilderToolDecision: false,
                outputGrammar: null,
                allowAgenticPauses: false,
                internalInferenceStep: false);

            // Echo recovery: a small model sometimes copies the injected tool observations (or the
            // tool catalog itself) back out as its "deliverable" instead of answering. When the
            // answer is recognizably an observation echo, regenerate once with the observations
            // removed; if the clean pass fails too, keep the original rather than lose the turn.
            string echoReference = toolContext + "\n" + observationBlock;
            if (observationBlock.Length > 0 && LocalToolIntentRouter.IsToolObservationEcho(finalResponse.Answer, echoReference))
            {
                ToolReliabilityLedger.RecordEcho(builderModelPath);
                LogActivity("Builder deliverable echoed the tool observations; regenerating once without tool context.");
                try
                {
                    ReasoningParser.ParsedResponse cleanRetry = await ExecuteCouncilRoleAsync(
                        CouncilRole.Builder,
                        finalSystemPrompt,
                        userPayload,
                        token,
                        temperatureOverride,
                        baseStateVault,
                        loadBaseState,
                        allowBatchRecovery,
                        showLiveCard,
                        maxGenerationTokensOverride,
                        contextSizeOverride,
                        useBuilderToolDecision: false,
                        outputGrammar: null,
                        allowAgenticPauses: false,
                        internalInferenceStep: false);
                    if (!string.IsNullOrWhiteSpace(cleanRetry.Answer)
                        && !LocalToolIntentRouter.IsToolObservationEcho(cleanRetry.Answer, echoReference))
                    {
                        return cleanRetry;
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    LogActivity($"Echo-recovery regeneration failed; keeping the original Builder output: {ex.Message}");
                }
            }

            return finalResponse;
        }

        private sealed record BuilderToolObservation(string Tool, string Query, bool Success, string Result);

        private async Task<BuilderToolObservation?> RunBuilderPreflightToolAsync(
            BuilderToolDecision decision,
            HashSet<string> usedToolCalls,
            StringBuilder toolContext,
            List<BuilderToolObservation> observations,
            CancellationToken token)
        {
            string callKey = decision.Tool + "\n" + decision.Query.Trim();
            if (!usedToolCalls.Add(callKey))
            {
                LogActivity("Builder attempted a duplicate preflight tool call; stopping tool loop.");
                return null;
            }

            LogActivity($"Builder constrained tool decision {usedToolCalls.Count}/{MaxBuilderPreflightTools}: {decision.Tool}.");
            AgenticPauseEngine.StructuredToolResult toolResult = _agenticPauseEngine == null
                ? AgenticPauseEngine.StructuredToolResult.Fail("The tool dispatcher is unavailable.")
                : await _agenticPauseEngine.ExecuteStructuredToolAsync(decision.Tool, decision.Query, token);

            string resultText = toolResult.IsSuccess ? toolResult.Data : toolResult.ErrorMessage;
            if (resultText.Length > BuilderToolResultMaxChars)
                resultText = resultText[..BuilderToolResultMaxChars] + "\n[tool result truncated]";

            toolContext.AppendLine($"[[TOOL OBSERVATION {usedToolCalls.Count}]]");
            toolContext.AppendLine($"Tool: {decision.Tool}");
            toolContext.AppendLine($"Query: {decision.Query}");
            toolContext.AppendLine($"Status: {(toolResult.IsSuccess ? "success" : "failed")}");
            toolContext.AppendLine(resultText);
            toolContext.AppendLine($"[[END TOOL OBSERVATION {usedToolCalls.Count}]]");

            var observation = new BuilderToolObservation(decision.Tool, decision.Query, toolResult.IsSuccess, resultText);
            observations.Add(observation);
            return observation;
        }

        private string BuildToolDecisionGroundingContext(string userPayload)
        {
            // Grounding evidence for semantic validation: the user's actual objective plus the
            // tail of the Builder payload (where document context, prior knowledge, and the plan
            // live). Generous on purpose — numbers a tool query legitimately uses may come from
            // any of these, and a false rejection costs a useful tool call.
            CouncilRunContext? runContext = _activeCouncilRunContext ?? _lastRunContext;
            string objective = runContext?.UserPrompt ?? string.Empty;
            string payloadExcerpt = userPayload ?? string.Empty;
            const int maxExcerptChars = 6000;
            if (payloadExcerpt.Length > maxExcerptChars)
                payloadExcerpt = payloadExcerpt[^maxExcerptChars..];
            return objective + "\n" + payloadExcerpt;
        }

        private bool TryBuildDeterministicBuilderToolDecision(string userPayload, out BuilderToolDecision? decision)
        {
            decision = null;
            string objective = ExtractOriginalRequestForToolRouting(userPayload);
            if (string.IsNullOrWhiteSpace(objective))
                objective = (userPayload ?? string.Empty).Trim();
            if (objective.Length > BuilderToolDecisionMaxQueryChars)
                objective = objective[..BuilderToolDecisionMaxQueryChars];

            if (!LocalToolIntentRouter.TryRouteIntent(
                    objective,
                    _isWebSearchEnabled,
                    _connectedWorkspace.CodebaseEditAccessEnabled,
                    out LocalToolIntentRouter.ToolIntent? intent) || intent == null)
            {
                return false;
            }

            decision = new BuilderToolDecision("tool", intent.Tool, intent.Query);
            LogActivity($"Deterministic tool router matched {intent.Tool} ({intent.Reason}).");
            return true;
        }

        private static string ExtractOriginalRequestForToolRouting(string userPayload)
        {
            if (string.IsNullOrWhiteSpace(userPayload))
                return string.Empty;

            Match match = Regex.Match(
                userPayload,
                @"\[\[ORIGINAL REQUEST\]\]\s*(?<body>.*?)\s*\[\[END ORIGINAL REQUEST\]\]",
                RegexOptions.Singleline | RegexOptions.IgnoreCase);
            return match.Success ? match.Groups["body"].Value.Trim() : string.Empty;
        }

        private string BuildBuilderToolDecisionSystemPrompt(bool isRepairAttempt, int toolsUsed)
        {
            string webTool = _isWebSearchEnabled ? ", WEB_SEARCH" : string.Empty;
            bool codebaseTools = _connectedWorkspace.CodebaseEditAccessEnabled;
            string codebaseToolList = codebaseTools ? ", READ_FILE, SEARCH_CODEBASE, LIST_FILES" : string.Empty;
            string codebaseToolGuidance = codebaseTools
                ? "Use READ_FILE to open a known connected-workspace relative path, SEARCH_CODEBASE to find symbols/text across connected code files, and LIST_FILES to inspect connected workspace paths. "
                : string.Empty;
            string repair = isRepairAttempt
                ? "The previous envelope was semantically invalid. Correct it. "
                : string.Empty;

            return
                "You are a tool router for the local Builder. " + repair +
                $"This is decision round {toolsUsed + 1}; {toolsUsed} tool(s) have already run. " +
                "Decide whether one additional tool must run before the Builder generates its deliverable. " +
                "Tools retrieve evidence or calculate/execute checks; they do not write the final code. " +
                "Choose final when the supplied context and observations are sufficient. Never repeat an equivalent call. " +
                "Available tools: SEARCH_HIPPOCAMPUS, CALCULATE, RUN_SANDBOX, PYTHON_MATH" + codebaseToolList + webTool + ". " +
                codebaseToolGuidance +
                "Use RUN_SANDBOX only to check a small supplied snippet, not to generate an application. " +
                "No tool can edit files, install packages, operate the UI, or modify Project Canvas. " +
                "Return exactly one JSON object with fields in this order: action, tool, query. " +
                "For no tool use {\"action\":\"final\",\"tool\":\"NONE\",\"query\":\"\"}. " +
                "For a tool use action=tool, an available tool name, and a standalone non-empty query. " +
                "Tool queries must be grounded in the actual request: never invent numbers, entities, or paths that the request does not contain. " +
                LocalToolIntentRouter.BuildToolDecisionFewShotExamples(_isWebSearchEnabled, codebaseTools);
        }

        private string BuildBuilderToolDecisionPayload(
            string userPayload,
            string previousInvalidDecision,
            string priorToolObservations,
            IReadOnlyCollection<string> usedToolCalls)
        {
            CouncilRunContext? runContext = _activeCouncilRunContext ?? _lastRunContext;
            string objective = runContext?.UserPrompt ?? string.Empty;
            string payloadExcerpt = userPayload ?? string.Empty;
            const int maxExcerptChars = 10000;
            if (payloadExcerpt.Length > maxExcerptChars)
                payloadExcerpt = "[earlier Builder context omitted]\n" + payloadExcerpt[^maxExcerptChars..];

            var payload = new StringBuilder();
            payload.AppendLine(BuildCouncilGoalContractBlock(runContext?.GoalContract));
            payload.AppendLine(BuildCouncilCapabilityCard(_isWebSearchEnabled, codebaseToolsEnabled: _connectedWorkspace.CodebaseEditAccessEnabled));
            if (!string.IsNullOrWhiteSpace(objective))
            {
                payload.AppendLine("[[USER OBJECTIVE]]");
                payload.AppendLine(objective);
                payload.AppendLine("[[END USER OBJECTIVE]]");
            }

            payload.AppendLine("[[BUILDER INPUT]]");
            payload.AppendLine(payloadExcerpt);
            payload.AppendLine("[[END BUILDER INPUT]]");
            if (!string.IsNullOrWhiteSpace(priorToolObservations))
            {
                payload.AppendLine("[[PRIOR TOOL OBSERVATIONS]]");
                payload.AppendLine(priorToolObservations.Trim());
                payload.AppendLine("[[END PRIOR TOOL OBSERVATIONS]]");
            }
            if (usedToolCalls.Count > 0)
                payload.AppendLine("Already-used calls must not be repeated: " + string.Join(" | ", usedToolCalls.Select(call =>
                    call.Length <= 180 ? call.Replace('\n', ':') : call[..180].Replace('\n', ':') + "...")));
            if (!string.IsNullOrWhiteSpace(previousInvalidDecision))
                payload.AppendLine("Previous invalid envelope: " + previousInvalidDecision);
            return payload.ToString();
        }

        private Grammar CreateBuilderToolDecisionGrammar()
        {
            var tools = new List<string>
            {
                "NONE",
                "SEARCH_HIPPOCAMPUS",
                "CALCULATE",
                "RUN_SANDBOX",
                "PYTHON_MATH"
            };
            if (_connectedWorkspace.CodebaseEditAccessEnabled)
            {
                tools.Add("READ_FILE");
                tools.Add("SEARCH_CODEBASE");
                tools.Add("LIST_FILES");
            }
            if (_isWebSearchEnabled)
                tools.Add("WEB_SEARCH");

            string toolAlternatives = string.Join(" | ", tools.Select(tool => $"\"\\\"{tool}\\\"\""));
            string gbnf =
                "root ::= object\n" +
                "object ::= \"{\" ws \"\\\"action\\\"\" ws \":\" ws action ws \",\" ws \"\\\"tool\\\"\" ws \":\" ws tool ws \",\" ws \"\\\"query\\\"\" ws \":\" ws string ws \"}\" ws\n" +
                "action ::= \"\\\"final\\\"\" | \"\\\"tool\\\"\"\n" +
                "tool ::= " + toolAlternatives + "\n" +
                "string ::= \"\\\"\" char* \"\\\"\"\n" +
                "char ::= [^\"\\\\\\x00-\\x1F] | \"\\\\\" escape\n" +
                "escape ::= [\"\\\\/bfnrt] | \"u\" hex hex hex hex\n" +
                "hex ::= [0-9a-fA-F]\n" +
                "ws ::= [ \\t\\n\\r]*\n";
            return new Grammar(gbnf, "root");
        }

        private bool TryParseBuilderToolDecision(
            string json,
            out BuilderToolDecision? decision,
            out string error)
        {
            decision = null;
            error = string.Empty;
            try
            {
                using JsonDocument document = JsonDocument.Parse(json);
                JsonElement root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 3)
                    return Reject("Envelope must contain exactly action, tool, and query.", out error);

                if (!root.TryGetProperty("action", out JsonElement actionElement)
                    || !root.TryGetProperty("tool", out JsonElement toolElement)
                    || !root.TryGetProperty("query", out JsonElement queryElement)
                    || actionElement.ValueKind != JsonValueKind.String
                    || toolElement.ValueKind != JsonValueKind.String
                    || queryElement.ValueKind != JsonValueKind.String)
                {
                    return Reject("Envelope fields must be strings.", out error);
                }

                string action = actionElement.GetString() ?? string.Empty;
                string tool = toolElement.GetString() ?? string.Empty;
                string query = queryElement.GetString() ?? string.Empty;
                if (action == "final" && tool == "NONE" && query.Length == 0)
                {
                    decision = new BuilderToolDecision(action, tool, query);
                    return true;
                }

                var allowedTools = new HashSet<string>(StringComparer.Ordinal)
                {
                    "SEARCH_HIPPOCAMPUS", "CALCULATE", "RUN_SANDBOX", "PYTHON_MATH"
                };
                if (_connectedWorkspace.CodebaseEditAccessEnabled)
                {
                    allowedTools.Add("READ_FILE");
                    allowedTools.Add("SEARCH_CODEBASE");
                    allowedTools.Add("LIST_FILES");
                }
                if (_isWebSearchEnabled)
                    allowedTools.Add("WEB_SEARCH");

                if (action != "tool" || !allowedTools.Contains(tool))
                    return Reject("Tool action or tool name is inconsistent.", out error);
                if (string.IsNullOrWhiteSpace(query))
                    return Reject("Tool query is empty.", out error);
                if (query.Length > BuilderToolDecisionMaxQueryChars)
                    return Reject($"Tool query exceeds {BuilderToolDecisionMaxQueryChars} characters.", out error);

                decision = new BuilderToolDecision(action, tool, query.Trim());
                return true;
            }
            catch (JsonException ex)
            {
                return Reject("Invalid JSON: " + ex.Message, out error);
            }
        }

        private static bool Reject(string message, out string error)
        {
            error = message;
            return false;
        }
    }
}
