using System.Text.Json;
using Malx_AI;
using Xunit;

namespace Malx_AI.Tests
{
    public class OllamaStreamChunkConverterTests
    {
        private static JsonElement Delta(string chunkJson)
        {
            using JsonDocument document = JsonDocument.Parse(chunkJson);
            return document.RootElement.GetProperty("choices")[0].GetProperty("delta").Clone();
        }

        [Fact]
        public void NativeChatLine_BecomesAContentDelta()
        {
            string line = """{"model":"gemma4:12b","message":{"role":"assistant","content":"Hello"},"done":false}""";

            Assert.True(OllamaStreamChunkConverter.TryConvertLine(line, out string chunk));
            Assert.Equal("Hello", Delta(chunk).GetProperty("content").GetString());
        }

        [Fact]
        public void NativeThinkingLine_BecomesAReasoningDelta()
        {
            string line = """{"message":{"role":"assistant","content":"","thinking":"weighing options"},"done":false}""";

            Assert.True(OllamaStreamChunkConverter.TryConvertLine(line, out string chunk));
            JsonElement delta = Delta(chunk);
            Assert.Equal("weighing options", delta.GetProperty("reasoning").GetString());
            Assert.False(delta.TryGetProperty("content", out _));
        }

        [Fact]
        public void ReasoningContentFieldIsAlsoRead()
        {
            string line = """{"message":{"role":"assistant","reasoning_content":"step one"},"done":false}""";

            Assert.True(OllamaStreamChunkConverter.TryConvertLine(line, out string chunk));
            Assert.Equal("step one", Delta(chunk).GetProperty("reasoning").GetString());
        }

        [Fact]
        public void GenerateShapeIsConverted()
        {
            string line = """{"response":"partial text","done":false}""";

            Assert.True(OllamaStreamChunkConverter.TryConvertLine(line, out string chunk));
            Assert.Equal("partial text", Delta(chunk).GetProperty("content").GetString());
        }

        [Fact]
        public void FinalLineCarriesFinishReasonAndUsage()
        {
            string line = """{"message":{"role":"assistant","content":""},"done":true,"done_reason":"stop","prompt_eval_count":31,"eval_count":420}""";

            Assert.True(OllamaStreamChunkConverter.TryConvertLine(line, out string chunk));
            using JsonDocument document = JsonDocument.Parse(chunk);
            JsonElement choice = document.RootElement.GetProperty("choices")[0];
            Assert.Equal("stop", choice.GetProperty("finish_reason").GetString());

            JsonElement usage = document.RootElement.GetProperty("usage");
            Assert.Equal(31, usage.GetProperty("prompt_tokens").GetInt32());
            Assert.Equal(420, usage.GetProperty("completion_tokens").GetInt32());
            Assert.Equal(451, usage.GetProperty("total_tokens").GetInt32());
        }

        [Fact]
        public void OpenAiShapedLineIsPassedThroughUnchanged()
        {
            string line = """{"choices":[{"delta":{"content":"hi"}}]}""";

            Assert.True(OllamaStreamChunkConverter.TryConvertLine(line, out string chunk));
            Assert.Equal(line, chunk);
        }

        [Fact]
        public void ErrorPayloadIsPassedThroughSoTheCallerCanSurfaceIt()
        {
            string line = """{"error":{"message":"model not found"}}""";

            Assert.True(OllamaStreamChunkConverter.TryConvertLine(line, out string chunk));
            Assert.Equal(line, chunk);
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("not json at all")]
        [InlineData("data: {\"choices\":[]}")]
        // A fragment of a pretty-printed body: the caller must fall back to reading the whole body.
        [InlineData("{\"message\": {")]
        // Keep-alive style chunk with nothing in it.
        [InlineData("{\"message\":{\"role\":\"assistant\",\"content\":\"\"},\"done\":false}")]
        public void UnusableLinesAreRejected(string line)
        {
            Assert.False(OllamaStreamChunkConverter.TryConvertLine(line, out string chunk));
            Assert.Equal(string.Empty, chunk);
        }

        [Fact]
        public void NullLineIsRejected()
        {
            Assert.False(OllamaStreamChunkConverter.TryConvertLine(null, out _));
        }

        [Theory]
        [InlineData(": keep-alive")]
        [InlineData(": OPENROUTER PROCESSING")]
        [InlineData("event: message")]
        [InlineData("id: 42")]
        [InlineData("retry: 3000")]
        public void SseCommentAndFieldLines_AreNotChunks(string line)
        {
            // Regression: a gateway keep-alive sent while the model composed a tool call was read
            // as the start of a non-streamed body, and the rest of the stream (the tool call and
            // the whole answer) was swallowed.
            Assert.True(OllamaStreamChunkConverter.IsSseNonDataLine(line));
        }

        [Theory]
        [InlineData("data: {\"choices\":[]}")]
        [InlineData("{\"message\":{\"content\":\"hi\"}}")]
        [InlineData("")]
        public void DataAndJsonLines_AreNotSkipped(string line)
        {
            Assert.False(OllamaStreamChunkConverter.IsSseNonDataLine(line));
        }
    }
}
