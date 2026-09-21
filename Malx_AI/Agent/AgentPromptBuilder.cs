using System;
using System.Text;

namespace Malx_AI.Agent
{
    /// <summary>How much agent protocol the answering model can carry.</summary>
    public enum AgentTier
    {
        /// <summary>Under 1B. One tool at a time, flat syntax, very few steps.</summary>
        Micro,

        /// <summary>1B-4B. Flat syntax, a real loop, no multi-line file writing.</summary>
        Compact,

        /// <summary>4B and up, and every cloud model. JSON calls and the full tool set.</summary>
        Full
    }

    /// <summary>Builds the agent's operating instructions, sized to the model running them.</summary>
    public static class AgentPromptBuilder
    {
        internal static AgentTier TierFor(LocalModelCapabilityProfile? capability, bool cloudMode)
        {
            if (cloudMode)
                return AgentTier.Full;

            return capability?.SizeClass switch
            {
                LocalModelSizeClass.SubOneB => AgentTier.Micro,
                LocalModelSizeClass.OneToFourB => AgentTier.Compact,
                _ => AgentTier.Full
            };
        }

        /// <summary>Iteration ceiling for a tier and effort. Small models get fewer steps to avoid loops; large and cloud models get deep budgets.</summary>
        internal static int MaxStepsFor(
            AgentTier tier,
            EffortLevel effort,
            LocalModelCapabilityProfile? capability,
            bool isCloudMode)
        {
            if (isCloudMode || (tier == AgentTier.Full && (capability == null || capability.SizeClass == LocalModelSizeClass.TenBPlus || capability.SizeClass == LocalModelSizeClass.Unknown)))
            {
                return effort switch
                {
                    EffortLevel.Light => 20,
                    EffortLevel.Medium => 36,
                    EffortLevel.High => 60,
                    EffortLevel.ExtraHigh => 90,
                    EffortLevel.Ultra => 128,
                    _ => 36
                };
            }

            if (capability?.SizeClass == LocalModelSizeClass.FourToTenB || tier == AgentTier.Full)
            {
                return effort switch
                {
                    EffortLevel.Light => 14,
                    EffortLevel.Medium => 24,
                    EffortLevel.High => 40,
                    EffortLevel.ExtraHigh => 64,
                    EffortLevel.Ultra => 96,
                    _ => 24
                };
            }

            if (capability?.SizeClass == LocalModelSizeClass.OneToFourB || tier == AgentTier.Compact)
            {
                return effort switch
                {
                    EffortLevel.Light => 8,
                    EffortLevel.Medium => 14,
                    EffortLevel.High => 24,
                    EffortLevel.ExtraHigh => 36,
                    EffortLevel.Ultra => 52,
                    _ => 14
                };
            }

            // SubOneB / Micro
            return effort switch
            {
                EffortLevel.Light => 4,
                EffortLevel.Medium => 8,
                EffortLevel.High => 12,
                EffortLevel.ExtraHigh => 18,
                EffortLevel.Ultra => 26,
                _ => 8
            };
        }

        /// <summary>Iteration ceiling for a tier at the given effort.</summary>
        public static int MaxStepsFor(AgentTier tier, EffortLevel effort) =>
            MaxStepsFor(tier, effort, null, tier == AgentTier.Full);

        /// <summary>Iteration ceiling for a tier at default Medium effort.</summary>
        public static int MaxStepsFor(AgentTier tier) => MaxStepsFor(tier, EffortLevel.Medium);

