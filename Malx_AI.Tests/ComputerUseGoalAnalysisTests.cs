using Malx_AI.ComputerUse;
using Xunit;

namespace Malx_AI.Tests
{
    public class ComputerUseGoalAnalysisTests
    {
        [Theory]
        [InlineData("Open my own GitHub repo called 'Axiom'.", "Axiom")]
        [InlineData("go to my github repository named demo-app", "demo-app")]
        [InlineData("open up microsoft edge, go to my github repo 'Axiom', then make a new tab and take me to youtube.", "Axiom")]
        [InlineData("Open my GitHub repository \"another-project\"", "another-project")]
        public void FindsRequestedGitHubRepositoryWithoutAnOwner(string goal, string expected)
        {
            Assert.True(ComputerUseGoalAnalysis.TryGetRequestedOwnedGitHubRepository(goal, out string repository));
            Assert.Equal(expected, repository);
        }

        [Fact]
        public void DoesNotTreatARepositoryWithAnExplicitOwnerAsMyRepository()
        {
            Assert.False(ComputerUseGoalAnalysis.TryGetRequestedOwnedGitHubRepository("Open github.com/someone/Axiom", out _));
        }

        [Fact]
        public void DoesNotTreatConnectingWordsAsARepositoryName()
            => Assert.False(ComputerUseGoalAnalysis.TryGetRequestedOwnedGitHubRepository("Open my github repo and then another tab", out _));
    }
}
