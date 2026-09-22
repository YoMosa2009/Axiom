using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Malx_AI;
using Malx_AI.Agent;
using Xunit;

namespace Malx_AI.Tests
{
    [Collection("ProcessLifecycleCollection")]
    public class AgentContinuationAndScalingTests : IDisposable
    {
        private readonly string _folder = Path.Combine(Path.GetTempPath(), "AxiomContinuationTests", Guid.NewGuid().ToString("N"));

        public AgentContinuationAndScalingTests() => Directory.CreateDirectory(_folder);

        public void Dispose()
        {
            if (Directory.Exists(_folder))
                Directory.Delete(_folder, recursive: true);
        }

        [Fact]
        public void MaxStepsFor_ScalesUltraByALot()
        {
            // Cloud / 10B+ on Ultra should be 128 steps (increased from 24)
            int ultraSteps = AgentPromptBuilder.MaxStepsFor(AgentTier.Full, EffortLevel.Ultra, null, isCloudMode: true);
            Assert.Equal(128, ultraSteps);

            // Relative increases across efforts for Cloud / 10B+
            Assert.Equal(20, AgentPromptBuilder.MaxStepsFor(AgentTier.Full, EffortLevel.Light, null, isCloudMode: true));
            Assert.Equal(36, AgentPromptBuilder.MaxStepsFor(AgentTier.Full, EffortLevel.Medium, null, isCloudMode: true));
            Assert.Equal(60, AgentPromptBuilder.MaxStepsFor(AgentTier.Full, EffortLevel.High, null, isCloudMode: true));
            Assert.Equal(90, AgentPromptBuilder.MaxStepsFor(AgentTier.Full, EffortLevel.ExtraHigh, null, isCloudMode: true));
            Assert.Equal(128, AgentPromptBuilder.MaxStepsFor(AgentTier.Full, EffortLevel.Ultra, null, isCloudMode: true));
        }

        [Fact]
        public void MaxStepsFor_ScalesAcrossAllModelSizeClasses()
        {
            var sub1B = new LocalModelCapabilityProfile { SizeClass = LocalModelSizeClass.SubOneB };
            var compact1To4B = new LocalModelCapabilityProfile { SizeClass = LocalModelSizeClass.OneToFourB };
            var mid4To10B = new LocalModelCapabilityProfile { SizeClass = LocalModelSizeClass.FourToTenB };
            var large10BPlus = new LocalModelCapabilityProfile { SizeClass = LocalModelSizeClass.TenBPlus };

            // Sub 1B
            Assert.Equal(4, AgentPromptBuilder.MaxStepsFor(AgentTier.Micro, EffortLevel.Light, sub1B, false));
            Assert.Equal(8, AgentPromptBuilder.MaxStepsFor(AgentTier.Micro, EffortLevel.Medium, sub1B, false));
            Assert.Equal(12, AgentPromptBuilder.MaxStepsFor(AgentTier.Micro, EffortLevel.High, sub1B, false));
            Assert.Equal(18, AgentPromptBuilder.MaxStepsFor(AgentTier.Micro, EffortLevel.ExtraHigh, sub1B, false));
            Assert.Equal(26, AgentPromptBuilder.MaxStepsFor(AgentTier.Micro, EffortLevel.Ultra, sub1B, false));

            // 1B-4B
            Assert.Equal(8, AgentPromptBuilder.MaxStepsFor(AgentTier.Compact, EffortLevel.Light, compact1To4B, false));
            Assert.Equal(14, AgentPromptBuilder.MaxStepsFor(AgentTier.Compact, EffortLevel.Medium, compact1To4B, false));
            Assert.Equal(24, AgentPromptBuilder.MaxStepsFor(AgentTier.Compact, EffortLevel.High, compact1To4B, false));
            Assert.Equal(36, AgentPromptBuilder.MaxStepsFor(AgentTier.Compact, EffortLevel.ExtraHigh, compact1To4B, false));
            Assert.Equal(52, AgentPromptBuilder.MaxStepsFor(AgentTier.Compact, EffortLevel.Ultra, compact1To4B, false));

            // 4B-10B
            Assert.Equal(14, AgentPromptBuilder.MaxStepsFor(AgentTier.Full, EffortLevel.Light, mid4To10B, false));
            Assert.Equal(24, AgentPromptBuilder.MaxStepsFor(AgentTier.Full, EffortLevel.Medium, mid4To10B, false));
            Assert.Equal(40, AgentPromptBuilder.MaxStepsFor(AgentTier.Full, EffortLevel.High, mid4To10B, false));
            Assert.Equal(64, AgentPromptBuilder.MaxStepsFor(AgentTier.Full, EffortLevel.ExtraHigh, mid4To10B, false));
            Assert.Equal(96, AgentPromptBuilder.MaxStepsFor(AgentTier.Full, EffortLevel.Ultra, mid4To10B, false));

            // 10B+
            Assert.Equal(20, AgentPromptBuilder.MaxStepsFor(AgentTier.Full, EffortLevel.Light, large10BPlus, false));
            Assert.Equal(36, AgentPromptBuilder.MaxStepsFor(AgentTier.Full, EffortLevel.Medium, large10BPlus, false));
            Assert.Equal(60, AgentPromptBuilder.MaxStepsFor(AgentTier.Full, EffortLevel.High, large10BPlus, false));
            Assert.Equal(90, AgentPromptBuilder.MaxStepsFor(AgentTier.Full, EffortLevel.ExtraHigh, large10BPlus, false));
            Assert.Equal(128, AgentPromptBuilder.MaxStepsFor(AgentTier.Full, EffortLevel.Ultra, large10BPlus, false));
        }

