using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Malx_AI.Agent;
using Xunit;

namespace Malx_AI.Tests
{
    // Runs real PowerShell commands, which the updater tests kill: they must not run in parallel.
    [Collection("ProcessLifecycleCollection")]
    public sealed class AgentRound3Tests : IDisposable
    {
        private readonly string _folder = Path.Combine(
            Path.GetTempPath(),
            "AxiomRound3Tests",
            Guid.NewGuid().ToString("N"));

        // Minimal 1x1 PNG bytes
        private static readonly byte[] MinimalPngBytes = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");

        public AgentRound3Tests()
        {
            Directory.CreateDirectory(_folder);
        }

        public void Dispose()
        {
            if (Directory.Exists(_folder))
                Directory.Delete(_folder, recursive: true);
        }

        [Fact]
        public void BuildContinuationGoal_PrioritizesUserFollowUpAndIncludesAntiRepetitionRule()
        {
            string goal = AgentContextManager.BuildContinuationGoal(
                originalGoal: "Create a 3D rocket launch visualizer in G:\\RocketLaunchVisualizer",
                userQuery: "Add a night launch toggle button to the HUD and explain how it works.",
                touchedFiles: new[] { "index.html", "src/main.js" },
                lastErrorOrStatus: "Built: G:\\RocketLaunchVisualizer with 11 files",
                verifiedDependencies: new[] { "three" },
                backupFolder: null);

            int currentHeaderIdx = goal.IndexOf("[CURRENT USER REQUEST — HIGHEST PRIORITY]", StringComparison.Ordinal);
            int queryIdx = goal.IndexOf("Add a night launch toggle button", StringComparison.Ordinal);
            int existingContextIdx = goal.IndexOf("[EXISTING WORKSPACE CONTEXT", StringComparison.Ordinal);

            Assert.True(currentHeaderIdx >= 0, "Expected current user request header");
            Assert.True(queryIdx > currentHeaderIdx, "Expected follow-up query under current request header");
            Assert.True(existingContextIdx > queryIdx, "Expected existing workspace context after follow-up query");
            Assert.Contains("Do NOT repeat", goal, StringComparison.Ordinal);
        }

        [Fact]
        public void TakeScreenshot_RegisteredInToolsSchemasAndVisionPromptOnly()
        {
            Assert.Contains(AgentToolNames.TakeScreenshot, AgentToolNames.All);
            Assert.Contains(AgentToolNames.TakeScreenshot, AgentToolNames.ReadOnly);
            Assert.Contains(AgentToolSchemas.All(), t => string.Equals(t.Name, AgentToolNames.TakeScreenshot, StringComparison.Ordinal));

            var call = new AgentToolCall(AgentToolNames.TakeScreenshot, new Dictionary<string, string>());
            Assert.Equal("Took a screenshot", call.DescribeShort());

            string visionPrompt = AgentPromptBuilder.Build(
                AgentTier.Full,
                AgentScope.Folder(_folder),
                AgentApprovalMode.Auto,
                nativeToolCalling: true,
                visionCapable: true);

            Assert.Contains("VISION & SCREENSHOT INSPECTION", visionPrompt, StringComparison.Ordinal);
            Assert.Contains(AgentToolNames.TakeScreenshot, visionPrompt, StringComparison.Ordinal);

            string nonVisionPrompt = AgentPromptBuilder.Build(
                AgentTier.Full,
                AgentScope.Folder(_folder),
                AgentApprovalMode.Auto,
                nativeToolCalling: true,
                visionCapable: false);

            Assert.DoesNotContain("VISION & SCREENSHOT INSPECTION", nonVisionPrompt, StringComparison.Ordinal);
        }

        [Fact]
        public async Task TakeScreenshot_ExecutorInvokesScreenCaptureAndAttachesImageDataUrl()
        {
            var scope = AgentScope.Folder(_folder);
            int requestedMaxWidth = 0;
            var executor = new AgentToolExecutor(
                scope,
                screenCaptureAsync: (maxWidth, _) =>
                {
                    requestedMaxWidth = maxWidth;
                    return Task.FromResult((MinimalPngBytes, 1280, 720));
                });

            var call = new AgentToolCall(
                AgentToolNames.TakeScreenshot,
                new Dictionary<string, string> { ["save_path"] = "captures/check.png" });

            AgentToolResult result = await executor.ExecuteAsync(call, CancellationToken.None);

            Assert.True(result.Succeeded, result.Output);
            Assert.Equal(1280, requestedMaxWidth);
            Assert.NotNull(result.ImageDataUrl);
            Assert.StartsWith("data:image/png;base64,", result.ImageDataUrl, StringComparison.Ordinal);
            Assert.True(File.Exists(Path.Combine(_folder, "captures", "check.png")));
        }

        [Fact]
        public async Task ReadFile_OnPngImage_ReturnsImageDataUrl()
        {
            string imgPath = Path.Combine(_folder, "preview.png");
            await File.WriteAllBytesAsync(imgPath, MinimalPngBytes);

            var executor = new AgentToolExecutor(AgentScope.Folder(_folder));
            var call = new AgentToolCall(
                AgentToolNames.ReadFile,
                new Dictionary<string, string> { ["path"] = "preview.png" });

            AgentToolResult result = await executor.ExecuteAsync(call, CancellationToken.None);

            Assert.True(result.Succeeded, result.Output);
            Assert.NotNull(result.ImageDataUrl);
            Assert.StartsWith("data:image/png;base64,", result.ImageDataUrl, StringComparison.Ordinal);
        }

