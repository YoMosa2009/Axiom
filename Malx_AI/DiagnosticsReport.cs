using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Malx_AI
{
    /// <summary>One anonymous diagnostics event. Field names match the gateway's /v1/diagnostics input.</summary>
    internal sealed record DiagnosticsEvent(
        string Kind,
        string Area,
        string ExceptionType,
        string Message,
        string Stack,
        string Fingerprint,
        int Occurrences,
        string Detail,
        string OccurredAt);

    /// <summary>
    /// Builds anonymous diagnostics: what failed and where in Axiom's code, never what the user
    /// was doing. Exception messages are scrubbed of keys, paths, emails, URLs and quoted text;
    /// stack traces keep only method names (no file paths or source lines). Prompts, replies,
    /// file contents, notes and account details are never collected.
    /// </summary>
    internal static class DiagnosticsReport
    {
        public const int MaxMessageChars = 300;
        public const int MaxStackFrames = 12;
        public const int MaxPendingEvents = 100;
        private static readonly Regex AreaToken = new(@"[^A-Za-z0-9 ._:/+()\-]", RegexOptions.Compiled);
        private static readonly Regex InstallId = new("^[0-9a-f]{32}$", RegexOptions.Compiled);

        public static DiagnosticsEvent FromException(string area, Exception exception, bool crash, DateTimeOffset now)
        {
            string type = exception.GetType().FullName ?? exception.GetType().Name;
            var inner = new List<string>();
            for (Exception? e = exception.InnerException; e is not null && inner.Count < 3; e = e.InnerException)
                inner.Add(e.GetType().FullName ?? e.GetType().Name);
            string stack = StackFrames(exception);
            string cleanArea = CleanArea(area);
            string fingerprint = Fingerprint(cleanArea + "|" + type + "|" + string.Join("|", stack.Split('\n').Take(5)));
            string detail = inner.Count == 0 ? string.Empty : "inner: " + string.Join(" > ", inner);
            return new DiagnosticsEvent(crash ? "crash" : "error", cleanArea, type, Scrub(exception.Message), stack,
                fingerprint, 1, detail, now.UtcDateTime.ToString("O"));
        }

        /// <summary>A usage count, e.g. area "Chat.Send" and detail "cloud". Counts only, no content.</summary>
        public static DiagnosticsEvent Feature(string area, string detail, int count, DateTimeOffset now)
            => new("feature", CleanArea(area), string.Empty, string.Empty, string.Empty,
                Fingerprint("feature|" + area + "|" + detail), Math.Max(1, count), CleanArea(detail), now.UtcDateTime.ToString("O"));

        public static DiagnosticsEvent Session(string detail, DateTimeOffset now)
            => new("session", "App.Start", string.Empty, string.Empty, string.Empty,
                Fingerprint("session|" + detail), 1, CleanArea(detail), now.UtcDateTime.ToString("O"));

        /// <summary>
        /// Adds an event to the pending list: a repeat of a known fingerprint only raises its
        /// count, and the list never grows past <see cref="MaxPendingEvents"/>.
        /// </summary>
        public static void Merge(List<DiagnosticsEvent> pending, DiagnosticsEvent next)
        {
            int existing = pending.FindIndex(e => e.Kind == next.Kind && e.Fingerprint == next.Fingerprint);
            if (existing >= 0)
            {
                var current = pending[existing];
                pending[existing] = current with { Occurrences = Math.Min(10_000, current.Occurrences + next.Occurrences) };
                return;
            }
            if (pending.Count >= MaxPendingEvents)
            {
                // Keep crashes and errors over usage counts when the list is full.
                int drop = pending.FindIndex(e => e.Kind is "feature" or "session");
                if (drop < 0 || next.Kind is "feature" or "session")
                    return;
                pending.RemoveAt(drop);
            }
            pending.Add(next);
        }

        public static string BuildPayload(string installId, string appVersion, string os, IEnumerable<DiagnosticsEvent> events)
        {
            if (!InstallId.IsMatch(installId))
                throw new ArgumentException("Install id must be 32 lowercase hex characters.", nameof(installId));
            return JsonSerializer.Serialize(new
            {
                installId,
                appVersion = CleanArea(appVersion),
                os = CleanArea(os),
                events = events.Take(50).ToList()
            }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        }

        public static string NewInstallId() => Guid.NewGuid().ToString("N");

        public static bool IsValidInstallId(string? value) => value is not null && InstallId.IsMatch(value);

        /// <summary>Removes secrets and personal details from an exception message and caps its length.</summary>
        public static string Scrub(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return string.Empty;
            // Paths first, before key redaction rewrites parts of them. Folder segments may contain
            // spaces (they end at a backslash); the final file name may not.
            string result = Regex.Replace(text, @"(?i)(?:[A-Z]:\\|\\\\)(?:[^\\\r\n'""<>|]*\\)*[^\s\\'""<>|]*", "<path>");
            result = FeedbackReportBuilder.Redact(result);
            result = Regex.Replace(result, @"(?i)\b(?:https?|wss?|file)://[^\s'""<>]+", m =>
                Uri.TryCreate(m.Value, UriKind.Absolute, out var uri) && !uri.IsFile ? $"{uri.Scheme}://{uri.Host}/…" : "<url>");
            result = Regex.Replace(result, @"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}", "<email>");
            result = Regex.Replace(result, @"(?<![A-Za-z0-9])/(?:home|Users|mnt|var|tmp)/[^\s'""<>]*", "<path>");
            // Quoted fragments are where user text usually lands ("Could not parse 'their words'").
            result = Regex.Replace(result, @"'[^'\r\n]{24,}'|""[^""\r\n]{24,}""", "<text>");
            result = Regex.Replace(result, @"\s+", " ").Trim();
            return result.Length <= MaxMessageChars ? result : result[..MaxMessageChars] + "…";
        }

        internal static string StackFrames(Exception exception)
        {
            var lines = new List<string>();
            try
            {
                foreach (StackFrame frame in new StackTrace(exception, fNeedFileInfo: false).GetFrames())
                {
                    var method = frame.GetMethod();
                    if (method is null)
                        continue;
                    string owner = method.DeclaringType?.FullName ?? "?";
                    lines.Add($"{owner}.{method.Name}");
                    if (lines.Count >= MaxStackFrames)
                        break;
                }
            }
            catch
            {
                // A stack that can't be read still leaves a useful type and area.
            }
            return string.Join("\n", lines);
        }

        internal static string CleanArea(string? value)
        {
            string cleaned = AreaToken.Replace(value ?? string.Empty, string.Empty).Trim();
            if (cleaned.Length == 0)
                cleaned = "unknown";
            return cleaned.Length <= 80 ? cleaned : cleaned[..80];
        }

        private static string Fingerprint(string value)
            => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..16].ToLowerInvariant();
    }
}