        [Theory]
        [InlineData("continue", true)]
        [InlineData("Continue", true)]
        [InlineData("continue.", true)]
        [InlineData("keep going", true)]
        [InlineData("proceed", true)]
        [InlineData("go on", true)]
        [InlineData("go ahead", true)]
        [InlineData("yes", true)]
        [InlineData("yes please", true)]
        [InlineData("please continue", true)]
        [InlineData("finish it", true)]
        [InlineData("resume", true)]
        [InlineData("make a simple tetris game", false)]
        [InlineData("run python tetris.py and test it", false)]
        [InlineData("what files are here", false)]
        public void ContinuationPhrase_Detection(string phrase, bool expected)
        {
            Assert.Equal(expected, AgentContextManager.IsContinuationPhrase(phrase));
        }

        [Fact]
        public void ObservationCompaction_PreservesRecentAndSummarizesOlder()
        {
            var exchanges = new List<AgentExchange>();
            for (int i = 0; i < 10; i++)
            {
                var call = new AgentToolCall(
                    AgentToolNames.ReadFile,
                    new Dictionary<string, string> { ["path"] = $"file_{i}.txt" });
                string longObs = $"1\tLine one\n2\tLine two\n{new string('x', 500)}";
                exchanges.Add(new AgentExchange(call, longObs));
            }

            var compacted = AgentContextManager.CompactExchanges(exchanges, recentKeepCount: 5);
            Assert.Equal(10, compacted.Count);

            // The first 5 should be compacted summaries
            for (int i = 0; i < 5; i++)
            {
                Assert.StartsWith("[Read file_", compacted[i].Observation);
                Assert.True(compacted[i].Observation.Length < 100);
            }

            // The last 5 should preserve full observation text
            for (int i = 5; i < 10; i++)
            {
                Assert.Contains("Line one", compacted[i].Observation);
                Assert.Contains(new string('x', 100), compacted[i].Observation);
            }
        }

