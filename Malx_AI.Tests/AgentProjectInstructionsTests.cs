using System;
using System.IO;
using Malx_AI.Agent;
using Xunit;

namespace Malx_AI.Tests
{
    public class AgentProjectInstructionsTests : IDisposable
    {
        private readonly string _folder = Path.Combine(Path.GetTempPath(), "AxiomProjectInstructions", Guid.NewGuid().ToString("N"));

        public AgentProjectInstructionsTests() => Directory.CreateDirectory(_folder);

        public void Dispose()
        {
            if (Directory.Exists(_folder))
                Directory.Delete(_folder, recursive: true);
            GC.SuppressFinalize(this);
        }

        [Fact]
        public void AgentsMdIsPreferredAndFramedAsProjectData()
        {
            File.WriteAllText(Path.Combine(_folder, "AGENTS.md"), "# Build\n- Run npm test before finishing.");
            File.WriteAllText(Path.Combine(_folder, "CLAUDE.md"), "# Other\n- Not this one.");

            string prompt = AgentPromptBuilder.Build(AgentTier.Full, AgentScope.Folder(_folder), AgentApprovalMode.Auto, nativeToolCalling: true);

            Assert.Contains("[PROJECT INSTRUCTIONS from AGENTS.md", prompt);
            Assert.Contains("Run npm test before finishing.", prompt);
            Assert.DoesNotContain("Not this one.", prompt);
            Assert.Contains("never override the user's request", prompt);
            Assert.True(prompt.IndexOf("[PROJECT INSTRUCTIONS", StringComparison.Ordinal) > prompt.IndexOf("[SESSION CONTEXT]", StringComparison.Ordinal),
                "Project text must stay in the per-session tail so the invariant prefix keeps its prompt cache.");
        }

        [Fact]
        public void ClaudeMdIsTheFallback()
        {
            File.WriteAllText(Path.Combine(_folder, "CLAUDE.md"), "- Use tabs.");
            Assert.Contains("Use tabs.", AgentProjectInstructions.Load(AgentScope.Folder(_folder), AgentTier.Full));
        }

        [Fact]
        public void SmallModelsGetOnlyHeadingsAndBulletsAndTinyModelsGetNothing()
        {
            File.WriteAllText(Path.Combine(_folder, "AGENTS.md"), "# Rules\nThis paragraph explains history at length.\n- Keep files small.\n1. Test first.");

            string compact = AgentProjectInstructions.Load(AgentScope.Folder(_folder), AgentTier.Compact)!;
            Assert.Contains("- Keep files small.", compact);
            Assert.Contains("1. Test first.", compact);
            Assert.DoesNotContain("explains history", compact);
            Assert.Null(AgentProjectInstructions.Load(AgentScope.Folder(_folder), AgentTier.Micro));
        }

        [Fact]
        public void LongFilesAreCappedWithAPointerToTheRest()
        {
            File.WriteAllText(Path.Combine(_folder, "AGENTS.md"), string.Concat(System.Linq.Enumerable.Repeat("- a rule that is repeated\n", 600)));
            string block = AgentProjectInstructions.Load(AgentScope.Folder(_folder), AgentTier.Full)!;
            Assert.True(block.Length < AgentProjectInstructions.FullTierMaxChars + 600);
            Assert.Contains("continues; read it with read_file", block);
        }

        [Fact]
        public void TheOffSwitchAndAFolderWithoutAFileAddNothing()
        {
            Assert.DoesNotContain("[PROJECT INSTRUCTIONS", AgentPromptBuilder.Build(AgentTier.Full, AgentScope.Folder(_folder), AgentApprovalMode.Auto));
            File.WriteAllText(Path.Combine(_folder, "AGENTS.md"), "- rule");
            Assert.DoesNotContain("[PROJECT INSTRUCTIONS", AgentPromptBuilder.Build(AgentTier.Full, AgentScope.Folder(_folder), AgentApprovalMode.Auto, includeProjectInstructions: false));
        }
    }
}
