using System.Text.Json.Nodes;
using Xunit;

namespace Malx_AI.Tests
{
    public class ModelSettingsRegistryTests
    {
        [Theory]
        [InlineData(@"C:\Models\Qwen3-8B-Q4_K_M.gguf", "qwen3")]
        [InlineData("qwen3:8b", "qwen3")]
        [InlineData("Qwen3.6-35B-A3B-UD-IQ3_S", "qwen3.6")]
        [InlineData("qwen/qwen3-coder-30b-a3b-instruct", "qwen3-coder")]
        [InlineData("gemma4:12b", "gemma-4")]
        [InlineData("unsloth/gemma-4-31B-it-GGUF", "gemma-4")]
        [InlineData("DeepSeek-R1-Distill-Qwen-7B-Q4_K_M.gguf", "deepseek-r1-distill")]
        [InlineData("deepseek/deepseek-v4.1-flash", "deepseek")]
        [InlineData("gpt-oss-20b", "gpt-oss")]
        [InlineData("Devstral-Small-2507-Q4_K_M.gguf", "devstral")]
        public void KnownFamiliesMatchByLongestPrefix(string id, string family)
        {
            Assert.Equal(family, ModelSettingsRegistry.Resolve(id).Family?.Id);
        }

        [Theory]
        [InlineData("qwen3.5-9b")]       // no reviewed entry: must not fall back to Qwen3's settings
        [InlineData("qwen30-something")]
        [InlineData("llama-3.1-8b-instruct")]
        [InlineData("nvidia/nemotron-3.5-super-vl:free")]
        [InlineData("")]
        [InlineData(null)]
        public void UnknownIdsChangeNothing(string? id)
        {
            ModelSettings settings = ModelSettingsRegistry.Resolve(id);
            Assert.False(settings.IsKnown);
            Assert.Null(settings.SamplingFor(true));
            Assert.Null(settings.SamplingFor(false));
        }

        [Fact]
        public void Qwen36UsesItsPreciseCodingProfileForAgentTurns()
        {
            ModelSettings settings = ModelSettingsRegistry.Resolve("qwen3.6-35b-a3b");
            Assert.Equal(0.6, settings.SamplingFor(true, ModelUse.Coding)!.Temperature);
            Assert.Equal(1.0, settings.SamplingFor(true)!.Temperature);
            Assert.Equal(0.7, settings.SamplingFor(false)!.Temperature);
        }

        [Fact]
        public void HybridLocalRequestsGetTheFamilySampling()
        {
            var payload = new JsonObject { ["temperature"] = 0.16, ["top_p"] = 0.8 };
            ModelSettingsRegistry.ApplyToRequest(payload, "qwen3.6-35b-a3b", thinking: true, coding: true, customEndpoint: true);

            Assert.Equal(0.6, payload["temperature"]!.GetValue<double>());
            Assert.Equal(0.95, payload["top_p"]!.GetValue<double>());
            Assert.Equal(20, payload["top_k"]!.GetValue<int>());
            Assert.Equal(0.0, payload["presence_penalty"]!.GetValue<double>());
        }

        [Fact]
        public void OpenRouterProfilesKeepTheirOwnSampling()
        {
            var payload = new JsonObject { ["temperature"] = 0.16, ["top_p"] = 0.8 };
            ModelSettingsRegistry.ApplyToRequest(payload, "qwen/qwen3-8b", thinking: false, coding: true, customEndpoint: false);

            Assert.Equal(0.16, payload["temperature"]!.GetValue<double>());
            Assert.False(payload.ContainsKey("top_k"));
        }

        [Fact]
        public void DeepSeekThinkingTurnsSendNoSampling()
        {
            var payload = new JsonObject { ["temperature"] = 0.1, ["top_p"] = 0.75 };
            ModelSettingsRegistry.ApplyToRequest(payload, "deepseek/deepseek-v4.1-flash", thinking: true, coding: true, customEndpoint: false);
            Assert.False(payload.ContainsKey("temperature"));
            Assert.False(payload.ContainsKey("top_p"));

            var nonThinking = new JsonObject { ["temperature"] = 0.1 };
            ModelSettingsRegistry.ApplyToRequest(nonThinking, "deepseek/deepseek-v4.1-flash", thinking: false, coding: true, customEndpoint: false);
            Assert.True(nonThinking.ContainsKey("temperature"));
        }

        [Fact]
        public void UnknownModelPayloadIsUntouched()
        {
            var payload = new JsonObject { ["temperature"] = 0.16, ["top_p"] = 0.8 };
            ModelSettingsRegistry.ApplyToRequest(payload, "my-own-finetune", thinking: true, coding: true, customEndpoint: true);
            Assert.Equal("{\"temperature\":0.16,\"top_p\":0.8}", payload.ToJsonString());
        }

        [Fact]
        public void UserEntriesWinOverBuiltInOnes()
        {
            var user = ModelSettingsRegistry.ParseUserOverrides(
                "[ { \"match\": [\"qwen3\"], \"temperature\": 0.3, \"top_k\": 10 }, // comment\n { \"match\": \"bad\" } ]", out string? error);

            Assert.Null(error);
            Assert.Single(user);
            ModelSettings settings = ModelSettingsRegistry.Resolve("qwen3-8b", user);
            Assert.True(settings.Family!.FromUser);
            Assert.Equal(0.3, settings.SamplingFor(false)!.Temperature);
            Assert.Equal(10, settings.SamplingFor(true)!.TopK);
            // A longer built-in prefix still wins over nothing, but the user's entry is checked first.
            Assert.Equal("qwen3.6", ModelSettingsRegistry.Resolve("qwen3.6-35b", user).Family!.Id);
        }

        [Fact]
        public void BrokenUserFileGivesNoEntries()
        {
            Assert.Empty(ModelSettingsRegistry.ParseUserOverrides("{ not json", out string? error));
            Assert.NotNull(error);
            Assert.Empty(ModelSettingsRegistry.ParseUserOverrides("[{\"match\":\"x\",\"temperature\":9}]", out _));
        }

        [Fact]
        public void EveryBuiltInEntryNamesItsSource()
        {
            foreach (ModelFamilySettings entry in ModelSettingsRegistry.BuiltIn)
                Assert.StartsWith("https://", entry.Source);
        }
    }
}
