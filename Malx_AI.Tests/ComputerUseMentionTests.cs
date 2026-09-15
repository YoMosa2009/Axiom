using Malx_AI.ComputerUse;
using Xunit;

namespace Malx_AI.Tests
{
    public class ComputerUseMentionTests
    {
        [Theory]
        [InlineData("@ComputerUse open notepad", true)]
        [InlineData("please @computer click the start menu", true)]
        [InlineData("@Computer_Use type hello", true)]
        [InlineData("do not use computer use", false)]
        [InlineData("@ProjectCanvas dashboard", false)]
        public void IsInvoked_DetectsWorkplaceHandles(string text, bool expected)
        {
            Assert.Equal(expected, ComputerUseMention.IsInvoked(text));
        }

        [Fact]
        public void StripMentions_LeavesTheGoal()
        {
            string stripped = ComputerUseMention.StripMentions("@ComputerUse open Notepad and type hello");
            Assert.Equal("open Notepad and type hello", stripped);
        }

        [Theory]
        [InlineData("", true)]
        [InlineData("comp", true)]
        [InlineData("Use", true)]
        [InlineData("gmail", false)]
        public void MatchesQuery_FiltersPicker(string query, bool expected)
        {
            Assert.Equal(expected, ComputerUseMention.MatchesQuery(query));
        }
    }
}