        [Fact]
        public void BuildContinuationGoal_PreservesContext()
        {
            string originalGoal = "make me a simple tetris game that i can play.";
            var touchedFiles = new HashSet<string> { @"C:\Users\user\tetris.py" };
            string lastError = "IndexError: list index out of range";

            string goal = AgentContextManager.BuildContinuationGoal(
                originalGoal,
                "continue",
                touchedFiles,
                lastError);

            Assert.Contains("[CONTINUING ACTIVE TASK]", goal);
            Assert.Contains("tetris.py", goal);
            Assert.Contains(originalGoal, goal);
            Assert.Contains(lastError, goal);
            Assert.Contains("Resume execution from your current progress", goal);
        }

        [Fact]
        public void ArgumentAliasing_ResolvesSynonyms()
        {
            // 'file' resolves to 'path'
            var callFile = new AgentToolCall("read_file", new Dictionary<string, string> { ["file"] = "tetris.py" });
            Assert.Equal("tetris.py", callFile.Arg("path"));

            // 'cmd' resolves to 'command'
            var callCmd = new AgentToolCall("run_command", new Dictionary<string, string> { ["cmd"] = "python tetris.py" });
            Assert.Equal("python tetris.py", callCmd.Arg("command"));

            // 'query' resolves to 'pattern'
            var callQuery = new AgentToolCall("search_text", new Dictionary<string, string> { ["query"] = "Clock" });
            Assert.Equal("Clock", callQuery.Arg("pattern"));

            // 'old_text' resolves to 'old_string'
            var callEdit = new AgentToolCall("edit_file", new Dictionary<string, string>
            {
                ["file"] = "tetris.py",
                ["old_text"] = "get_raw_time",
                ["new_text"] = "get_rawtime"
            });
            Assert.Equal("tetris.py", callEdit.Arg("path"));
            Assert.Equal("get_raw_time", callEdit.Arg("old_string"));
            Assert.Equal("get_rawtime", callEdit.Arg("new_string"));
        }

        [Fact]
        public void ToolCallParser_SupportsToolCallTagAndStringifiedArguments()
        {
            string xmlToolCall = "<tool_call>\n{\"tool\": \"run_command\", \"arguments\": \"{\\\"command\\\": \\\"python --version\\\"}\"}\n</tool_call>";
            bool parsed = AgentToolCallParser.TryParse(xmlToolCall, out AgentToolCall? call, out string? error);

            Assert.True(parsed);
            Assert.NotNull(call);
            Assert.Equal("run_command", call!.Tool);
            Assert.Equal("python --version", call.Arg("command"));
            Assert.Null(error);
        }

        [Fact]
        public async Task EditFile_NormalizesCrlfAndLfLineEndings()
        {
            string file = Path.Combine(_folder, "game.py");
            // File written with Windows CRLF
            File.WriteAllText(file, "def main():\r\n    time = clock.get_raw_time()\r\n    return time\r\n");

            var executor = new AgentToolExecutor(AgentScope.Folder(_folder));
            // Tool call uses Unix LF in old_string and new_string
            var call = new AgentToolCall("edit_file", new Dictionary<string, string>
            {
                ["path"] = file,
                ["old_string"] = "def main():\n    time = clock.get_raw_time()",
                ["new_string"] = "def main():\n    time = clock.get_rawtime()"
            });

            AgentToolResult result = await executor.ExecuteAsync(call, CancellationToken.None);
            Assert.True(result.Succeeded, result.Error);

            string content = File.ReadAllText(file);
            Assert.Contains("get_rawtime()", content);
            Assert.DoesNotContain("get_raw_time()", content);
        }

