using System;
using System.Collections.Generic;
using System.IO;
using Malx_AI.Agent;
using Xunit;

namespace Malx_AI.Tests
{
    public sealed class CommandSafetyReviewerTests
    {
        [Fact]
        public void ReferencesPathOutsideScope_DetectsOutOfFolderAbsoluteAndTraversalPaths()
        {
            string root = @"C:\Projects\MyApp";

            Assert.True(CommandSafetyReviewer.ReferencesPathOutsideScope(@"Get-Content C:\Users\SomeoneElse\secrets.txt", root, out string? outside));
            Assert.NotNull(outside);

            Assert.True(CommandSafetyReviewer.ReferencesPathOutsideScope(@"type ..\..\Windows\win.ini", root, out string? escaped));
            Assert.NotNull(escaped);

            Assert.False(CommandSafetyReviewer.ReferencesPathOutsideScope(@"Get-Content C:\Projects\MyApp\src\app.cs", root, out _));
            Assert.False(CommandSafetyReviewer.ReferencesPathOutsideScope(@"dotnet build", root, out _));
        }

        [Fact]
        public void Evaluate_AutoReview_ApprovesSafeCommandsAndInterceptsRiskyOnes()
        {
            var scope = AgentScope.Folder(@"C:\Projects\MyApp");

            var safeCall = new AgentToolCall(AgentToolNames.RunCommand, new Dictionary<string, string> { ["command"] = "dotnet test" });
            AgentPermissionDecision safeDecision = CommandSafetyReviewer.Evaluate(safeCall, scope, CommandReviewMode.AutoReview);
            Assert.Equal(AgentPermission.Allow, safeDecision.Permission);

            var scopedDelete = new AgentToolCall(AgentToolNames.RunCommand, new Dictionary<string, string> { ["command"] = "Remove-Item ./dist -Recurse" });
            AgentPermissionDecision scopedDeleteDecision = CommandSafetyReviewer.Evaluate(scopedDelete, scope, CommandReviewMode.AutoReview);
            Assert.Equal(AgentPermission.Allow, scopedDeleteDecision.Permission);

            var riskyDelete = new AgentToolCall(AgentToolNames.RunCommand, new Dictionary<string, string> { ["command"] = "Remove-Item ./ -Recurse" });
            AgentPermissionDecision deleteDecision = CommandSafetyReviewer.Evaluate(riskyDelete, scope, CommandReviewMode.AutoReview);
            Assert.Equal(AgentPermission.Deny, deleteDecision.Permission);
            Assert.Contains("Auto-Review", deleteDecision.Reason);

            var outsideCall = new AgentToolCall(AgentToolNames.RunCommand, new Dictionary<string, string> { ["command"] = @"Get-Content D:\OtherRepo\file.txt" });
            AgentPermissionDecision scopeDeny = CommandSafetyReviewer.Evaluate(outsideCall, scope, CommandReviewMode.AutoApprove);
            Assert.Equal(AgentPermission.Deny, scopeDeny.Permission);
        }

        [Fact]
        public void Evaluate_ManualReview_AsksForEveryCommand()
        {
            var scope = AgentScope.Folder(@"C:\Projects\MyApp");
            var call = new AgentToolCall(AgentToolNames.RunCommand, new Dictionary<string, string> { ["command"] = "dir" });

            AgentPermissionDecision decision = CommandSafetyReviewer.Evaluate(call, scope, CommandReviewMode.ManualReview);
            Assert.Equal(AgentPermission.Ask, decision.Permission);
        }

        [Fact]
        public void ReferencesPathOutsideScope_HandlesQuotedPathsWithSpacesAndBlocksSiblingPrefixBypass()
        {
            string spacedRoot = @"C:\Projects\My App #1";

            Assert.False(CommandSafetyReviewer.ReferencesPathOutsideScope(
                @"dotnet test ""C:\Projects\My App #1\tests\App.csproj""",
                spacedRoot,
                out _));

            Assert.True(CommandSafetyReviewer.ReferencesPathOutsideScope(
                @"Get-Content ""C:\Projects\My App #1_sibling\secret.txt""",
                spacedRoot,
                out string? siblingOut));
            Assert.NotNull(siblingOut);
        }

