using System;
using System.IO;
using System.Text.RegularExpressions;

namespace Malx_AI.ComputerUse
{
    internal static class ComputerUseCapability
    {
        private static readonly Regex VisionNameRegex = new(
            @"\b(vl|vision|visual|multimodal|mmproj|llava|pixtral|internvl|minicpm-v|nvila|qwen2\.5-vl|qwen3-vl|gemma-3|gemma3|gemma-4|gemma4|gpt-4o|gpt-4\.1|gpt-5|claude-3|claude-sonnet|claude-opus|llama-4|llama4|phi-4-multimodal|nemotron.+vision)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

        public static bool LooksLikeVisionModel(string? modelIdOrPath)
        {
            if (string.IsNullOrWhiteSpace(modelIdOrPath))
                return false;

            string name = modelIdOrPath;
            try
            {
                if (modelIdOrPath.Contains('\\') || modelIdOrPath.Contains('/') || modelIdOrPath.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase))
                    name = Path.GetFileNameWithoutExtension(modelIdOrPath);
            }
            catch
            {
                name = modelIdOrPath;
            }

            return VisionNameRegex.IsMatch(name) || VisionNameRegex.IsMatch(modelIdOrPath);
        }

        public static bool LooksLikeGemma4Cli(string? modelIdOrPath)
        {
            if (string.IsNullOrWhiteSpace(modelIdOrPath))
                return false;

            string text = modelIdOrPath;
            return text.Contains("gemma-4", StringComparison.OrdinalIgnoreCase)
                || text.Contains("gemma4", StringComparison.OrdinalIgnoreCase)
                || text.Contains("gemma_4", StringComparison.OrdinalIgnoreCase);
        }

        public static bool HasLocalProjector(string? modelPath)
        {
            if (string.IsNullOrWhiteSpace(modelPath) || !File.Exists(modelPath))
                return false;

            try
            {
                return !string.IsNullOrWhiteSpace(LocalVisionSupport.FindBestProjectorNextToModel(modelPath));
            }
            catch
            {
                return false;
            }
        }

        public static ComputerUseCapabilityResult Evaluate(
            bool cloudMode,
            bool hybridLocal,
            bool catalogVision,
            bool catalogKnown,
            string actingModelLabel,
            string? actingModelIdOrPath,
            bool localProjectorAvailable)
        {
            string surface = !cloudMode
                ? "Local"
                : hybridLocal ? "Hybrid-Local" : "Cloud";

            if (!cloudMode)
            {
                if (string.IsNullOrWhiteSpace(actingModelIdOrPath))
                {
                    return Fail(surface, actingModelLabel, catalogVision, false, localProjectorAvailable,
                        "Computer Use needs a loaded Builder/agent model with vision (mmproj) support.");
                }

                if (LooksLikeGemma4Cli(actingModelIdOrPath) || LooksLikeGemma4Cli(actingModelLabel))
                {
                    return Fail(surface, actingModelLabel, catalogVision, LooksLikeVisionModel(actingModelIdOrPath), localProjectorAvailable,
                        "Gemma 4 local CLI mode cannot receive screenshot pixels. Load a GGUF vision model with an mmproj projector.");
                }

                bool heuristic = LooksLikeVisionModel(actingModelIdOrPath) || LooksLikeVisionModel(actingModelLabel);
                if (!localProjectorAvailable)
                {
                    return Fail(surface, actingModelLabel, false, heuristic, false,
                        "Computer Use needs a compatible mmproj projector next to the local model so it can see screenshots.");
                }

                return new ComputerUseCapabilityResult
                {
                    CanRun = true,
                    CatalogVision = false,
                    HeuristicVision = heuristic,
                    LocalProjectorAvailable = true,
                    ExecutionSurface = surface,
                    ActingModelLabel = actingModelLabel,
                    Reason = $"Local vision projector found for {actingModelLabel}."
                };
            }

            bool heuristicVision = LooksLikeVisionModel(actingModelIdOrPath) || LooksLikeVisionModel(actingModelLabel);
            bool hasVision = catalogVision || (!catalogKnown && heuristicVision);
            if (!hasVision)
            {
                string reason = catalogKnown
                    ? $"{actingModelLabel} does not advertise image input, so Computer Use cannot send screenshots."
                    : $"{actingModelLabel} does not look like a vision-capable model, so Computer Use is blocked.";
                return Fail(surface, actingModelLabel, catalogVision, heuristicVision, localProjectorAvailable, reason);
            }

            return new ComputerUseCapabilityResult
            {
                CanRun = true,
                CatalogVision = catalogVision,
                HeuristicVision = heuristicVision,
                LocalProjectorAvailable = localProjectorAvailable,
                ExecutionSurface = surface,
                ActingModelLabel = actingModelLabel,
                Reason = catalogVision
                    ? $"{actingModelLabel} advertises image input."
                    : $"{actingModelLabel} is treated as vision-capable from its model id (catalog modality unavailable)."
            };
        }

        private static ComputerUseCapabilityResult Fail(
            string surface,
            string actingModelLabel,
            bool catalogVision,
            bool heuristicVision,
            bool localProjector,
            string reason)
        {
            return new ComputerUseCapabilityResult
            {
                CanRun = false,
                CatalogVision = catalogVision,
                HeuristicVision = heuristicVision,
                LocalProjectorAvailable = localProjector,
                ExecutionSurface = surface,
                ActingModelLabel = actingModelLabel,
                Reason = reason
            };
        }
    }
}