        [Fact]
        public async Task AgentSession_PreservesInitialHistoryAndAllowList()
        {
            var scriptedModel = new ScriptedAgentModel(
                AgentModelReply.Tool(new AgentToolCall("read_file", new Dictionary<string, string> { ["path"] = "test.txt" })),
                AgentModelReply.Answer("Task complete after resume.")
            );

            File.WriteAllText(Path.Combine(_folder, "test.txt"), "hello world");
            var executor = new AgentToolExecutor(AgentScope.Folder(_folder));
            var session = new AgentSession(AgentScope.Folder(_folder), 10, executor);

            var priorExchange = new AgentExchange(
                new AgentToolCall("write_file", new Dictionary<string, string> { ["path"] = "test.txt", ["content"] = "hello world" }),
                "Wrote 11 characters to test.txt.");

            var result = await session.RunAsync(
                "Continue task",
                AgentApprovalMode.Auto,
                scriptedModel,
                (_, _, _) => Task.FromResult(AgentApprovalOutcome.Approve),
                _ => { },
                CancellationToken.None,
                initialHistory: [priorExchange],
                initialAllowList: ["python test.py"]);

            Assert.Equal("Task complete after resume.", result.FinalMessage);
            Assert.False(result.StoppedOnStepLimit);
            Assert.NotNull(result.Exchanges);
            // Result exchanges should include the prior exchange plus the new turn
            Assert.True(result.Exchanges!.Count >= 2);
            Assert.Contains("python test.py", session.SessionAllowList);
        }

        [Fact]
        public void UpdateReleaseParser_ParsesV196ReleaseCorrectlyForOta()
        {
            string releaseJson = """
            {
              "tag_name": "v1.9.6",
              "name": "Axiom V1.9.6",
              "draft": false,
              "prerelease": false,
              "html_url": "https://github.com/YoMosa2009/Axiom/releases/tag/v1.9.6",
              "body": "Agent continuation, dynamic step scaling, tool execution robustness, and Workplace attachments.",
              "published_at": "2026-09-21T21:52:12Z",
              "assets": [
                {
                  "name": "Axiom-v1.9.6-win-x64-clean.zip",
                  "browser_download_url": "https://github.com/YoMosa2009/Axiom/releases/download/v1.9.6/Axiom-v1.9.6-win-x64-clean.zip",
                  "size": 415111779,
                  "digest": "sha256:0e414f1ac056b08981bcb7adb6f9bad748e2bdf2ca84b3477efb5708415a095f"
                }
              ]
            }
            """;

            var result = UpdateReleaseParser.Parse(releaseJson, new Version(1, 9, 5));
            Assert.NotNull(result);
            Assert.Equal("v1.9.6", result.LatestVersionTag);
            Assert.Equal(new Version(1, 9, 6, 0), result.LatestVersion);
            Assert.True(result.IsNewerVersionAvailable);
            Assert.True(result.HasPackageAsset);
            Assert.Equal(UpdatePackageKind.Zip, result.PackageKind);
            Assert.Equal("Axiom-v1.9.6-win-x64-clean.zip", result.PackageFileName);
            Assert.Equal("0e414f1ac056b08981bcb7adb6f9bad748e2bdf2ca84b3477efb5708415a095f", result.PackageSha256);
            Assert.Equal(415111779, result.PackageSizeBytes);

            // Once updated to 1.9.6, newer version available should be false
            var currentResult = UpdateReleaseParser.Parse(releaseJson, new Version(1, 9, 6));
            Assert.NotNull(currentResult);
            Assert.False(currentResult.IsNewerVersionAvailable);
        }

