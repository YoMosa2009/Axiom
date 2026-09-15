using Malx_AI.ComputerUse;
using Xunit;

namespace Malx_AI.Tests
{
    public class ComputerUseCapabilityTests
    {
        [Theory]
        [InlineData("google/gemma-4-31b-it:free", true)]
        [InlineData("qwen/qwen2.5-vl-72b-instruct", true)]
        [InlineData("poolside/laguna-m.1:free", false)]
        [InlineData("llama-3.3-70b-instruct", false)]
        public void LooksLikeVisionModel_UsesNameSignals(string id, bool expected)
        {
            Assert.Equal(expected, ComputerUseCapability.LooksLikeVisionModel(id));
        }

        [Fact]
        public void Evaluate_LocalRequiresProjector()
        {
            ComputerUseCapabilityResult result = ComputerUseCapability.Evaluate(
                cloudMode: false,
                hybridLocal: false,
                catalogVision: false,
                catalogKnown: false,
                actingModelLabel: "Qwen3-4B",
                actingModelIdOrPath: @"C:\models\Qwen3-4B.gguf",
                localProjectorAvailable: false);

            Assert.False(result.CanRun);
            Assert.Contains("mmproj", result.Reason, System.StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Evaluate_CloudUsesCatalogVision()
        {
            ComputerUseCapabilityResult result = ComputerUseCapability.Evaluate(
                cloudMode: true,
                hybridLocal: false,
                catalogVision: true,
                catalogKnown: true,
                actingModelLabel: "Gemma 4",
                actingModelIdOrPath: "google/gemma-4-31b-it:free",
                localProjectorAvailable: false);

            Assert.True(result.CanRun);
        }

        [Fact]
        public void Evaluate_CloudWithoutCatalogFallsBackToHeuristic()
        {
            ComputerUseCapabilityResult allowed = ComputerUseCapability.Evaluate(
                true, true, false, false, "Kestrel 1", "local-gemma-4", false);
            Assert.True(allowed.CanRun);

            ComputerUseCapabilityResult blocked = ComputerUseCapability.Evaluate(
                true, false, false, true, "Laguna", "poolside/laguna-m.1:free", false);
            Assert.False(blocked.CanRun);
        }
    }
}
