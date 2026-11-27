using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.Json.Nodes;
using Malx_AI.Agent;
using Xunit;

namespace Malx_AI.Tests
{
    public class CloudAgentReasoningTests
    {
        [Theory]
        [InlineData("axiom-deepseek-4.1f-1017", true, true)]
        [InlineData("deepseek-v4f-1017", true, true)]
        [InlineData("threnody-1.0", true, true)]
        [InlineData("deepseek/deepseek-v4.1-flash", true, true)]
        [InlineData("axiom-deepseek-4.1f-1017", false, false)]
        [InlineData("workplace-gpt-oss-20b", true, false)]
        [InlineData("custom-endpoint", true, false)]
        public void ReasoningIsReturnedOnlyForDeepSeekThinkingTurns(string model, bool thinking, bool expected)
        {
            Assert.Equal(expected, CloudAgentModel.ShouldReturnReasoning(model, thinking));
        }

        [Fact]
        public void LongReasoningKeepsItsEnd()
        {
            string reasoning = new string('a', 20000) + "so I will call read_file";
            string clipped = CloudAgentModel.ClipReasoning(reasoning)!;
            Assert.Equal(12000, clipped.Length);
            Assert.EndsWith("so I will call read_file", clipped);
            Assert.Null(CloudAgentModel.ClipReasoning("  "));
        }

        [Fact]
        public void AssistantToolCallMessagesCarryReasoningContentOnTheWire()
        {
            MethodInfo build = typeof(OpenRouterChatService).GetMethod("BuildMessages", BindingFlags.NonPublic | BindingFlags.Static)!;
            var messages = new List<OpenRouterMessage>
            {
                new("user", "list files"),
                new("assistant", string.Empty, ToolCalls: [new OpenRouterToolCall("call_1", "list_directory", "{}")], ReasoningContent: "I should look first."),
                new("tool", "a.txt", ToolCallId: "call_1"),
            };
            var payload = (JsonArray)build.Invoke(null, [messages, "system"])!;
            JsonObject assistant = payload[2]!.AsObject();
            Assert.Equal("I should look first.", (string?)assistant["reasoning_content"]);
            Assert.Null(payload[1]!.AsObject()["reasoning_content"]);
            Assert.Null(payload[3]!.AsObject()["reasoning_content"]);
        }

        [Fact]
        public void BackoffGrowsAndStaysCapped()
        {
            for (int i = 0; i < 50; i++)
            {
                TimeSpan first = CloudAgentModel.BackoffDelay(1);
                Assert.InRange(first.TotalSeconds, 1.5, 2.5);
                Assert.InRange(CloudAgentModel.BackoffDelay(2).TotalSeconds, 3, 5);
                Assert.InRange(CloudAgentModel.BackoffDelay(10).TotalSeconds, 22.5, 37.5);
            }
        }
    }
}