        [Fact]
        public void UpdateReleaseParser_ParsesV197ReleaseCorrectlyForOta()
        {
            string releaseJson = """
            {
              "tag_name": "v1.9.7",
              "name": "Axiom V1.9.7",
              "draft": false,
              "prerelease": false,
              "html_url": "https://github.com/YoMosa2009/Axiom/releases/tag/v1.9.7",
              "body": "Agent Access anti-redundancy, Council Mode synergy, Workplace token tracking accuracy, Enter-to-send, and grounded tool feedback.",
              "published_at": "2026-09-21T23:45:00Z",
              "assets": [
                {
                  "name": "Axiom-v1.9.7-win-x64-clean.zip",
                  "browser_download_url": "https://github.com/YoMosa2009/Axiom/releases/download/v1.9.7/Axiom-v1.9.7-win-x64-clean.zip",
                  "size": 415000000,
                  "digest": "sha256:1111111111111111111111111111111111111111111111111111111111111111"
                }
              ]
            }
            """;

            var result = UpdateReleaseParser.Parse(releaseJson, new Version(1, 9, 6));
            Assert.NotNull(result);
            Assert.Equal("v1.9.7", result.LatestVersionTag);
            Assert.Equal(new Version(1, 9, 7, 0), result.LatestVersion);
            Assert.True(result.IsNewerVersionAvailable);
            Assert.True(result.HasPackageAsset);
            Assert.Equal(UpdatePackageKind.Zip, result.PackageKind);
            Assert.Equal("Axiom-v1.9.7-win-x64-clean.zip", result.PackageFileName);

            // Once updated to 1.9.7, newer version available should be false
            var currentResult = UpdateReleaseParser.Parse(releaseJson, new Version(1, 9, 7));
            Assert.NotNull(currentResult);
            Assert.False(currentResult.IsNewerVersionAvailable);
        }

        [Fact]
        public void ExtractVerifiedDependencies_ExtractsFromSuccessfulChecksAndInstalls()
        {
            var exchanges = new List<AgentExchange>
            {
                new(new AgentToolCall("run_command", new Dictionary<string, string> { ["command"] = "python -c \"import pygame\"" }), "[Exit code: 0 | Duration: 120ms]\n(Command completed successfully with no standard output)"),
                new(new AgentToolCall("run_command", new Dictionary<string, string> { ["command"] = "pip show numpy" }), "[Exit code: 0 | Duration: 80ms]\nName: numpy\nVersion: 1.24.0"),
                new(new AgentToolCall("run_command", new Dictionary<string, string> { ["command"] = "python -c \"import torch\"" }), "[Exit code: 1 | Duration: 95ms]\nModuleNotFoundError: No module named 'torch'"),
                new(new AgentToolCall("run_command", new Dictionary<string, string> { ["command"] = "pip install sounddevice" }), "[Exit code: 0 | Duration: 1500ms]\nSuccessfully installed sounddevice-0.4.6")
            };

            HashSet<string> verified = AgentContextManager.ExtractVerifiedDependencies(exchanges);
            Assert.Contains("pygame", verified);
            Assert.Contains("numpy", verified);
            Assert.Contains("sounddevice", verified);
            Assert.DoesNotContain("torch", verified);
        }

        [Fact]
        public void BuildContinuationGoal_IncludesVerifiedDependenciesAndForbidsReinstall()
        {
            string goal = AgentContextManager.BuildContinuationGoal(
                "make me a simple tetris game",
                "make the blocks blue and add pause button",
                ["tetris.py"],
                "Tetris game created and running",
                ["pygame"]);

            Assert.Contains("[CONTINUING ACTIVE TASK]", goal);
            Assert.Contains("Original task: make me a simple tetris game", goal);
            Assert.Contains("Files touched so far: tetris.py", goal);
            Assert.Contains("Verified packages/tools already installed and working: pygame", goal);
            Assert.Contains("User follow-up / change request: make the blocks blue and add pause button", goal);
            Assert.Contains("DO NOT re-install packages or re-download dependencies", goal);
        }

        [Theory]
        [InlineData("new task: create a snake game", true)]
        [InlineData("new project", true)]
        [InlineData("start fresh", true)]
        [InlineData("reset agent", true)]
        [InlineData("clear agent", true)]
        [InlineData("make the blocks blue", false)]
        [InlineData("change the colors to neon", false)]
        [InlineData("fix the collision bug", false)]
        [InlineData("continue", false)]
        public void IsNewTaskPhrase_DistinguishesNewProjectFromFollowUp(string text, bool expected)
        {
            Assert.Equal(expected, AgentContextManager.IsNewTaskPhrase(text));
        }

