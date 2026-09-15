using System;
using System.IO;
using System.Text.Json;

namespace Malx_AI
{
    public enum EffortLevel
    {
        Light,
        Medium,
        High,
        ExtraHigh,
        Ultra
    }

    /// <summary>
    /// One shared, persisted effort preference for every Axiom chat surface. The policy changes
    /// concrete budgets; it never promises hidden model capability that a local model lacks.
    /// </summary>
    public static class EffortPreferenceStore
    {
        private sealed class PersistedEffort { public EffortLevel Level { get; set; } = EffortLevel.Medium; }
        private static readonly object Gate = new();
        private static readonly string Path = System.IO.Path.Combine(AppDataPaths.ChatHistory, "effort_preference.json");
        private static bool _loaded;
        private static EffortLevel _current = EffortLevel.Medium;

        public static event Action<EffortLevel>? Changed;

        public static EffortLevel Current
        {
            get
            {
                lock (Gate)
                {
                    EnsureLoaded();
                    return _current;
                }
            }
        }

        public static void Set(EffortLevel level)
        {
            Action<EffortLevel>? changed;
            lock (Gate)
            {
                EnsureLoaded();
                if (_current == level)
                    return;

                _current = level;
                try
                {
                    Directory.CreateDirectory(AppDataPaths.ChatHistory);
                    AtomicFileWriter.WriteAllText(Path, JsonSerializer.Serialize(new PersistedEffort { Level = level }));
                }
                catch
                {
                    // The selection remains effective for this process even if storage is unavailable.
                }
                changed = Changed;
            }
            changed?.Invoke(level);
        }

        private static void EnsureLoaded()
        {
            if (_loaded)
                return;

            _loaded = true;
            try
            {
                var result = JsonPersistenceRecovery.Load<PersistedEffort>(Path);
                if (result.Value != null && Enum.IsDefined(result.Value.Level))
                    _current = result.Value.Level;
            }
            catch
            {
                _current = EffortLevel.Medium;
            }
        }
    }

    internal static class EffortPolicy
    {
        public static string DisplayName(EffortLevel level) => level switch
        {
            EffortLevel.ExtraHigh => "Extra High",
            _ => level.ToString()
        };

        public static string CloudReasoningEffort(EffortLevel level) => level switch
        {
            EffortLevel.Light => "low",
            EffortLevel.Medium => "medium",
            EffortLevel.High or EffortLevel.ExtraHigh or EffortLevel.Ultra => "high",
            _ => "medium"
        };

        public static bool RequestsReasoning(bool userEnabled)
            => userEnabled || Current >= EffortLevel.High;

        public static EffortLevel Current => EffortPreferenceStore.Current;

        public static int ScaleGenerationTokens(int baseline, LocalModelCapabilityProfile? capability, int upperBound)
        {
            if (baseline <= 0)
                return baseline;

            double multiplier = GetGenerationMultiplier(Current, capability?.SizeClass ?? LocalModelSizeClass.Unknown);
            int scaled = (int)Math.Round(baseline * multiplier, MidpointRounding.AwayFromZero);
            return Math.Clamp(scaled, Math.Min(128, upperBound), Math.Max(128, upperBound));
        }

        public static int ScaleToolBudget(int baseline, LocalModelCapabilityProfile? capability)
        {
            if (baseline <= 0)
                return 0;

            int extra = Current switch
            {
                EffortLevel.Light => -1,
                EffortLevel.High => 1,
                EffortLevel.ExtraHigh => 2,
                EffortLevel.Ultra => 3,
                _ => 0
            };
            if (capability?.IsSubOneB == true)
                extra = Math.Min(extra, 0);
            if (capability?.IsCompactClass == true)
                extra = Math.Min(extra, 1);
            return Math.Max(0, baseline + extra);
        }

        public static string BuildSystemInstruction()
        {
            return Current switch
            {
                EffortLevel.Light => "[EFFORT: Light] Answer directly. Use tools only when essential and avoid unnecessary deliberation.",
                EffortLevel.Medium => "[EFFORT: Medium] Use a proportionate plan, verify important claims, and use tools when they materially improve the answer.",
                EffortLevel.High => "[EFFORT: High] Plan before acting, verify important intermediate results, and use relevant tools or checks when they improve correctness.",
                EffortLevel.ExtraHigh => "[EFFORT: Extra High] Work methodically through the task, verify intermediate results, and spend additional time checking requirements before finalizing.",
                EffortLevel.Ultra => "[EFFORT: Ultra] Use the available budget deliberately: plan, inspect evidence, verify critical intermediate results, recover from failures, and complete every user requirement before finalizing.",
                _ => string.Empty
            };
        }

        private static double GetGenerationMultiplier(EffortLevel level, LocalModelSizeClass size) => size switch
        {
            LocalModelSizeClass.SubOneB => level switch
            {
                EffortLevel.Light => 0.65,
                EffortLevel.Medium => 0.85,
                _ => 1.0
            },
            LocalModelSizeClass.OneToFourB => level switch
            {
                EffortLevel.Light => 0.7,
                EffortLevel.Medium => 0.9,
                EffortLevel.High => 1.0,
                EffortLevel.ExtraHigh => 1.1,
                _ => 1.2
            },
            LocalModelSizeClass.FourToTenB => level switch
            {
                EffortLevel.Light => 0.7,
                EffortLevel.Medium => 0.9,
                EffortLevel.High => 1.05,
                EffortLevel.ExtraHigh => 1.25,
                _ => 1.45
            },
            _ => level switch
            {
                EffortLevel.Light => 0.7,
                EffortLevel.Medium => 0.9,
                EffortLevel.High => 1.1,
                EffortLevel.ExtraHigh => 1.35,
                _ => 1.6
            }
        };
    }
}
