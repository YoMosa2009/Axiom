using Xunit;

namespace Malx_AI.Tests
{
    public sealed class WorkplaceSessionTitleGeneratorTests
    {
        [Theory]
        [InlineData(null, true)]
        [InlineData("", true)]
        [InlineData("Workplace 1", true)]
        [InlineData("Workplace 12", true)]
        [InlineData("New session", true)]
        [InlineData("Fix WebView2 dark background", false)]
        public void IsPlaceholderName_DetectsGenericNumberedTitles(string? title, bool expected)
        {
            Assert.Equal(expected, WorkplaceSessionTitleGenerator.IsPlaceholderName(title));
        }

        [Fact]
        public void GenerateTitle_ProducesConciseReadableTitleAndShortLabel()
        {
            string title = WorkplaceSessionTitleGenerator.GenerateTitle(
                "Can you please help me refactor the login controller and add unit tests for token expiry?",
                @"C:\Users\user\source\repos\Axiom");

            Assert.DoesNotContain("Workplace 1", title);
            Assert.True(title.Length <= 42);
            Assert.StartsWith("Refactor", title, System.StringComparison.OrdinalIgnoreCase);

            Assert.Equal("Axiom", WorkplaceSessionTitleGenerator.ShortProjectLabel(@"C:\Users\user\source\repos\Axiom"));
            Assert.Equal("g:", WorkplaceSessionTitleGenerator.ShortProjectLabel(@"G:\"));
        }
    }
}