        [Fact]
        public async Task AgentToolExecutor_WriteAndEditFile_ReturnsDiskVerification()
        {
            var executor = new AgentToolExecutor(AgentScope.Folder(_folder));
            string filePath = Path.Combine(_folder, "test_verify.py");

            var writeCall = new AgentToolCall("write_file", new Dictionary<string, string>
            {
                ["path"] = filePath,
                ["content"] = "import pygame\nprint('hello')\n"
            });

            AgentToolResult writeResult = await executor.ExecuteAsync(writeCall, CancellationToken.None);
            Assert.True(writeResult.Succeeded);
            Assert.Contains("Verified on disk", writeResult.Output);
            Assert.Contains("bytes", writeResult.Output);
            Assert.Contains("lines", writeResult.Output);

            var editCall = new AgentToolCall("edit_file", new Dictionary<string, string>
            {
                ["path"] = filePath,
                ["old_string"] = "print('hello')",
                ["new_string"] = "print('verified')"
            });

            AgentToolResult editResult = await executor.ExecuteAsync(editCall, CancellationToken.None);
            Assert.True(editResult.Succeeded);
            Assert.Contains("Verified on disk", editResult.Output);
            Assert.Contains("print('verified')", File.ReadAllText(filePath));
        }

        [Fact]
        public async Task AgentToolExecutor_RunCommand_FormatsExitCodeAndDuration()
        {
            var executor = new AgentToolExecutor(AgentScope.Folder(_folder));
            var call = new AgentToolCall("run_command", new Dictionary<string, string>
            {
                ["command"] = "Write-Output 'GroundingCheck'"
            });

            AgentToolResult result = await executor.ExecuteAsync(call, CancellationToken.None);
            Assert.True(result.Succeeded, result.Error ?? result.Output);
            Assert.Contains("[Exit code: 0", result.Output);
            Assert.Contains("Duration:", result.Output);
            Assert.Contains("GroundingCheck", result.Output);
        }

        [Fact]
        public void UpdateReleaseParser_ParsesV198ReleaseCorrectlyForOta()
        {
            string releaseJson = """
            {
              "tag_name": "v1.9.8",
              "name": "Axiom V1.9.8",
              "draft": false,
              "prerelease": false,
              "html_url": "https://github.com/YoMosa2009/Axiom/releases/tag/v1.9.8",
              "body": "Workplace attachment composer clearance, Ctrl+V image/file pasting, Agent Access design vision, shell execution normalization, and repetitive command loop guard.",
              "published_at": "2026-09-22T03:00:00Z",
              "assets": [
                {
                  "name": "Axiom-v1.9.8-win-x64-clean.zip",
                  "browser_download_url": "https://github.com/YoMosa2009/Axiom/releases/download/v1.9.8/Axiom-v1.9.8-win-x64-clean.zip",
                  "size": 415000000,
                  "digest": "sha256:2222222222222222222222222222222222222222222222222222222222222222"
                }
              ]
            }
            """;

            var result = UpdateReleaseParser.Parse(releaseJson, new Version(1, 9, 7));
            Assert.NotNull(result);
            Assert.Equal("v1.9.8", result.LatestVersionTag);
            Assert.Equal(new Version(1, 9, 8, 0), result.LatestVersion);
            Assert.True(result.IsNewerVersionAvailable);
            Assert.True(result.HasPackageAsset);
            Assert.Equal(UpdatePackageKind.Zip, result.PackageKind);
            Assert.Equal("Axiom-v1.9.8-win-x64-clean.zip", result.PackageFileName);

            var currentResult = UpdateReleaseParser.Parse(releaseJson, new Version(1, 9, 8));
            Assert.NotNull(currentResult);
            Assert.False(currentResult.IsNewerVersionAvailable);
        }

