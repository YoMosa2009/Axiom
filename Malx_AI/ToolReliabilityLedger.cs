using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Malx_AI
{
    /// <summary>
    /// Persistent per-model tool-competence record (same pattern as the native crash ledger,
    /// but for tool use): every model-chosen tool decision is recorded as valid/invalid, every
    /// execution as success/failure, and every observation-echo incident. Size class gives the
    /// PRIOR tool budget; this ledger adjusts it with EVIDENCE — a model that demonstrably
    /// routes well earns an extra call, one that flails is stepped down to deterministic-only —
    /// so tool trust tracks the actual model file, not just its parameter count.
    /// All methods are exception-safe: a ledger problem must never break an inference turn.
    /// </summary>
    internal static class ToolReliabilityLedger
    {
        private sealed class ToolStats
        {
            public int Decisions { get; set; }
            public int ValidDecisions { get; set; }
            public int Executions { get; set; }
            public int SuccessfulExecutions { get; set; }
            public int Echoes { get; set; }
        }

        // Minimum recorded decisions before the ledger overrides the size-class prior.
        private const int MinDecisionsForVerdict = 8;
        private const double StepDownValidityThreshold = 0.4;
        private const double StepUpValidityThreshold = 0.8;
        private const double StepUpSuccessThreshold = 0.6;
        private const int MaxModelChosenTools = 2;

        private static readonly object Gate = new();
        private static Dictionary<string, ToolStats>? _stats;
        private static string? _storagePathOverride;

        private static string StoragePath => _storagePathOverride ?? Path.Combine(AppDataPaths.Root, "tool_reliability.json");

        /// <summary>Redirects persistence for the standalone verification harness.</summary>
        internal static void UseStoragePathForTesting(string path)
        {
            lock (Gate)
            {
                _storagePathOverride = path;
                _stats = null;
            }
        }

        public static void RecordDecision(string modelPathOrName, string tool, bool valid)
        {
            Mutate(modelPathOrName, tool, stats =>
            {
                stats.Decisions++;
                if (valid)
                    stats.ValidDecisions++;
            });
        }

        public static void RecordExecution(string modelPathOrName, string tool, bool success)
        {
            Mutate(modelPathOrName, tool, stats =>
            {
                stats.Executions++;
                if (success)
                    stats.SuccessfulExecutions++;
            });
        }

        public static void RecordEcho(string modelPathOrName)
        {
            Mutate(modelPathOrName, "ANY", stats => stats.Echoes++);
        }

        /// <summary>
        /// Adjusts the size-class tool budget with recorded evidence. A zero prior (sub-1B)
        /// is never raised — the size gate exists because those models cannot route at all,
        /// and eight lucky grammar rolls must not re-open it.
        /// </summary>
        public static int GetTrustAdjustedToolBudget(string modelPathOrName, int sizeClassDefaultBudget)
        {
            if (sizeClassDefaultBudget <= 0)
                return 0;

            try
            {
                string modelKey = BuildModelKey(modelPathOrName);
                if (modelKey.Length == 0)
                    return sizeClassDefaultBudget;

                int decisions = 0, valid = 0, executions = 0, successes = 0;
                lock (Gate)
                {
                    EnsureLoaded();
                    foreach (var pair in _stats!)
                    {
                        if (!pair.Key.StartsWith(modelKey + "|", StringComparison.Ordinal))
                            continue;

                        decisions += pair.Value.Decisions;
                        valid += pair.Value.ValidDecisions;
                        executions += pair.Value.Executions;
                        successes += pair.Value.SuccessfulExecutions;
                    }
                }

                if (decisions < MinDecisionsForVerdict)
                    return sizeClassDefaultBudget;

                double validityRate = (double)valid / decisions;
                if (validityRate < StepDownValidityThreshold)
                    return 0;

                double successRate = executions > 0 ? (double)successes / executions : 0;
                if (validityRate >= StepUpValidityThreshold && executions > 0 && successRate >= StepUpSuccessThreshold)
                    return Math.Min(sizeClassDefaultBudget + 1, MaxModelChosenTools);

                return sizeClassDefaultBudget;
            }
            catch
            {
                return sizeClassDefaultBudget;
            }
        }

        /// <summary>One-line competence summary for activity logs ("12/14 valid, 9/11 ok").</summary>
        public static string DescribeModel(string modelPathOrName)
        {
            try
            {
                string modelKey = BuildModelKey(modelPathOrName);
                int decisions = 0, valid = 0, executions = 0, successes = 0, echoes = 0;
                lock (Gate)
                {
                    EnsureLoaded();
                    foreach (var pair in _stats!)
                    {
                        if (!pair.Key.StartsWith(modelKey + "|", StringComparison.Ordinal))
                            continue;

                        decisions += pair.Value.Decisions;
                        valid += pair.Value.ValidDecisions;
                        executions += pair.Value.Executions;
                        successes += pair.Value.SuccessfulExecutions;
                        echoes += pair.Value.Echoes;
                    }
                }

                return $"decisions {valid}/{decisions} valid, executions {successes}/{executions} ok, echoes {echoes}";
            }
            catch
            {
                return "no ledger data";
            }
        }

        private static void Mutate(string modelPathOrName, string tool, Action<ToolStats> mutation)
        {
            try
            {
                string modelKey = BuildModelKey(modelPathOrName);
                if (modelKey.Length == 0)
                    return;

                string key = modelKey + "|" + LocalToolIntentRouter.NormalizeToolName(tool);
                lock (Gate)
                {
                    EnsureLoaded();
                    if (!_stats!.TryGetValue(key, out ToolStats? stats))
                    {
                        stats = new ToolStats();
                        _stats[key] = stats;
                    }

                    mutation(stats);
                    Save();
                }
            }
            catch
            {
                // Ledger persistence is diagnostics — never let it break an inference turn.
            }
        }

        private static string BuildModelKey(string modelPathOrName)
        {
            if (string.IsNullOrWhiteSpace(modelPathOrName))
                return string.Empty;

            string name;
            try
            {
                name = Path.GetFileNameWithoutExtension(modelPathOrName);
            }
            catch
            {
                name = modelPathOrName;
            }

            return (string.IsNullOrWhiteSpace(name) ? modelPathOrName : name).Trim().ToLowerInvariant();
        }

        private static void EnsureLoaded()
        {
            if (_stats != null)
                return;

            try
            {
                if (File.Exists(StoragePath))
                {
                    string json = File.ReadAllText(StoragePath);
                    _stats = JsonSerializer.Deserialize<Dictionary<string, ToolStats>>(json)
                        ?? new Dictionary<string, ToolStats>(StringComparer.Ordinal);
                    return;
                }
            }
            catch
            {
                // Corrupt/unreadable ledger: start fresh rather than fail the pipeline.
            }

            _stats = new Dictionary<string, ToolStats>(StringComparer.Ordinal);
        }

        private static void Save()
        {
            string json = JsonSerializer.Serialize(_stats, new JsonSerializerOptions { WriteIndented = true });
            string tempPath = StoragePath + ".tmp";
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, StoragePath, overwrite: true);
        }
    }
}
