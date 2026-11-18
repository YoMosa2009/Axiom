using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;

namespace Malx_AI
{
    /// <summary>Full per-chat evidence, independent of a model's finite prompt window.</summary>
    public sealed class ConversationEvidence : IDisposable
    {
        private static readonly AsyncLocal<ConversationEvidence?> Active = new();
        private static readonly object Gate = new();
        private readonly ConversationEvidence? _previous;
        private readonly HashSet<OpenRouterMessage> _mirroredToolResults = new(ReferenceEqualityComparer.Instance);
        public static ConversationEvidence? Current => Active.Value;
        public string DirectoryPath { get; }
        private string HistoryPath => Path.Combine(DirectoryPath, "history.md");
        public const string Instructions = "[CONVERSATION EVIDENCE] Full earlier chat and tool records are retained. If the prompt omits an earlier detail, call recall_context with a literal query, or page by offset. Do not guess, restart completed work, or assume a shortened observation is complete. In Agent Access, archived image paths can be read with read_file.";

        public ConversationEvidence(string root, string chatKey, string conversation)
        {
            if (string.IsNullOrWhiteSpace(chatKey) || chatKey is "." or ".." || chatKey.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) throw new ArgumentException("Invalid chat key.");
            DirectoryPath = Path.Combine(root, "ConversationEvidence", chatKey);
            Directory.CreateDirectory(DirectoryPath);
            _previous = Active.Value;
            Active.Value = this;
            Record("Conversation at turn start", conversation);
        }
        public static OpenRouterToolDefinition ToolDefinition => new("recall_context",
            "Retrieve complete earlier conversation and tool evidence for this chat. Search with a literal query or page using offset; the result gives the next offset. Recover omitted history instead of repeating work.",
            new JsonObject { ["type"] = "object", ["properties"] = new JsonObject {
                ["query"] = new JsonObject { ["type"] = "string" },
                ["offset"] = new JsonObject { ["type"] = "integer" },
                ["limit"] = new JsonObject { ["type"] = "integer" } } });
        public void Record(string kind, string? content)
        {
            lock (Gate) AppendRecord(kind, content);
        }

        internal void RecordTransportToolResult(OpenRouterMessage message)
        {
            lock (Gate)
            {
                // Replayed history contains the same immutable message instance. Distinct messages
                // can share a provider ID and payload, so those alone cannot identify a replay.
                bool hasCallId = !string.IsNullOrWhiteSpace(message.ToolCallId);
                if (hasCallId && _mirroredToolResults.Contains(message))
                    return;

                AppendRecord("Transport tool evidence", message.Text);
                if (hasCallId)
                    _mirroredToolResults.Add(message);
            }
        }

        private void AppendRecord(string kind, string? content) =>
            File.AppendAllText(HistoryPath, $"\n## {DateTime.UtcNow:O} — {kind}\n{content}\n", new UTF8Encoding(false));

        public string SaveImage(byte[] bytes, string extension)
        {
            string path = Path.Combine(DirectoryPath, Guid.NewGuid().ToString("N") + (extension == ".png" ? ".png" : ".jpg"));
            File.WriteAllBytes(path, bytes);
            Record("Image evidence", path);
            return path;
        }
        public bool OwnsImage(string path)
        {
            try { return Path.GetDirectoryName(Path.GetFullPath(path))?.Equals(Path.GetFullPath(DirectoryPath), StringComparison.OrdinalIgnoreCase) == true
                && (Path.GetExtension(path).Equals(".jpg", StringComparison.OrdinalIgnoreCase) || Path.GetExtension(path).Equals(".png", StringComparison.OrdinalIgnoreCase)); }
            catch { return false; }
        }
        public string Read(string? query = null, int offset = 0, int limit = 3500)
        {
            string full;
            lock (Gate) full = File.Exists(HistoryPath) ? File.ReadAllText(HistoryPath) : "";
            int start = Math.Clamp(offset, 0, full.Length);
            if (!string.IsNullOrWhiteSpace(query))
            {
                int found = full.IndexOf(query, start, StringComparison.OrdinalIgnoreCase);
                if (found < 0) return $"No match after offset {start}. Full record: {full.Length} characters; use offset=0 without query to browse.";
                start = Math.Max(start, found - 200);
            }
            int end = Math.Min(full.Length, start + Math.Clamp(limit, 128, 5000));
            return $"[Full evidence: chars {start}..{end} of {full.Length}; next offset={end}; end={end == full.Length}]\n" + full[start..end];
        }
        public static string RecallJson(string json)
        {
            if (Current == null) return "No active conversation evidence archive.";
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            var root = doc.RootElement;
            return Current.Read(root.TryGetProperty("query", out var q) ? q.GetString() : null,
                root.TryGetProperty("offset", out var o) && o.TryGetInt32(out int offset) ? offset : 0,
                root.TryGetProperty("limit", out var l) && l.TryGetInt32(out int limit) ? limit : 3500);
        }
        public void Dispose() => Active.Value = _previous;
    }
}