        [Fact]
        public async Task AgentSession_PropagatesScreenshotImageDataUrlToSubsequentTurnHistory()
        {
            var scope = AgentScope.Folder(_folder);
            var executor = new AgentToolExecutor(
                scope,
                screenCaptureAsync: (_, _) => Task.FromResult((MinimalPngBytes, 800, 600)));
            var session = new AgentSession(scope, maxSteps: 4, executor: executor);

            var model = new ScriptedAgentModel(
                AgentModelReply.Tool(new AgentToolCall(AgentToolNames.TakeScreenshot, new Dictionary<string, string>())),
                AgentModelReply.Answer("Visual check passed: layout renders cleanly."));

            AgentRunResult run = await session.RunAsync(
                "Inspect the UI visually and summarize.",
                AgentApprovalMode.Auto,
                model,
                (_, _, _) => Task.FromResult(AgentApprovalOutcome.Approve),
                _ => { },
                CancellationToken.None);

            Assert.Single(run.Steps);
            Assert.Equal(2, model.SeenHistories.Count);
            Assert.Single(model.SeenHistories[1]);
            Assert.NotNull(model.SeenHistories[1][0].ImageDataUrl);
            Assert.StartsWith("data:image/png;base64,", model.SeenHistories[1][0].ImageDataUrl, StringComparison.Ordinal);
        }

        [Fact]
        public void CommandSafetyReviewer_AllowsHttpAndHttpsUrlsInsideStrictFolderScope()
        {
            Assert.False(CommandSafetyReviewer.ReferencesPathOutsideScope(
                "(Invoke-WebRequest -Uri 'http://localhost:8123/main.js' -UseBasicParsing).StatusCode",
                @"G:\",
                out string? offendingLocal));
            Assert.Null(offendingLocal);

            Assert.False(CommandSafetyReviewer.ReferencesPathOutsideScope(
                "$u = 'https://cdn.jsdelivr.net/npm/three@0.160.0/build/three.min.js'",
                @"G:\RocketVisualizer3D",
                out string? offendingHttps));
            Assert.Null(offendingHttps);

            Assert.True(CommandSafetyReviewer.ReferencesPathOutsideScope(
                @"Get-Content C:\Windows\win.ini",
                @"G:\",
                out string? offendingSystem));
            Assert.Equal(@"C:\Windows\win.ini", offendingSystem);

            Assert.True(CommandSafetyReviewer.ReferencesPathOutsideScope(
                @"Get-Content D:\other\file.txt",
                @"G:\",
                out string? offendingOtherDrive));
            Assert.Equal(@"D:\other\file.txt", offendingOtherDrive);
        }

        [Fact]
        public async Task AgentSession_BlocksRepeatedScreenshotsWithVaryingSavePathAndUiFocusLoops()
        {
            var scope = AgentScope.Folder(_folder);
            int captureCount = 0;
            var executor = new AgentToolExecutor(
                scope,
                screenCaptureAsync: (_, _) =>
                {
                    captureCount++;
                    return Task.FromResult((MinimalPngBytes, 800, 600));
                });
            var session = new AgentSession(scope, maxSteps: 8, executor: executor);

            var model = new ScriptedAgentModel(
                AgentModelReply.Tool(new AgentToolCall(AgentToolNames.TakeScreenshot, new Dictionary<string, string> { ["save_path"] = "shot1.png" })),
                AgentModelReply.Tool(new AgentToolCall(AgentToolNames.TakeScreenshot, new Dictionary<string, string> { ["save_path"] = "shot2.png" })),
                AgentModelReply.Tool(new AgentToolCall(AgentToolNames.TakeScreenshot, new Dictionary<string, string> { ["save_path"] = "shot3.png" })),
                AgentModelReply.Tool(new AgentToolCall(AgentToolNames.RunCommand, new Dictionary<string, string> { ["command"] = "Write-Output 'WScript.Shell AppActivate 1'" })),
                AgentModelReply.Tool(new AgentToolCall(AgentToolNames.RunCommand, new Dictionary<string, string> { ["command"] = "Write-Output 'WScript.Shell AppActivate 2'" })),
                AgentModelReply.Answer("Completed summary cleanly."));

            AgentRunResult run = await session.RunAsync(
                "Verify app and summarize.",
                AgentApprovalMode.Auto,
                model,
                (_, _, _) => Task.FromResult(AgentApprovalOutcome.Approve),
                _ => { },
                CancellationToken.None,
                commandReviewMode: CommandReviewMode.AutoApprove);

            Assert.Equal(2, captureCount);
            Assert.Equal(5, run.Steps.Count);
            Assert.True(run.Steps[0].Result!.Succeeded);
            Assert.True(run.Steps[1].Result!.Succeeded);
            Assert.False(run.Steps[2].Result!.Succeeded);
            Assert.Contains("Screenshot limit reached", run.Steps[2].Result!.Error, StringComparison.Ordinal);
            Assert.True(run.Steps[3].Result!.Succeeded);
            Assert.False(run.Steps[4].Result!.Succeeded);
            Assert.Contains("WScript.Shell", run.Steps[4].Result!.Error, StringComparison.Ordinal);
        }
    }
}
