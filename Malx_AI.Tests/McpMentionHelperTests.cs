using Malx_AI.Mcp;
using Xunit;

namespace Malx_AI.Tests
{
    public class McpMentionHelperTests
    {
        private static readonly string[] Handles = ["Gmail", "GoogleDrive"];

        [Fact]
        public void FindMentions_MarksCompleteHandles()
        {
            var spans = McpMentionHelper.FindMentions("Check @Gmail and @Nope and @GoogleDrive please", Handles);
            Assert.Equal(3, spans.Count);
            Assert.True(spans[0].IsComplete);
            Assert.Equal("Gmail", spans[0].Handle);
            Assert.False(spans[1].IsComplete);
            Assert.True(spans[2].IsComplete);
            Assert.Equal("GoogleDrive", spans[2].Handle);
        }

        [Fact]
        public void TryGetActiveMentionQuery_DetectsInProgressToken()
        {
            string text = "hello @Gm";
            Assert.True(McpMentionHelper.TryGetActiveMentionQuery(text, text.Length, out int atIndex, out string query));
            Assert.Equal(6, atIndex);
            Assert.Equal("Gm", query);
        }

        [Fact]
        public void ApplyMentionCompletion_InsertsHandleAndSpace()
        {
            string text = "use @Gm";
            string result = McpMentionHelper.ApplyMentionCompletion(text, 4, text.Length, "Gmail");
            Assert.Equal("use @Gmail ", result);
        }

        [Fact]
        public void FilterConnectors_MatchesHandlePrefix()
        {
            var connectors = new[]
            {
                new McpConnectorInfo
                {
                    Id = "gmail",
                    Handle = "Gmail",
                    DisplayName = "Gmail",
                    Description = "mail",
                    Kind = McpConnectorKind.Gmail,
                    LogoGlyph = "✉",
                    IsConnected = true
                },
                new McpConnectorInfo
                {
                    Id = "drive",
                    Handle = "GoogleDrive",
                    DisplayName = "Google Drive",
                    Description = "drive",
                    Kind = McpConnectorKind.GoogleDrive,
                    LogoGlyph = "📁",
                    IsConnected = false
                }
            };

            var filtered = McpMentionHelper.FilterConnectors(connectors, "goog", connectedOnly: false);
            Assert.Single(filtered);
            Assert.Equal("GoogleDrive", filtered[0].Handle);
        }
    }
}