        [Theory]
        [InlineData("powershell -Command \"New-Item -ItemType Directory -Path 'F:\\WebsiteProject'\"", "New-Item -ItemType Directory -Path 'F:\\WebsiteProject'")]
        [InlineData("powershell.exe -NoProfile -ExecutionPolicy Bypass -Command \"Write-Output 'Hello'\"", "Write-Output 'Hello'")]
        [InlineData("powershell -c 'Get-Process'", "Get-Process")]
        [InlineData("cmd /c \"echo Hello\"", "echo Hello")]
        [InlineData("Write-Output 'Direct'", "Write-Output 'Direct'")]
        public void NormalizeCommand_UnwrapsRedundantShellWrappers(string input, string expected)
        {
            string normalized = AgentToolExecutor.NormalizeCommand(input);
            Assert.Equal(expected, normalized);
        }

        [Fact]
        public void IsDirectoryAlreadyExistsScenario_DetectsDirectoryExist()
        {
            string cmd = "New-Item -ItemType Directory -Path 'F:\\WebsiteProject'";
            string stderr = "New-Item : An item with the specified name F:\\WebsiteProject already exists.\nAt line:1 char:1\nCategoryInfo : ResourceExists: (F:\\WebsiteProject:String) [New-Item], IOException\nFullyQualifiedErrorId : DirectoryExist,Microsoft.PowerShell.Commands.NewItemCommand";
            bool detected = AgentToolExecutor.IsDirectoryAlreadyExistsScenario(cmd, string.Empty, stderr);
            Assert.True(detected);

            bool notDirectory = AgentToolExecutor.IsDirectoryAlreadyExistsScenario("python script.py", string.Empty, "ImportError: no module");
            Assert.False(notDirectory);
        }

        [Fact]
        public void AgentSession_AreCallsIdentical_ComparesAccurately()
        {
            var call1 = new AgentToolCall("run_command", new Dictionary<string, string> { ["command"] = "dir" });
            var call2 = new AgentToolCall("run_command", new Dictionary<string, string> { ["command"] = "dir" });
            var call3 = new AgentToolCall("run_command", new Dictionary<string, string> { ["command"] = "ls" });
            var call4 = new AgentToolCall("read_file", new Dictionary<string, string> { ["path"] = "a.txt" });

            Assert.True(AgentSession.AreCallsIdentical(call1, call2));
            Assert.False(AgentSession.AreCallsIdentical(call1, call3));
            Assert.False(AgentSession.AreCallsIdentical(call1, call4));
            Assert.False(AgentSession.AreCallsIdentical(call1, null));
        }

        [Fact]
        public async Task AgentSession_RepetitiveToolCallGuard_BlocksOnThirdRepetition()
        {
            var mockModel = new ScriptedAgentModel(
                AgentModelReply.Tool(new AgentToolCall("run_command", new Dictionary<string, string> { ["command"] = "New-Item -ItemType Directory -Path 'test'" })),
                AgentModelReply.Tool(new AgentToolCall("run_command", new Dictionary<string, string> { ["command"] = "New-Item -ItemType Directory -Path 'test'" })),
                AgentModelReply.Tool(new AgentToolCall("run_command", new Dictionary<string, string> { ["command"] = "New-Item -ItemType Directory -Path 'test'" })),
                AgentModelReply.Answer("Done.")
            );

            var executor = new AgentToolExecutor(AgentScope.Folder(_folder));
            var session = new AgentSession(AgentScope.Folder(_folder), maxSteps: 10, executor: executor);

            AgentRunResult result = await session.RunAsync(
                "create directory test",
                AgentApprovalMode.Auto,
                mockModel,
                (_, _, _) => Task.FromResult(AgentApprovalOutcome.Approve),
                _ => { },
                CancellationToken.None);

            Assert.False(result.Cancelled);
            // Verify step 3 was blocked by the repetitive tool call guard
            Assert.Contains(result.Steps, s => s.Note == "repetition blocked" && s.Result?.Error?.Contains("Repetitive action blocked") == true);
        }
    }
}
