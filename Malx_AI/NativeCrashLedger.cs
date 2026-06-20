using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace Malx_AI
{
    /// <summary>
    /// Self-healing crash-loop breaker. A llama.cpp native abort (CUDA illegal memory access,
    /// GGML_ASSERT, OOM) kills the process with no catchable managed exception, so we cannot
    /// recover in-flight. Instead, the in-flight decode marker (NativeDecodeForensics) left
    /// behind by a dead session is consumed at the NEXT launch and recorded here as a "strike"
    /// against that model's GPU configuration. On the next load of a struck model the app
    /// forces CPU — stable everywhere — so the user is never trapped in a launch→crash loop,
    /// regardless of the crash's root cause or their hardware. A clean run clears the strike,
    /// so a one-off blip (e.g. a transient VRAM shortage) self-heals back to GPU.
    /// </summary>
    public static class NativeCrashLedger
    {
        private static readonly object Gate = new();
        private static string LedgerPath => Path.Combine(AppDataPaths.Root, "crash-ledger.json");

        private sealed class Entry
        {
            public int GpuStrikes { get; set; }
            public string LastStage { get; set; } = string.Empty;
            public int LastCtx { get; set; }
            public string LastSeenUtc { get; set; } = string.Empty;
        }

        private static Dictionary<string, Entry> Load()
        {
            try
            {
                if (!File.Exists(LedgerPath))
                    return new(StringComparer.OrdinalIgnoreCase);

                string json = File.ReadAllText(LedgerPath);
                var data = JsonSerializer.Deserialize<Dictionary<string, Entry>>(json);
                return data == null
                    ? new(StringComparer.OrdinalIgnoreCase)
                    : new(data, StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[NativeCrashLedger] load failed: {ex.Message}");
                return new(StringComparer.OrdinalIgnoreCase);
            }
        }

        private static void Save(Dictionary<string, Entry> data)
        {
            try
            {
                Directory.CreateDirectory(AppDataPaths.Root);
                File.WriteAllText(LedgerPath, JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[NativeCrashLedger] save failed: {ex.Message}");
            }
        }

        private static string Key(string modelPath)
        {
            try { return Path.GetFullPath(modelPath).ToLowerInvariant(); }
            catch { return (modelPath ?? string.Empty).ToLowerInvariant(); }
        }

        /// <summary>
        /// Records a GPU-backed native crash for a model. Only GPU crashes are tracked because
        /// the only step-down we can apply is GPU → CPU; a CPU crash has nowhere safer to go.
        /// </summary>
        public static void RecordGpuCrash(string modelPath, string stage, int ctx)
        {
            if (string.IsNullOrWhiteSpace(modelPath))
                return;

            lock (Gate)
            {
                var data = Load();
                string key = Key(modelPath);
                if (!data.TryGetValue(key, out Entry? entry) || entry == null)
                {
                    entry = new Entry();
                    data[key] = entry;
                }

                entry.GpuStrikes++;
                entry.LastStage = stage ?? string.Empty;
                entry.LastCtx = ctx;
                entry.LastSeenUtc = DateTime.UtcNow.ToString("o");
                Save(data);
            }
        }

        /// <summary>True when this model previously died under GPU and has not since run cleanly.</summary>
        public static bool ShouldForceCpu(string modelPath)
        {
            if (string.IsNullOrWhiteSpace(modelPath))
                return false;

            lock (Gate)
            {
                var data = Load();
                return data.TryGetValue(Key(modelPath), out Entry? entry) && entry is { GpuStrikes: > 0 };
            }
        }

        /// <summary>
        /// Number of unresolved GPU crashes recorded for this model (0 when it has run cleanly
        /// since, or never crashed). Used to escalate recovery: first strike retries GPU with a
        /// smaller context (less VRAM pressure), repeated strikes fall back to CPU.
        /// </summary>
        public static int GetGpuStrikes(string modelPath)
        {
            if (string.IsNullOrWhiteSpace(modelPath))
                return 0;

            lock (Gate)
            {
                var data = Load();
                return data.TryGetValue(Key(modelPath), out Entry? entry) && entry != null
                    ? entry.GpuStrikes
                    : 0;
            }
        }

        /// <summary>
        /// Clears a model's strikes after a clean, completed turn so a one-off crash does not
        /// pin the model to CPU forever. Cheap no-op when the model has no recorded strikes.
        /// </summary>
        public static void RegisterCleanRun(string modelPath)
        {
            if (string.IsNullOrWhiteSpace(modelPath))
                return;

            lock (Gate)
            {
                var data = Load();
                if (data.Remove(Key(modelPath)))
                    Save(data);
            }
        }

        /// <summary>
        /// Parses a crash marker left by NativeDecodeForensics and records a GPU strike when the
        /// dead session was running on CUDA. Returns the model display name ONLY when a strike
        /// was actually recorded (so callers can honestly say it will load on CPU), else null.
        /// </summary>
        public static string? RecordFromCrashMarker(string? marker)
        {
            if (string.IsNullOrWhiteSpace(marker))
                return null;

            string backend = MarkerField(marker, "Backend:");
            string modelPath = MarkerField(marker, "ModelPath:");
            string stage = MarkerField(marker, "Stage:");
            string model = MarkerField(marker, "Model:");
            int.TryParse(MarkerField(marker, "Ctx:"), out int ctx);

            if (string.Equals(backend, "CUDA", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(modelPath))
            {
                RecordGpuCrash(modelPath, stage, ctx);
                return string.IsNullOrWhiteSpace(model) ? Path.GetFileName(modelPath) : model;
            }

            return null;
        }

        // Pipe-delimited "Key:Value" fields. ModelPath is the marker's last field, so taking
        // text up to the next " | " (or end of string) tolerates ':' and spaces in paths.
        private static string MarkerField(string marker, string key)
        {
            int i = marker.IndexOf(key, StringComparison.Ordinal);
            if (i < 0)
                return string.Empty;

            i += key.Length;
            int j = marker.IndexOf(" | ", i, StringComparison.Ordinal);
            return (j < 0 ? marker[i..] : marker[i..j]).Trim();
        }
    }
}
