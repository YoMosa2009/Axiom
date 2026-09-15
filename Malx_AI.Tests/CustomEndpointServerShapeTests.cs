using Xunit;

namespace Malx_AI.Tests
{
    /// <summary>
    /// Response shapes taken from the local inference servers Axiom's Hybrid Local mode is pointed
    /// at in practice. Each case asserts the SERVED window is chosen, not the model's trained
    /// capacity: declaring a window the server cannot honour does not fail cleanly, it stalls the
    /// server or kills the runner mid-stream.
    /// </summary>
    public class CustomEndpointServerShapeTests
    {
        [Fact]
        public void Ollama_RunningModels_ReportsTheLoadedWindow()
        {
            // GET /api/ps — the only Ollama endpoint that exposes the runtime window, which is set
            // by OLLAMA_CONTEXT_LENGTH / num_ctx and is unrelated to what the model was trained for.
            const string json = """
            {
              "models": [
                {
                  "name": "gemma3:12b",
                  "model": "gemma3:12b",
                  "size": 11278663168,
                  "details": { "family": "gemma3", "parameter_size": "12.2B" },
                  "context_length": 16384
                }
              ]
            }
            """;

            CustomEndpointMetadata metadata = CustomEndpointMetadataParser.Parse(json, "gemma3:12b");
            Assert.Equal(16384, metadata.ContextWindowTokens);
        }

        [Fact]
        public void Ollama_ModelCard_FallsBackToTrainedCapacityAndReadsVisionCapability()
        {
            // POST /api/show — context_length here is the architecture's trained capacity, which is
            // routinely 8x the served window. It is probed last, and only counts when nothing else
            // answered at all.
            const string json = """
            {
              "details": { "family": "gemma3", "quantization_level": "Q4_K_M" },
              "model_info": {
                "general.architecture": "gemma3",
                "gemma3.context_length": 131072,
                "gemma3.attention.head_count": 16
              },
              "capabilities": ["completion", "vision"]
            }
            """;

            CustomEndpointMetadata metadata = CustomEndpointMetadataParser.Parse(json, "gemma3:12b");
            Assert.Equal(131072, metadata.ContextWindowTokens);
            Assert.True(metadata.SupportsImageInput);
        }

        [Fact]
        public void LmStudio_PrefersLoadedWindowOverTheModelCeiling()
        {
            // GET /api/v0/models — reports both figures at once. Picking the larger one would
            // declare 131072 against a server actually serving 16384.
            const string json = """
            {
              "object": "list",
              "data": [
                {
                  "id": "google/gemma-3-12b",
                  "object": "model",
                  "type": "vlm",
                  "arch": "gemma3",
                  "state": "loaded",
                  "max_context_length": 131072,
                  "loaded_context_length": 16384
                }
              ]
            }
            """;

            CustomEndpointMetadata metadata = CustomEndpointMetadataParser.Parse(json, "google/gemma-3-12b");
            Assert.Equal(16384, metadata.ContextWindowTokens);
            // "vlm" is LM Studio's own way of saying the model takes images.
            Assert.True(metadata.SupportsImageInput);
        }

        [Fact]
        public void LlamaCpp_Props_ReadsServedContextNotTrainedContext()
        {
            // GET /props — n_ctx is what the server was launched with; n_ctx_train is the model's.
            const string json = """
            {
              "default_generation_settings": {
                "n_ctx": 8192,
                "n_predict": -1,
                "model": "/models/gemma-3-12b-it-Q4_K_M.gguf"
              },
              "total_slots": 1,
              "model_info": { "n_ctx_train": 131072 }
            }
            """;

            CustomEndpointMetadata metadata = CustomEndpointMetadataParser.Parse(json, "gemma-3-12b-it");
            Assert.Equal(8192, metadata.ContextWindowTokens);
        }

        [Fact]
        public void KoboldCpp_ScalarConfigResponseIsReadFromTheEndpointMeaning()
        {
            // GET /api/v1/config/max_context_length — the field is named "value"; only the URL
            // says what it is, so the generic field-name walk must not be what resolves it.
            const string json = """{ "value": 24576 }""";

            Assert.False(CustomEndpointMetadataParser.Parse(json, "any-model").ContextWindowTokens.HasValue);
            Assert.True(CustomEndpointMetadataParser.TryParseScalarContextWindow(json, out int tokens));
            Assert.Equal(24576, tokens);
        }

        [Fact]
        public void TabbyApi_ReadsMaxSeqLenOfTheLoadedModel()
        {
            const string json = """
            {
              "id": "gemma-3-12b-it-exl2",
              "object": "model",
              "owned_by": "tabbyAPI",
              "parameters": { "max_seq_len": 32768, "cache_size": 32768, "rope_scale": 1.0 }
            }
            """;

            CustomEndpointMetadata metadata = CustomEndpointMetadataParser.Parse(json, "gemma-3-12b-it-exl2");
            Assert.Equal(32768, metadata.ContextWindowTokens);
        }

        [Fact]
        public void Vllm_ReadsMaxModelLenFromTheOpenAiCatalog()
        {
            const string json = """
            {
              "object": "list",
              "data": [
                {
                  "id": "google/gemma-3-12b-it",
                  "object": "model",
                  "owned_by": "vllm",
                  "max_model_len": 32768
                }
              ]
            }
            """;

            CustomEndpointMetadata metadata = CustomEndpointMetadataParser.Parse(json, "google/gemma-3-12b-it");
            Assert.Equal(32768, metadata.ContextWindowTokens);
        }

        [Fact]
        public void TrainedCapacityNeverOutranksAServedWindowInTheSameDocument()
        {
            // The core invariant behind the whole tier ordering, stated on its own.
            const string json = """
            {
              "data": [
                {
                  "id": "some-model",
                  "n_ctx": 4096,
                  "max_model_len": 65536,
                  "context_length": 131072,
                  "n_ctx_train": 1048576
                }
              ]
            }
            """;

            CustomEndpointMetadata metadata = CustomEndpointMetadataParser.Parse(json, "some-model");
            Assert.Equal(4096, metadata.ContextWindowTokens);
        }

        [Fact]
        public void UnknownServerReportsNothingRatherThanGuessing()
        {
            // A server Axiom does not recognise must yield null so the caller can say so plainly,
            // rather than inventing a number that reads as discovered.
            const string json = """{ "status": "ok", "uptime_seconds": 1200 }""";

            CustomEndpointMetadata metadata = CustomEndpointMetadataParser.Parse(json, "some-model");
            Assert.Null(metadata.ContextWindowTokens);
        }
    }
}