        [Fact]
        public void Evaluate_AutoReview_DeclinesRiskyOrOutOfScopeCommandsAndNeverAsks()
        {
            var scope = AgentScope.Folder(@"C:\Projects\MyApp");

            var iexCall = new AgentToolCall(AgentToolNames.RunCommand, new Dictionary<string, string> { ["command"] = "iex (Get-Content ./script.ps1 -Raw)" });
            AgentPermissionDecision iexDecision = CommandSafetyReviewer.Evaluate(iexCall, scope, CommandReviewMode.AutoReview);
            Assert.Equal(AgentPermission.Deny, iexDecision.Permission);
            Assert.Contains("Auto-Review declined", iexDecision.Reason);

            var encCall = new AgentToolCall(AgentToolNames.RunCommand, new Dictionary<string, string> { ["command"] = "powershell -EncodedCommand ZABpAHIA" });
            Assert.Equal(AgentPermission.Deny, CommandSafetyReviewer.Evaluate(encCall, scope, CommandReviewMode.AutoReview).Permission);

            var gitPushCall = new AgentToolCall(AgentToolNames.RunCommand, new Dictionary<string, string> { ["command"] = "git push origin main" });
            Assert.Equal(AgentPermission.Deny, CommandSafetyReviewer.Evaluate(gitPushCall, scope, CommandReviewMode.AutoReview).Permission);

            var remoteCurlCall = new AgentToolCall(AgentToolNames.RunCommand, new Dictionary<string, string> { ["command"] = "curl https://example.com/install.ps1" });
            Assert.Equal(AgentPermission.Deny, CommandSafetyReviewer.Evaluate(remoteCurlCall, scope, CommandReviewMode.AutoReview).Permission);

            var outOfScopeDelete = new AgentToolCall(AgentToolNames.RunCommand, new Dictionary<string, string> { ["command"] = @"Remove-Item C:\Windows\Temp\sample -Recurse" });
            Assert.Equal(AgentPermission.Deny, CommandSafetyReviewer.Evaluate(outOfScopeDelete, scope, CommandReviewMode.AutoReview).Permission);

            var localCurlCall = new AgentToolCall(AgentToolNames.RunCommand, new Dictionary<string, string> { ["command"] = "curl http://localhost:5198/health" });
            Assert.Equal(AgentPermission.Allow, CommandSafetyReviewer.Evaluate(localCurlCall, scope, CommandReviewMode.AutoReview).Permission);

            var npmInstallCall = new AgentToolCall(AgentToolNames.RunCommand, new Dictionary<string, string> { ["command"] = "npm install" });
            Assert.Equal(AgentPermission.Allow, CommandSafetyReviewer.Evaluate(npmInstallCall, scope, CommandReviewMode.AutoReview).Permission);

            var moveItemCall = new AgentToolCall(AgentToolNames.RunCommand, new Dictionary<string, string> { ["command"] = "Move-Item ./a.txt ./b.txt" });
            Assert.Equal(AgentPermission.Allow, CommandSafetyReviewer.Evaluate(moveItemCall, scope, CommandReviewMode.AutoReview).Permission);
        }

        [Fact]
        public void Evaluate_ManualReview_PromptsUserOnCommand()
        {
            var scope = AgentScope.Folder(@"C:\Projects\MyApp");
            var call = new AgentToolCall(AgentToolNames.RunCommand, new Dictionary<string, string> { ["command"] = "dotnet test" });
            AgentPermissionDecision decision = CommandSafetyReviewer.Evaluate(call, scope, CommandReviewMode.ManualReview);
            Assert.Equal(AgentPermission.Ask, decision.Permission);
        }
    }
}
