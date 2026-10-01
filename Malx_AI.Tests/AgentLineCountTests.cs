using Malx_AI.Agent;
using Xunit;

namespace Malx_AI.Tests
{
    public class AgentLineCountTests
    {
        [Theory]
        [InlineData("a", 1)]
        [InlineData("a\n", 1)]
        [InlineData("a\nb", 2)]
        [InlineData("a\nb\n", 2)]
        [InlineData("a\r\nb\r\n", 2)]
        [InlineData("\n", 1)]
        [InlineData("", 0)]
        public void CountLines_Returns_Correct_Count(string text, int expected)
        {
            int actual = AgentToolExecutor.CountLines(text);
            Assert.Equal(expected, actual);
        }
    }
}
