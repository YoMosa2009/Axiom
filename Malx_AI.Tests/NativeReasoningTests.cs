using System.Text.Json;
using Xunit;

namespace Malx_AI.Tests
{
    /// <summary>
    /// Shapes copied from OpenRouter's /api/v1/models on 2026-11-26 (nemotron-3.5-super-vl,
    /// gpt-5.1, gpt-5.1-codex-max, deepseek-r1-0528).
    /// </summary>
    public class NativeReasoningTests
    {
        private static NativeReasoningInfo Parse(string json)
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            return NativeReasoning.FromOpenRouterModel(doc.RootElement);
        }

        private static readonly NativeReasoningInfo Nemotron = Parse(
            "{\"id\":\"nvidia/nemotron-3.5-super-vl:free\",\"supported_parameters\":[\"include_reasoning\",\"reasoning\",\"tools\"],\"reasoning\":{\"mandatory\":false}}");
        private static readonly NativeReasoningInfo Gpt51 = Parse(
            "{\"id\":\"openai/gpt-5.1\",\"supported_parameters\":[\"reasoning\"],\"reasoning\":{\"mandatory\":false,\"default_enabled\":false,\"supported_efforts\":[\"high\",\"medium\",\"low\",\"none\"],\"default_effort\":\"medium\"}}");
        private static readonly NativeReasoningInfo CodexMax = Parse(
            "{\"id\":\"openai/gpt-5.1-codex-max\",\"supported_parameters\":[\"reasoning\"],\"reasoning\":{\"mandatory\":true,\"supported_efforts\":[\"xhigh\",\"high\",\"medium\",\"low\"],\"default_effort\":\"medium\"}}");
        private static readonly NativeReasoningInfo R1 = Parse(
            "{\"id\":\"deepseek/deepseek-r1-0528\",\"supported_parameters\":[\"reasoning\",\"include_reasoning\"],\"reasoning\":{\"mandatory\":true}}");

        [Fact]
        public void TheModelListIsReadForMandatoryThinkingAndEfforts()
        {
            Assert.True(Nemotron.Supported);
            Assert.False(Nemotron.Mandatory);
            Assert.True(CodexMax.Mandatory);
            Assert.Equal(["xhigh", "high", "medium", "low"], CodexMax.Efforts);
            Assert.False(Parse("{\"id\":\"x\",\"supported_parameters\":[\"tools\"]}").Supported);
        }

        [Fact]
        public void AModelThatAlwaysThinksIsNeverToldToStop()
        {
            NativeReasoningDecision decision = NativeReasoning.Decide(R1, thinkingWanted: false, EffortLevel.Medium);
            Assert.Equal(NativeReasoningMode.Enable, decision.Mode);
            Assert.Equal("low", decision.Effort);
        }

        [Fact]
        public void ASwitchableModelIsTurnedOffWhenTheEffortLevelDoesNotAskForThinking()
        {
            Assert.Equal(NativeReasoningMode.Disable, NativeReasoning.Decide(Nemotron, false, EffortLevel.Light).Mode);
        }

        [Fact]
        public void UltraReachesTheModelsTopEffortAndIsFittedToWhatItAccepts()
        {
            Assert.Equal("xhigh", NativeReasoning.Decide(CodexMax, true, EffortLevel.Ultra).Effort);
            Assert.Equal("high", NativeReasoning.Decide(Gpt51, true, EffortLevel.Ultra).Effort);
            Assert.Equal("high", NativeReasoning.Decide(Nemotron, true, EffortLevel.Ultra).Effort);
            Assert.Equal("medium", NativeReasoning.Decide(Gpt51, true, EffortLevel.Medium).Effort);
        }

        [Fact]
        public void TheSubscriptionModelGetsItsMaxThinkingAtUltraAndNoneBelowHigh()
        {
            Assert.Equal("max", NativeReasoning.Decide(NativeReasoning.SubscriptionModel, true, EffortLevel.Ultra).Effort);
            Assert.Equal("high", NativeReasoning.Decide(NativeReasoning.SubscriptionModel, true, EffortLevel.High).Effort);
            Assert.Equal(NativeReasoningMode.Disable, NativeReasoning.Decide(NativeReasoning.SubscriptionModel, false, EffortLevel.Medium).Mode);
        }

        [Fact]
        public void AnUnsupportedModelGetsNoReasoningField()
        {
            Assert.Equal(NativeReasoningMode.Omit, NativeReasoning.Decide(NativeReasoningInfo.None, true, EffortLevel.High).Mode);
        }

        [Fact]
        public void ChatTemplatesShowWhetherThinkingCanBeSwitched()
        {
            Assert.Equal((true, true), NativeReasoning.FromChatTemplate("{%- if enable_thinking is defined and enable_thinking is false %}<think>\n\n</think>{%- endif %}"));
            Assert.Equal((true, false), NativeReasoning.FromChatTemplate("{{ '<think>\\n' }}"));
            Assert.Equal((false, false), NativeReasoning.FromChatTemplate("{{ messages }}"));
        }

        [Fact]
        public void AHybridServerTemplateWithAThinkingSwitchIsDetectedFromProps()
        {
            var metadata = CustomEndpointMetadataParser.Parse(
                "{\"default_generation_settings\":{\"n_ctx\":131072},\"chat_template\":\"{% if enable_thinking %}<think>{% endif %}\"}", "qwen3.6");
            Assert.True(metadata.ThinkingSwitchable);
            Assert.Equal(131072, metadata.ContextWindowTokens);
        }
    }
}
