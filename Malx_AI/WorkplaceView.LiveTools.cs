using System;
using System.Collections.Generic;
using System.IO;
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

        // Pages the agent opens load here, invisibly, unless the user asked to see them in their
        // own browser (owner request 2026-11-26: the screen must stay on Axiom).
        private readonly HeadlessBrowserSession _headlessBrowser = new();
        private HeadlessBrowserSession.PageSnapshot? _headlessSnapshot;
        private bool _liveBrowserIsVisible;
        private Timer? _headlessIdleTimer;
        private static readonly TimeSpan HeadlessIdleLimit = TimeSpan.FromMinutes(10);

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

            if (call.Tool.Equals(AgentToolNames.TakeScreenshot, StringComparison.OrdinalIgnoreCase))
                return await TryHeadlessScreenshotAsync(call, token);

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

            if (action is "open" or "navigate")
            {
                if (IsTrue(call.Arg("visible")))
                {
                    // The user asked to see it: their own browser, as before.
                    _liveBrowser.Reset();
                    _liveBrowserIsVisible = true;
                    return null;
                }
                return await OpenHeadlessAsync(call.Arg("url"), token);
            }

            if (action is "read" or "read_page" or "fetch" && string.IsNullOrWhiteSpace(call.Arg("url")))
            {
                // "Read the page I have open" (a live run asked for exactly that and failed).
                if (!_liveBrowserIsVisible && _headlessBrowser.HasPage)
                    return await HeadlessSnapshotResultAsync(includeText: true, token);
                return AgentToolResult.Fail("No page is open. Use browser_control read_page with a URL, or open one first.");
            }

            if (action is "inspect" or "click" or "fill")
            {
                if (!_liveBrowserIsVisible)
                    return await ExecuteHeadlessBrowserAsync(action, call.Arg("target_id"), call.Arg("text"), token);
                return await ExecuteLiveBrowserAsync(action, call.Arg("url"), call.Arg("target_id"), call.Arg("text"), token);
            }
            return null;
        }

        private static bool IsTrue(string value) =>
            value.Trim().Equals("true", StringComparison.OrdinalIgnoreCase) || value.Trim() == "1"
            || value.Trim().Equals("yes", StringComparison.OrdinalIgnoreCase);

        private async Task<AgentToolResult> OpenHeadlessAsync(string rawUrl, CancellationToken token)
        {
            string url = BrowserControlService.NormalizeHttpUrl(rawUrl);
            if (string.IsNullOrWhiteSpace(url))
                url = BrowserControlService.LocalPageUrl(rawUrl) ?? string.Empty;
            if (string.IsNullOrWhiteSpace(url))
                return AgentToolResult.Fail("A valid http:// or https:// URL, or the path of a local .html/.htm/.svg/.pdf file, is required for browser_control action 'open'.");
            if (HeadlessBrowserSession.FindBrowser() == null)
            {
                // No Chromium browser to run in the background: fall back to the visible browser.
                _liveBrowser.Reset();
                _liveBrowserIsVisible = true;
                return BrowserControlService.OpenInBrowser(BrowserControlService.DetectSingleBrowser(), url);
            }

            try
            {
                _liveBrowserIsVisible = false;
                _headlessSnapshot = await _headlessBrowser.OpenAsync(url, token);
                TouchHeadlessBrowser();
                return AgentToolResult.Ok(HeadlessBrowserSession.Describe(_headlessSnapshot, includeText: true, maxTextChars: 4000)
                    + "\n\nThe page is open in Axiom's background browser; the user's screen did not change. "
                    + "Use inspect/click/fill with a target_id, read_page (no url) for the full text, or take_screenshot (no arguments) to see it.");
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                return AgentToolResult.Fail($"Could not open {url} in the background browser: {ex.Message}");
            }
        }

        private async Task<AgentToolResult> HeadlessSnapshotResultAsync(bool includeText, CancellationToken token)
        {
            try
            {
                _headlessSnapshot = await _headlessBrowser.SnapshotAsync(token);
                TouchHeadlessBrowser();
                return AgentToolResult.Ok(HeadlessBrowserSession.Describe(_headlessSnapshot, includeText, includeText ? 7000 : 0));
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                return AgentToolResult.Fail("The background browser page could not be read: " + ex.Message);
            }
        }

        private async Task<AgentToolResult> ExecuteHeadlessBrowserAsync(string action, string targetId, string text, CancellationToken token)
        {
            if (!_headlessBrowser.HasPage)
                return AgentToolResult.Fail("No page is open. Use browser_control open with a URL first; it loads in the background without changing the user's screen.");
            if (action == "inspect")
                return await HeadlessSnapshotResultAsync(includeText: false, token);

            HeadlessBrowserSession.Control? target = _headlessSnapshot?.Controls.FirstOrDefault(c => c.Id == targetId.Trim());
            if (target == null)
                return AgentToolResult.Fail("Inspect the page first and use a target_id from that result.");
            if (action == "fill" && (target.Kind != "field" || text.Length is 0 or > 2000 || text.Any(char.IsControl)))
                return AgentToolResult.Fail("Fill requires a text field and a single line of up to 2000 characters.");
            if (action == "click" && target.Kind == "field")
                return AgentToolResult.Fail("This is a text field; use fill.");

            var safety = ComputerUseSafety.Evaluate(new ComputerUseAction { Summary = target.Name, Text = text }, null);
            if (safety.Dangerous)
                return AgentToolResult.Fail(safety.Reason);
            BrowserActionRisk.Level risk = BrowserActionRisk.Classify(target.Name);
            if (risk == BrowserActionRisk.Level.Blocked)
                return AgentToolResult.Fail(BrowserActionRisk.BlockedMessage);

            // Following a link or filling a search box changes nothing outside the page, so it
            // needs no approval; a button may submit something, so it asks as before.
            bool needsApproval = target.Kind == "click" || risk == BrowserActionRisk.Level.Irreversible;
            if (needsApproval)
            {
                string detail = $"{action} “{target.Name}” on “{_headlessSnapshot?.Title}” (background browser)"
                    + (action == "fill" ? $"\nText: {text}" : "")
                    + (risk == BrowserActionRisk.Level.Irreversible ? "\n" + BrowserActionRisk.IrreversibleWarning : "");
                if (!await ApproveLiveBrowserActionAsync(detail, token))
                    return AgentToolResult.Fail("The user declined this browser action.");
            }

            try
            {
                TouchHeadlessBrowser();
                if (action == "fill")
                {
                    bool filled = await _headlessBrowser.FillAsync(target.Id, text, token);
                    return filled
                        ? AgentToolResult.Ok("Field value set and verified. The form was not submitted.")
                        : AgentToolResult.Fail("The page did not accept the text in that field. Inspect the page again.");
                }

                string outcome = await _headlessBrowser.ClickAsync(target.Id, token);
                _headlessSnapshot = null;
                return outcome switch
                {
                    "missing" => AgentToolResult.Fail("That control is no longer on the page. Inspect the page again."),
                    "navigated" => AgentToolResult.Ok($"Clicked “{target.Name}”. The page changed to {_headlessBrowser.CurrentUrl}. Inspect or read_page to see it."),
                    _ => AgentToolResult.Ok($"Clicked “{target.Name}”. Inspect the page to see the result.")
                };
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                return AgentToolResult.Fail("The background browser action failed: " + ex.Message);
            }
        }

        /// <summary>
        /// take_screenshot with no url while a background page is open shows that page as it is
        /// now. Returns null to let the normal capture run (a url, or a named window).
        /// </summary>
        private async Task<AgentToolResult?> TryHeadlessScreenshotAsync(AgentToolCall call, CancellationToken token)
        {
            if (_liveBrowserIsVisible || !_headlessBrowser.HasPage || !string.IsNullOrWhiteSpace(call.Arg("url")))
                return null;
            string window = call.Arg("window").Trim().ToLowerInvariant();
            bool meansBrowser = window.Length == 0
                || new[] { "browser", "edge", "chrome", "brave", "page", "web" }.Any(window.Contains)
                || (_headlessSnapshot?.Title is { Length: > 0 } title && title.Contains(window, StringComparison.OrdinalIgnoreCase));
            if (!meansBrowser)
                return null;

            try
            {
                AgentScreenshot shot = await _headlessBrowser.ScreenshotAsync(token);
                TouchHeadlessBrowser();
                string folder = Path.Combine(AppDataPaths.Root, "WorkplaceCaptures");
                Directory.CreateDirectory(folder);
                string path = Path.Combine(folder, $"screenshot_{DateTime.Now:yyyyMMdd_HHmmss_fff}.png");
                await File.WriteAllBytesAsync(path, shot.PngBytes, token);
                string dataUrl = "data:image/png;base64," + Convert.ToBase64String(shot.PngBytes);
                return AgentToolResult.Ok(
                    $"Captured screenshot of {shot.Description} ({shot.Width}x{shot.Height}) saved to {path}. Inspect the attached image closely.",
                    dataUrl, path);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                return AgentToolResult.Fail("The background browser screenshot failed: " + ex.Message);
            }
        }

        /// <summary>Closes the background browser after ten quiet minutes.</summary>
        private void TouchHeadlessBrowser()
        {
            _headlessIdleTimer ??= new Timer(_ =>
            {
                if (DateTime.UtcNow - _headlessBrowser.LastUsedUtc >= HeadlessIdleLimit)
                {
                    _headlessSnapshot = null;
                    _ = _headlessBrowser.DisposeAsync().AsTask();
                }
            });
            _headlessIdleTimer.Change(HeadlessIdleLimit + TimeSpan.FromSeconds(5), Timeout.InfiniteTimeSpan);
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
                        _liveBrowserIsVisible = true;
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
            // Explicit local browser commands from the user ("open <url>") use their browser.
            return await ExecuteLiveBrowserAsync(action, action == "open" ? argument : "", argument, text, token);
        }

        private static string ReadToolArgument(JsonElement arguments, string name) =>
            arguments.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? "" : "";
    }
}
