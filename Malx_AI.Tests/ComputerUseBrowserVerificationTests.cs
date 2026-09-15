using Malx_AI.ComputerUse;
using Xunit;

namespace Malx_AI.Tests
{
    public class ComputerUseBrowserVerificationTests
    {
        [Fact]
        public void NavigationConfirmation_RejectsMatchingUrlOnErrorPage()
        {
            var state = new ComputerUseBrowserState
            {
                Address = "https://github.com/example/Axiom",
                TabTitles = ["Page not found - GitHub"],
                HasTabTelemetry = true,
                IsErrorPage = true
            };

            Assert.False(ComputerUseBrowserVerification.IsNavigationConfirmed("https://github.com/example/Axiom", state, out string reason));
            Assert.Contains("error", reason, System.StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void NavigationConfirmation_RequiresExpectedHostAndPath()
        {
            var state = new ComputerUseBrowserState
            {
                Address = "https://github.com/other/Axiom",
                TabTitles = ["other/Axiom"],
                HasTabTelemetry = true,
                IsErrorPage = false
            };

            Assert.False(ComputerUseBrowserVerification.IsNavigationConfirmed("https://github.com/owner/Axiom", state, out _));
        }

        [Fact]
        public void NewTabConfirmation_RequiresAnObservedTabIncrease()
        {
            Assert.False(ComputerUseBrowserVerification.IsNewTabConfirmed(1, new ComputerUseBrowserState { TabTitles = ["one"], HasTabTelemetry = true }));
            Assert.True(ComputerUseBrowserVerification.IsNewTabConfirmed(1, new ComputerUseBrowserState { TabTitles = ["one", "New tab"], HasTabTelemetry = true }));
            Assert.False(ComputerUseBrowserVerification.IsNewTabConfirmed(1, new ComputerUseBrowserState { TabTitles = ["one", "New tab"] }));
        }
    }
}
