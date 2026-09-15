using Xunit;

namespace Malx_AI.Tests
{
    public class CustomEndpointMetadataParserTests
    {
        [Fact]
        public void Parse_OpenAiCompatibleModelsList_ReadsContextAndVision()
        {
            const string json = """
            {
              "data": [
                {
                  "id": "gemma-4-12b-it",
                  "name": "Gemma 4 12B IT",
                  "context_length": 231424,
                  "architecture": { "input_modalities": ["text", "image"] }
                }
              ]
            }
            """;

            CustomEndpointMetadata metadata = CustomEndpointMetadataParser.Parse(json, "gemma-4-12b-it");
            Assert.Equal(231424, metadata.ContextWindowTokens);
            Assert.True(metadata.SupportsImageInput);
            Assert.Equal("gemma-4-12b-it", metadata.MatchedModelId);
        }

        [Fact]
        public void Parse_VllmMaxModelLen_ReadsLargestWindow()
        {
            const string json = """
            {
              "data": [
                {
                  "id": "google/gemma-4-12b-it",
                  "max_model_len": 226000
                }
              ]
            }
            """;

            CustomEndpointMetadata metadata = CustomEndpointMetadataParser.Parse(json, "gemma-4-12b-it");
            Assert.Equal(226000, metadata.ContextWindowTokens);
            Assert.True(metadata.SupportsImageInput);
        }

        [Fact]
        public void LooksLikeVisionModel_RecognizesGemma4()
        {
            Assert.True(CustomEndpointMetadataParser.LooksLikeVisionModel("gemma4-12b-it"));
            Assert.False(CustomEndpointMetadataParser.LooksLikeVisionModel("llama-3.3-70b-instruct"));
        }

        [Fact]
        public void Parse_OllamaModelsList_AdvertisesNoContextWindow()
        {
            // Ollama's OpenAI-compatible surface reports no serving-time window at all, and none
            // may be inferred: the real one is whatever OLLAMA_CONTEXT_LENGTH says, typically a few
            // thousand tokens no matter what the model was trained for. Returning null here is what
            // routes the caller to the conservative fallback instead of an optimistic guess.
            const string json = """
            {
              "object": "list",
              "data": [
                { "id": "gemma4:12b", "object": "model", "created": 1730000000, "owned_by": "library" }
              ]
            }
            """;

            CustomEndpointMetadata metadata = CustomEndpointMetadataParser.Parse(json, "gemma4:12b");
            Assert.Null(metadata.ContextWindowTokens);
            // The vision capability is still inferable from the model name alone.
            Assert.True(metadata.SupportsImageInput);
        }
    }
}
