using System.Collections.Generic;

namespace Malx_AI
{
    internal enum LocalModelFit { RunsOnGpu, RunsOnCpu, Tight, TooLarge }

    internal sealed record LocalModelFitHardware(double TotalRamGb, double TotalVramGb, bool HasNvidiaGpu, int LogicalCores);

    internal sealed record LocalModelFitCandidate(string DisplayName, double FileSizeGb);

    internal sealed record LocalModelFitResult(LocalModelFitCandidate Candidate, LocalModelFit Fit, string Summary);

    /// <summary>Rates how well a local GGUF model fits this computer, for the first-run guide.</summary>
    internal static class LocalModelFitAdvisor
    {
        public static double EstimateMemoryNeededGb(double fileSizeGb) => fileSizeGb * 1.2 + 0.5;

        public static LocalModelFitResult Assess(LocalModelFitCandidate candidate, LocalModelFitHardware hardware)
        {
            double needed = EstimateMemoryNeededGb(candidate.FileSizeGb);

            if (hardware.HasNvidiaGpu && hardware.TotalVramGb > 0 && needed <= hardware.TotalVramGb * 0.85)
                return new LocalModelFitResult(candidate, LocalModelFit.RunsOnGpu, "Fast: fits on your graphics card.");

            if (needed <= hardware.TotalRamGb * 0.5)
                return new LocalModelFitResult(candidate, LocalModelFit.RunsOnCpu, "Runs on your processor. Slower than a graphics card, but works.");

            if (needed <= hardware.TotalRamGb * 0.75)
                return new LocalModelFitResult(candidate, LocalModelFit.Tight, "Uses most of your memory. Expect slow replies.");

            return new LocalModelFitResult(candidate, LocalModelFit.TooLarge, "Too large for this computer.");
        }

        public static int ChooseRecommendedIndex(IReadOnlyList<LocalModelFitCandidate> candidates, LocalModelFitHardware hardware)
        {
            if (candidates.Count == 0)
                return -1;

            // Prefer the largest model that runs entirely on the graphics card.
            int bestGpuIndex = -1;
            double bestGpuSize = double.NegativeInfinity;
            for (int i = 0; i < candidates.Count; i++)
            {
                var result = Assess(candidates[i], hardware);
                if (result.Fit == LocalModelFit.RunsOnGpu && candidates[i].FileSizeGb > bestGpuSize)
                {
                    bestGpuIndex = i;
                    bestGpuSize = candidates[i].FileSizeGb;
                }
            }
            if (bestGpuIndex >= 0)
                return bestGpuIndex;

            // On the processor, cap the size: bigger models run, but too slowly to enjoy.
            double cpuLimitGb = hardware.LogicalCores >= 8 ? 3.0 : 1.5;
            int bestCpuIndex = -1;
            double bestCpuSize = double.NegativeInfinity;
            for (int i = 0; i < candidates.Count; i++)
            {
                var result = Assess(candidates[i], hardware);
                if (result.Fit == LocalModelFit.RunsOnCpu && candidates[i].FileSizeGb <= cpuLimitGb && candidates[i].FileSizeGb > bestCpuSize)
                {
                    bestCpuIndex = i;
                    bestCpuSize = candidates[i].FileSizeGb;
                }
            }
            if (bestCpuIndex >= 0)
                return bestCpuIndex;

            // Nothing fits comfortably: offer the smallest.
            int bestSmallIndex = 0;
            double bestSmallSize = candidates[0].FileSizeGb;
            for (int i = 1; i < candidates.Count; i++)
            {
                if (candidates[i].FileSizeGb < bestSmallSize)
                {
                    bestSmallIndex = i;
                    bestSmallSize = candidates[i].FileSizeGb;
                }
            }
            return bestSmallIndex;
        }
    }
}
