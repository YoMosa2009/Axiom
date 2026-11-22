using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Malx_AI.Agent;
using Malx_AI.ComputerUse;

namespace Malx_AI
{
    public partial class WorkplaceView
    {
        private readonly WorkplaceBrowserSession _liveBrowser = new();

        private bool HasAttachedWorkplaceSkills => _capabilityRegistry.Skills.Any(skill => skill.IsAttached);

        private AgentToolResult ReadWorkplaceSkill(string name)
        {
            string procedure = _capabilityRegistry.ReadAttachedSkill(name);
            return procedure.Length == 0
                ? AgentToolResult.Fail("Choose an exact name or ID from the attached Skill catalog.")
                : AgentToolResult.Ok(procedure);
        }

        private async Task<AgentToolResult?> ExecuteAgentWorkplaceToolAsync(AgentToolCall call, CancellationToken token)
        {
            if (call.Tool.Equals(AgentToolNames.UseSkill, StringComparison.OrdinalIgnoreCase))
                return ReadWorkplaceSkill(call.Arg("name"));

            string action = call.Arg("action").Trim().ToLowerInvariant();
            if (action == "search")
            {
                if (!_isWebSearchEnabled)
                    return AgentToolResult.Fail("Web search is disabled.");
                string query = call.Arg("url");
                if (string.IsNullOrWhiteSpace(query))
                    return AgentToolResult.Fail("Provide a web search query.");
                string result = await ExecuteWebSearchAsync(query, token);
                return HasWebSearchEvidence(result) ? AgentToolResult.Ok(result) : AgentToolResult.Fail(result);
            }
            if (action is "inspect" or "click" or "fill")
                return await ExecuteLiveBrowserAsync(action, call.Arg("url"), call.Arg("target_id"), call.Arg("text"), token);
            if (action is "open" or "navigate")
                _liveBrowser.Reset();
            return null;
        }

        private async Task<AgentToolResult> ExecuteLiveBrowserAsync(string action, string url, string target, string text, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                action = action.Trim().ToLowerInvariant();
                switch (action)
                {
                    case "open":
                        _liveBrowser.Reset();
                        return BrowserControlService.OpenInBrowser(BrowserControlService.DetectSingleBrowser(), url);
                    case "inspect":
                        return await _liveBrowser.InspectAsync(token);
                    case "click":
                    case "fill":
                        return await _liveBrowser.ActAsync(action, target, text, ApproveLiveBrowserActionAsync, token);
                    default:
                        return AgentToolResult.Fail("Use browser open, inspect, click or fill.");
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                return AgentToolResult.Fail("The visible browser changed or its accessibility controls are unavailable. Inspect the page again.");
            }
        }

        private async Task<bool> ApproveLiveBrowserActionAsync(string preview, CancellationToken token)
        {
            var call = new AgentToolCall(AgentToolNames.BrowserControl,
                new Dictionary<string, string> { ["preview"] = preview });
            return await RequestAgentApprovalAsync(call, "Browser interaction", token) == AgentApprovalOutcome.Approve;
        }

        private async Task<AgentToolResult> ExecuteLocalBrowserCommandAsync(string query, CancellationToken token)
        {
            string[] words = query.Trim().Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
            string action = words[0].ToLowerInvariant();
            string argument = words.Length >= 2 ? words[1] : "";
            string text = words.Length == 3 ? words[2] : "";
            return await ExecuteLiveBrowserAsync(action, action == "open" ? argument : "", argument, text, token);
        }

        private static string ReadToolArgument(JsonElement arguments, string name) =>
            arguments.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? "" : "";
    }
}
