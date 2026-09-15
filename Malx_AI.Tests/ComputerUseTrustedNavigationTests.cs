using Malx_AI.ComputerUse;
using Xunit;

namespace Malx_AI.Tests
{
    public class ComputerUseTrustedNavigationTests
    {
        [Fact]
        public void WorkspaceUrl_ProvidesAnExactGitHubDestination()
        {
            Assert.True(ComputerUseTrustedNavigation.TryGetGitHubRepositoryUrl(
                "https://github.com/example/Axiom.git",
                "Axiom",
                out string url));

            Assert.Equal("https://github.com/example/Axiom", url);
        }

        [Fact]
        public void RepositoryList_RejectsAmbiguousOwners()
        {
            const string list = "1. full_name=alpha/Axiom private=false default_branch=main url=https://github.com/alpha/Axiom\n" +
                                "2. full_name=beta/Axiom private=false default_branch=main url=https://github.com/beta/Axiom";

            Assert.False(ComputerUseTrustedNavigation.TryFindUniquePublicRepositoryUrl(list, "Axiom", out _));
        }

        [Fact]
        public void RepositoryList_UsesTheSinglePublicExactMatch()
        {
            const string list = "1. full_name=example/Axiom private=false default_branch=main url=https://github.com/example/Axiom description=test";

            Assert.True(ComputerUseTrustedNavigation.TryFindUniquePublicRepositoryUrl(list, "Axiom", out string url));
            Assert.Equal("https://github.com/example/Axiom", url);
        }

        [Fact]
        public void VerifiedDestination_BlocksAnOwnerSubstitution()
        {
            string context = ComputerUseTrustedNavigation.CreateContext("https://github.com/example/Axiom");

            Assert.True(ComputerUseTrustedNavigation.IsVerifiedDestinationAction(
                context,
                verifiedDestinationAlreadyReached: false,
                actionText: "https://github.com/guessed/Axiom",
                out string observation));
            Assert.Contains("differs", observation);
        }

        [Fact]
        public void VerifiedDestination_DoesNotBlockTheNextIndependentNavigation()
        {
            string context = ComputerUseTrustedNavigation.CreateContext("https://github.com/example/Axiom");

            Assert.False(ComputerUseTrustedNavigation.IsVerifiedDestinationAction(
                context,
                verifiedDestinationAlreadyReached: true,
                actionText: "https://www.youtube.com",
                out _));
        }

        [Fact]
        public void OwnedRepositoryWithoutEvidence_BlocksAGuessedUrl()
        {
            Assert.True(ComputerUseTrustedNavigation.IsUnverifiedOwnedRepositoryNavigation(
                "Open my GitHub repo called Axiom",
                string.Empty,
                "https://github.com/guessed/Axiom"));
        }
    }
}
