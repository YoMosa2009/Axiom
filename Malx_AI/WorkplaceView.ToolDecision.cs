using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LLama.Sampling;

namespace Malx_AI
{
    public partial class WorkplaceView
    {
        private const int BuilderToolDecisionMaxQueryChars = 4000;
        private const int BuilderToolResultMaxChars = 8000;

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
            int? contextSizeOverride)
        {
            BuilderToolDecision? decision = null;
            string lastInvalidDecision = string.Empty;

            try
            {
                Grammar grammar = CreateBuilderToolDecisionGrammar();
                for (int attempt = 0; attempt < 2 && decision == null; attempt++)
                {
                    string decisionSystem = BuildBuilderToolDecisionSystemPrompt(attempt > 0);
                    string decisionPayload = BuildBuilderToolDecisionPayload(userPayload, lastInvalidDecision);
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
                        LogActivity($"Builder tool decision rejected (attempt {attempt + 1}/2): {parseError}");
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Tool routing is an enhancement, not a reason to lose the Builder deliverable.
                // The final pass still runs normally and is subsequently validated/repaired.
                LogActivity($"Builder constrained tool decision unavailable; continuing without a tool: {ex.Message}");
            }

            var toolContext = new StringBuilder();
            if (decision?.Action == "tool")
            {
                if (_agenticPauseEngine == null)
                {
                    toolContext.AppendLine("Tool execution failed: the tool dispatcher is unavailable.");
                }
                else
                {
                    LogActivity($"Builder constrained tool decision: {decision.Tool}.");
                    AgenticPauseEngine.StructuredToolResult toolResult =
                        await _agenticPauseEngine.ExecuteStructuredToolAsync(decision.Tool, decision.Query, token);

                    string resultText = toolResult.IsSuccess ? toolResult.Data : toolResult.ErrorMessage;
                    if (resultText.Length > BuilderToolResultMaxChars)
                        resultText = resultText[..BuilderToolResultMaxChars] + "\n[tool result truncated]";

                    toolContext.AppendLine("[[PRE-BUILDER TOOL RESULT]]");
                    toolContext.AppendLine($"Tool: {decision.Tool}");
                    toolContext.AppendLine($"Status: {(toolResult.IsSuccess ? "success" : "failed")}");
                    toolContext.AppendLine(resultText);
                    toolContext.AppendLine("[[END PRE-BUILDER TOOL RESULT]]");
                }
            }

            string finalSystemPrompt = systemPrompt.Replace(
                AgenticPauseRule,
                "\n\nTOOL PREFLIGHT COMPLETE:\nDo not emit tool calls, [PAUSE:] markers, or JSON tool envelopes. Generate the requested Builder deliverable normally. Code and prose are unrestricted by the tool-decision grammar.\n",
                StringComparison.Ordinal);

            string finalPayload = toolContext.Length == 0
                ? userPayload
                : userPayload + "\n\n" + toolContext;

            return await ExecuteCouncilRoleAsync(
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
        }

        private string BuildBuilderToolDecisionSystemPrompt(bool isRepairAttempt)
        {
            string webTool = _isWebSearchEnabled ? ", WEB_SEARCH" : string.Empty;
            string repair = isRepairAttempt
                ? "The previous envelope was semantically invalid. Correct it. "
                : string.Empty;

            return
                "You are a tool router for the local Builder. " + repair +
                "Decide whether exactly one tool must run before the Builder generates its deliverable. " +
                "Tools retrieve evidence or calculate/execute checks; they do not write the final code. " +
                "Choose final when the supplied context is sufficient. " +
                "Available tools: SEARCH_HIPPOCAMPUS, CALCULATE, RUN_SANDBOX, PYTHON_MATH" + webTool + ". " +
                "Use RUN_SANDBOX only to check a small supplied snippet, not to generate an application. " +
                "Return exactly one JSON object with fields in this order: action, tool, query. " +
                "For no tool use {\"action\":\"final\",\"tool\":\"NONE\",\"query\":\"\"}. " +
                "For a tool use action=tool, an available tool name, and a standalone non-empty query.";
        }

        private string BuildBuilderToolDecisionPayload(string userPayload, string previousInvalidDecision)
        {
            string objective = _lastRunContext?.UserPrompt ?? string.Empty;
            string payloadExcerpt = userPayload ?? string.Empty;
            const int maxExcerptChars = 10000;
            if (payloadExcerpt.Length > maxExcerptChars)
                payloadExcerpt = "[earlier Builder context omitted]\n" + payloadExcerpt[^maxExcerptChars..];

            var payload = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(objective))
            {
                payload.AppendLine("[[USER OBJECTIVE]]");
                payload.AppendLine(objective);
                payload.AppendLine("[[END USER OBJECTIVE]]");
            }

            payload.AppendLine("[[BUILDER INPUT]]");
            payload.AppendLine(payloadExcerpt);
            payload.AppendLine("[[END BUILDER INPUT]]");
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
