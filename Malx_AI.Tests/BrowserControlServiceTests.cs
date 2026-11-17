using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Malx_AI.Agent;
using Xunit;

namespace Malx_AI.Tests
{
    public sealed class BrowserControlServiceTests
    {
        [Fact]
        public void DetectSingleBrowser_AlwaysPicksExactlyOneBrowser()
        {
            DetectedBrowserInfo detected = BrowserControlService.DetectSingleBrowser();

            Assert.False(string.IsNullOrWhiteSpace(detected.BrowserId));
            Assert.False(string.IsNullOrWhiteSpace(detected.DisplayName));
            Assert.False(string.IsNullOrWhiteSpace(detected.SearchUrlTemplate));
        }

        [Fact]
        public async Task ExecuteAsync_ReadPageAndSearch_UseHttpOverrideAndExtractReadableText()
        {
            string requestedUrl = string.Empty;
            var svc = new BrowserControlService(httpGetOverride: (url, _) =>
            {
                requestedUrl = url;
                return Task.FromResult("<html><head><title>Axiom Docs</title></head><body><script>evil()</script><h1>Welcome</h1><p>Browser integration works cleanly.</p></body></html>");
            });

            AgentToolResult pageRes = await svc.ExecuteAsync(
                "read_page",
                "https://example.com/docs",
                destinationPath: null,
                workingDirectory: Path.GetTempPath(),
                isPathAllowed: _ => true,
                CancellationToken.None);

            Assert.True(pageRes.Succeeded);
            Assert.Equal("https://example.com/docs", requestedUrl);
            Assert.Contains("Axiom Docs", pageRes.Output);
            Assert.Contains("Browser integration works cleanly.", pageRes.Output);
            Assert.DoesNotContain("evil()", pageRes.Output);
        }

        [Fact]
        public async Task ExecuteAsync_Download_EnforcesProjectScope()
        {
            string projectDir = Path.Combine(Path.GetTempPath(), "AxiomBrowserScope_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(projectDir);
            string outsideDir = Path.GetTempPath();
            var scope = AgentScope.Folder(projectDir);

            try
            {
                var svc = new BrowserControlService(downloadOverride: (url, dest, _) =>
                {
                    File.WriteAllText(dest, "downloaded-content");
                    return Task.FromResult(18L);
                });

                AgentToolResult blocked = await svc.ExecuteAsync(
                    "download",
                    "https://example.com/tool.txt",
                    destinationPath: Path.Combine(outsideDir, "escape.txt"),
                    workingDirectory: projectDir,
                    isPathAllowed: scope.Allows,
                    CancellationToken.None);

                Assert.False(blocked.Succeeded);
                Assert.Contains("outside", blocked.Error ?? string.Empty, StringComparison.OrdinalIgnoreCase);

                AgentToolResult allowed = await svc.ExecuteAsync(
                    "download",
                    "https://example.com/tool.txt",
                    destinationPath: Path.Combine(projectDir, "tool.txt"),
                    workingDirectory: projectDir,
                    isPathAllowed: scope.Allows,
                    CancellationToken.None);

                Assert.True(allowed.Succeeded);
                Assert.True(File.Exists(Path.Combine(projectDir, "tool.txt")));
            }
            finally
            {
                if (Directory.Exists(projectDir))
                    Directory.Delete(projectDir, recursive: true);
            }
        }

        [Fact]
        public void PermissionPolicy_AllowsReadOnlyAndGatesInstall()
        {
            var detectCall = new AgentToolCall(AgentToolNames.BrowserControl, new Dictionary<string, string> { ["action"] = "detect" });
            var installCall = new AgentToolCall(AgentToolNames.BrowserControl, new Dictionary<string, string> { ["action"] = "install", ["url"] = "Git.Git" });

            Assert.Equal(AgentPermission.Allow, CommandSafetyReviewer.Evaluate(detectCall, AgentScope.WholeComputer(), CommandReviewMode.AutoReview).Permission);
            Assert.Equal(AgentPermission.Ask, CommandSafetyReviewer.Evaluate(installCall, AgentScope.WholeComputer(), CommandReviewMode.AutoReview).Permission);
            Assert.Equal(AgentPermission.Allow, CommandSafetyReviewer.Evaluate(installCall, AgentScope.WholeComputer(), CommandReviewMode.AutoApprove).Permission);
        }
    }
}
