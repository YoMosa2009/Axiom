using System;

namespace Malx_AI
{
    /// <summary>Estimated token sizes of each part of the Workplace context (chars/4 style guesses).</summary>
    public readonly record struct WorkplaceContextEstimate(
        int Messages,
        int SystemTools,
        int McpTools,
        int Skills,
        int SystemPrompt,
        int McpInstructions,
        int MemoryFiles,
        int PendingInput);

    /// <summary>The numbers the Context panel shows.</summary>
    public sealed record WorkplaceContextUsageResult(
        int ContextWindow,
        int Messages,
        int SystemTools,
        int McpTools,
        int Skills,
        int SystemPrompt,
        int McpInstructions,
        int MemoryFiles,
        int PendingInput,
        int Used,
        int AutocompactBuffer,
        int Free,
        double Percent,
        int UntilCompact,
        bool IsMeasured);

    /// <summary>
    /// Pure calculator for the Workplace context panel. When the provider reported the prompt size of
    /// the last model turn, that is the true context and it overrides the estimates; the estimated
    /// rows are then fitted to it so the rows always add up to the headline number.
    /// </summary>
    public static class WorkplaceContextUsage
    {
        /// <summary>Share of the window at which auto-compact starts.</summary>
        public const double CompactThreshold = 0.80;

        public static WorkplaceContextUsageResult Calculate(
            int contextWindow,
            int? measuredPromptTokens,
            int measuredCompletionTokens,
            WorkplaceContextEstimate estimate,
            int addedSinceMeasurementTokens)
        {
            int window = Math.Max(0, contextWindow);
            int messages = Math.Max(0, estimate.Messages);
            int tools = Math.Max(0, estimate.SystemTools);
            int mcp = Math.Max(0, estimate.McpTools);
            int skills = Math.Max(0, estimate.Skills);
            int prompt = Math.Max(0, estimate.SystemPrompt);
            int mcpInstr = Math.Max(0, estimate.McpInstructions);
            int memory = Math.Max(0, estimate.MemoryFiles);
            int pending = Math.Max(0, estimate.PendingInput);

            bool measured = measuredPromptTokens is > 0;
            if (measured)
            {
                long measuredPartLong = (long)measuredPromptTokens!.Value
                    + Math.Max(0, measuredCompletionTokens)
                    + Math.Max(0, addedSinceMeasurementTokens);
                int measuredPart = (int)Math.Min(int.MaxValue / 2, measuredPartLong);

                long fixedLong = (long)tools + mcp + skills + prompt + mcpInstr + memory;
                if (fixedLong <= measuredPart)
                {
                    messages = measuredPart - (int)fixedLong;
                }
                else
                {
                    // The fixed overhead guesses are larger than the measured whole: shrink them
                    // proportionally and leave nothing for Messages.
                    int[] rows = { tools, mcp, skills, prompt, mcpInstr, memory };
                    int sum = 0;
                    int largest = 0;
                    for (int i = 0; i < rows.Length; i++)
                    {
                        rows[i] = (int)Math.Round(rows[i] * (double)measuredPart / fixedLong);
                        sum += rows[i];
                        if (rows[i] > rows[largest])
                            largest = i;
                    }
                    rows[largest] = Math.Max(0, rows[largest] + (measuredPart - sum));
                    (tools, mcp, skills, prompt, mcpInstr, memory) = (rows[0], rows[1], rows[2], rows[3], rows[4], rows[5]);
                    messages = 0;
                }
            }

            long usedLong = (long)messages + tools + mcp + skills + prompt + mcpInstr + memory + pending;
            int used = (int)Math.Min(int.MaxValue, usedLong);
            int buffer = window > 0 ? Math.Max(512, (int)Math.Round(window * 0.08)) : 0;
            int free = window > 0 ? Math.Max(0, window - used - buffer) : 0;
            double percent = window > 0 ? Math.Clamp(used * 100.0 / window, 0, 100) : 0;
            int until = window > 0 ? Math.Max(0, (int)(window * CompactThreshold) - used) : 0;

            return new WorkplaceContextUsageResult(
                window, messages, tools, mcp, skills, prompt, mcpInstr, memory, pending,
                used, buffer, free, percent, until, measured);
        }
    }
}
