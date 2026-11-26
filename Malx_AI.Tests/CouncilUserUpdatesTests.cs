using Xunit;

namespace Malx_AI.Tests
{
    public class CouncilUserUpdatesTests
    {
        [Fact]
        public void APayloadIsUnchangedWhenNothingWasSent()
        {
            var updates = new CouncilUserUpdates();
            Assert.Equal("build it", updates.ApplyTo("build it", "build it"));
        }

        [Fact]
        public void AMessageJoinsTheNextCallWithTheOriginalRequestKept()
        {
            var updates = new CouncilUserUpdates();
            updates.Post("use blue, not red");

            string payload = updates.ApplyTo("[TASK] build a landing page", "build a red landing page");

            Assert.StartsWith("[TASK] build a landing page", payload);
            Assert.Contains("\"use blue, not red\"", payload);
            Assert.Contains("The original request is still: \"build a red landing page\"", payload);
            Assert.False(updates.HasWaiting);
        }

        [Fact]
        public void EveryLaterStageSeesEveryUpdateSoFarInOrder()
        {
            var updates = new CouncilUserUpdates();
            updates.Post("first");
            updates.ApplyTo("builder", null);
            updates.Post("second");

            string critic = updates.ApplyTo("critic", null);

            Assert.True(critic.IndexOf("first", System.StringComparison.Ordinal) < critic.IndexOf("second", System.StringComparison.Ordinal));
            Assert.Contains("these messages", critic);
        }

        [Fact]
        public void AMessageAfterTheLastCallIsLeftForTheFollowUpAndANewRunStartsClean()
        {
            var updates = new CouncilUserUpdates();
            updates.ApplyTo("builder", null);
            updates.Post("also add a footer");

            Assert.True(updates.HasWaiting);
            Assert.Equal("also add a footer", CouncilUserUpdates.FollowUpText(updates.TakeWaiting()));
            updates.Reset();
            Assert.Equal("next run", updates.ApplyTo("next run", null));
        }
    }
}