        /// <summary>
        /// Builds the operating instructions.
        /// </summary>
        /// <param name="nativeToolCalling">
        /// True when the transport carries real function definitions. The prompt then must NOT
        /// describe a text protocol: telling a model to "reply with JSON" while it also holds real
        /// tool definitions produces a model that does neither well.
        /// </param>
        public static string Build(AgentTier tier, AgentScope scope, AgentApprovalMode mode, bool nativeToolCalling = false)
        {
            var builder = new StringBuilder();
            builder.AppendLine("[AXIOM COMPUTER AGENT]");
            builder.AppendLine($"You are operating the user's Windows computer directly. Scope: {scope.Describe()}.");
            builder.AppendLine(mode == AgentApprovalMode.Auto
                ? "Approval mode: Auto. Your calls run immediately."
                : "Approval mode: Manual. The user approves each command and file change before it runs; a refusal is a normal answer, not an error.");
            builder.AppendLine();

            if (nativeToolCalling)
                AppendNativeToolProtocol(builder);
            else if (tier == AgentTier.Full)
                AppendFullProtocol(builder);
            else
                AppendFlatProtocol(builder, tier);

            builder.AppendLine();
            builder.AppendLine("Rules:");
            builder.AppendLine("- Look before you change: read a file before editing it, and check a directory before assuming what is in it.");
            builder.AppendLine("- One tool call per message. Stop after it and wait for the result.");
            builder.AppendLine("- Never invent a tool result. If a call fails, read the error and try a different approach.");
            builder.AppendLine("- Prefer the smallest action that answers the question. Do not explore the whole disk for a one-file task.");
            builder.AppendLine("- Commands run through PowerShell, non-interactive: anything that waits for typed input will fail, so pass flags instead.");
            builder.Append("[/AXIOM COMPUTER AGENT]");
            return builder.ToString();
        }

        private static void AppendNativeToolProtocol(StringBuilder builder)
        {
            builder.AppendLine("You have real tools attached to this conversation. Call them; do not describe calling them.");
            builder.AppendLine();
            builder.AppendLine("- run_command runs any shell command on this machine, so \"I cannot access your computer\" is never true here. Launching an app is run_command (for example: Start-Process notepad).");
            builder.AppendLine("- read_file, write_file, edit_file, list_directory, find_files and search_text work on the user's real files.");
            builder.AppendLine();
            builder.AppendLine("Call one tool at a time and wait for its result before the next. When the task is done, reply in plain words with what you did and what you found; that reply ends the run.");
        }

        private static void AppendFullProtocol(StringBuilder builder)
        {
            builder.AppendLine("To act, reply with exactly one JSON object in a ```json block and nothing else:");
            builder.AppendLine("```json");
            builder.AppendLine("{\"tool\": \"run_command\", \"arguments\": {\"command\": \"dotnet build\"}}");
            builder.AppendLine("```");
            builder.AppendLine();
            builder.AppendLine("Tools and their arguments:");
            builder.AppendLine("- run_command: command, optional cwd, optional timeout_seconds");
            builder.AppendLine("- read_file: path, optional start_line, optional line_count");
            builder.AppendLine("- write_file: path, content  (replaces the whole file)");
            builder.AppendLine("- edit_file: path, old_string, new_string  (old_string must appear exactly once)");
            builder.AppendLine("- list_directory: path");
            builder.AppendLine("- find_files: pattern, optional path");
            builder.AppendLine("- search_text: pattern, optional path, optional file_pattern");
            builder.AppendLine("- finish: summary  (call this when the task is done)");
            builder.AppendLine();
            builder.AppendLine("When the work is complete, call finish with a short summary. Any message without a JSON block is treated as your final answer to the user.");
        }

        private static void AppendFlatProtocol(StringBuilder builder, AgentTier tier)
        {
            builder.AppendLine("To act, reply in exactly this format and nothing else:");
            builder.AppendLine();
            builder.AppendLine("TOOL run_command");
            builder.AppendLine("command: dir");
            builder.AppendLine("END");
            builder.AppendLine();
            builder.AppendLine("Available tools:");
            builder.AppendLine("- run_command   with: command");
            builder.AppendLine("- read_file     with: path");
            builder.AppendLine("- list_directory with: path");
            builder.AppendLine("- find_files    with: pattern");
            builder.AppendLine("- search_text   with: pattern");
            builder.AppendLine("- finish        with: summary");

            if (tier == AgentTier.Compact)
            {
                builder.AppendLine();
                builder.AppendLine("To write a file use:");
                builder.AppendLine("TOOL write_file");
                builder.AppendLine("path: C:\\folder\\file.txt");
                builder.AppendLine("content: <<<");
                builder.AppendLine("the full file text");
                builder.AppendLine(">>>");
                builder.AppendLine("END");
            }

            builder.AppendLine();
            builder.AppendLine("Use one tool per reply. When the task is done, reply with TOOL finish and a summary line.");
            builder.AppendLine("Write plain text with no code fences and no extra commentary.");
        }
    }
}
