using System.Collections.Generic;
using Malx_AI.Agent;
using Xunit;

namespace Malx_AI.Tests
{
    public class AgentPermissionPolicyTests
    {
        private static AgentToolCall Command(string command) =>
            new(AgentToolNames.RunCommand, new Dictionary<string, string> { ["command"] = command });

        private static AgentToolCall Write(string path) =>
            new(AgentToolNames.WriteFile, new Dictionary<string, string> { ["path"] = path, ["content"] = "x" });

        [Theory]
        [InlineData("format c:")]
        [InlineData("FORMAT D: /q")]
        [InlineData("diskpart")]
        [InlineData("mkfs.ext4 /dev/sda1")]
        [InlineData("rm -rf /")]
        [InlineData("rm -rf /*")]
        [InlineData("del /s /q C:\\")]
        [InlineData("rd /s /q D:\\")]
        [InlineData("bcdedit /set testsigning on")]
        [InlineData("shutdown /s /t 0")]
        [InlineData("Stop-Computer")]
        [InlineData("cipher /w:C")]
        public void CatastrophicCommandsAreBlockedInBothModes(string command)
        {
            foreach (AgentApprovalMode mode in new[] { AgentApprovalMode.Manual, AgentApprovalMode.Auto })
            {
                AgentPermissionDecision decision = AgentPermissionPolicy.Evaluate(Command(command), mode);
                Assert.Equal(AgentPermission.Deny, decision.Permission);
                Assert.Contains("Blocked", decision.Reason);
            }
        }

        [Theory]
        // The block list must not swallow ordinary work that merely looks similar.
        [InlineData("rm -rf ./build")]
        [InlineData("rm -rf node_modules")]
        [InlineData("rm -rf /home/me/project/dist")]
        [InlineData("del /q obj\\temp.txt")]
        [InlineData("git reset --hard")]
        [InlineData("dotnet build")]
        [InlineData("npm run format")]
        [InlineData("reg query HKLM\\Software")]
        public void OrdinaryCommandsAreNotBlocked(string command)
        {
            AgentPermissionDecision decision = AgentPermissionPolicy.Evaluate(Command(command), AgentApprovalMode.Auto);
            Assert.Equal(AgentPermission.Allow, decision.Permission);
        }

        [Fact]
        public void ManualModeAsksBeforeRunningAnyCommand()
        {
            AgentPermissionDecision decision = AgentPermissionPolicy.Evaluate(Command("dotnet build"), AgentApprovalMode.Manual);
            Assert.Equal(AgentPermission.Ask, decision.Permission);
        }

        [Fact]
        public void AutoModeRunsCommandsWithoutAsking()
        {
            AgentPermissionDecision decision = AgentPermissionPolicy.Evaluate(Command("dotnet build"), AgentApprovalMode.Auto);
            Assert.Equal(AgentPermission.Allow, decision.Permission);
        }

        [Fact]
        public void ReadsNeverRequireApproval()
        {
            var read = new AgentToolCall(AgentToolNames.ReadFile, new Dictionary<string, string> { ["path"] = "a.txt" });
            Assert.Equal(AgentPermission.Allow, AgentPermissionPolicy.Evaluate(read, AgentApprovalMode.Manual).Permission);
        }

        [Fact]
        public void ManualModeAsksBeforeWritingFiles()
        {
            Assert.Equal(AgentPermission.Ask, AgentPermissionPolicy.Evaluate(Write(@"C:\work\a.txt"), AgentApprovalMode.Manual).Permission);
        }

        [Theory]
        [InlineData(@"C:\Windows\System32\drivers\etc\hosts")]
        [InlineData(@"C:/Windows/System32/cmd.exe")]
        [InlineData(@"C:\Windows\SysWOW64\thing.dll")]
        public void SystemFoldersAreNeverWritable(string path)
        {
            foreach (AgentApprovalMode mode in new[] { AgentApprovalMode.Manual, AgentApprovalMode.Auto })
                Assert.Equal(AgentPermission.Deny, AgentPermissionPolicy.Evaluate(Write(path), mode).Permission);
        }

        [Fact]
        public void UnknownToolsAreRefused()
        {
            var call = new AgentToolCall("exfiltrate", new Dictionary<string, string>());
            Assert.Equal(AgentPermission.Deny, AgentPermissionPolicy.Evaluate(call, AgentApprovalMode.Auto).Permission);
        }

        [Fact]
        public void EmptyCommandsAreRefused()
        {
            Assert.Equal(AgentPermission.Deny, AgentPermissionPolicy.Evaluate(Command("   "), AgentApprovalMode.Auto).Permission);
        }

        [Fact]
        public void SessionAllowListCoversTheSameCommandWithMoreFlags()
        {
            var allowList = new List<string> { "git status" };
            Assert.Equal(
                AgentPermission.Allow,
                AgentPermissionPolicy.Evaluate(Command("git status --short"), AgentApprovalMode.Manual, allowList).Permission);
        }

        [Fact]
        public void SessionAllowListDoesNotLeakAcrossSubcommands()
        {
            // Approving "git status" must not quietly approve "git push --force".
            var allowList = new List<string> { "git status" };
            Assert.Equal(
                AgentPermission.Ask,
                AgentPermissionPolicy.Evaluate(Command("git push --force"), AgentApprovalMode.Manual, allowList).Permission);
        }

        [Fact]
        public void AllowListKeyKeepsTheSubcommand()
        {
            Assert.Equal("git status", AgentPermissionPolicy.AllowListKeyFor("git status --short -b"));
            Assert.Equal("dotnet", AgentPermissionPolicy.AllowListKeyFor("dotnet"));
        }

        [Fact]
        public void BlockedCommandsStayBlockedEvenIfAllowListed()
        {
            // A user can waive being asked; they cannot waive the hard block.
            var allowList = new List<string> { "format c:" };
            Assert.Equal(
                AgentPermission.Deny,
                AgentPermissionPolicy.Evaluate(Command("format c: /q"), AgentApprovalMode.Manual, allowList).Permission);
        }
    }
}
