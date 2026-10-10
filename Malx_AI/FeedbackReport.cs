using System;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Malx_AI
{
    internal enum FeedbackKind { Problem, Idea, Other }

    internal sealed record FeedbackSubmission(
        FeedbackKind Kind,
        string Message,
        string? ReplyEmail,
        bool IncludeDiagnostics,
        string AppVersion,
        string WindowsVersion,
        string HardwareSummary,
        string InferenceMode,
        string RecentErrorLog);

    internal sealed record FeedbackReport(string Subject, string Body);

    /// <summary>Builds the text of a feedback report and keeps secrets out of it.</summary>
    internal static class FeedbackReportBuilder
    {
        public const int MaxMessageChars = 5000;
        public const int MaxLogLines = 40;
        public const int MaxLogChars = 4000;
        public const string GitHubRepository = "YoMosa2009/Axiom";

        public static FeedbackReport Build(FeedbackSubmission submission)
        {
            string message = Redact((submission.Message ?? "").Trim());
            if (message.Length > MaxMessageChars)
                message = message.Substring(0, MaxMessageChars);

            string firstLine = "";
            foreach (var line in message.Split('\n'))
            {
                var trimmed = line.Trim();
                if (!string.IsNullOrEmpty(trimmed))
                {
                    firstLine = Regex.Replace(trimmed, @"\s+", " ");
                    break;
                }
            }
            if (string.IsNullOrEmpty(firstLine))
                firstLine = "(no message)";
            if (firstLine.Length > 60)
                firstLine = firstLine.Substring(0, 60).TrimEnd() + "\u2026";

            string kindLabel = submission.Kind switch
            {
                FeedbackKind.Problem => "Problem",
                FeedbackKind.Idea => "Idea",
                FeedbackKind.Other => "Feedback",
                _ => "Feedback"
            };

            string subject = $"[Axiom {submission.AppVersion}] {kindLabel}: {firstLine}";

            var bodyLines = new List<string>();
            bodyLines.Add("Type: " + kindLabel);
            bodyLines.Add("Reply to: " + (IsPlausibleEmail(submission.ReplyEmail) ? submission.ReplyEmail!.Trim() : "(not given)"));
            bodyLines.Add("");
            bodyLines.Add(message);

            if (submission.IncludeDiagnostics)
            {
                string log;
                if (string.IsNullOrWhiteSpace(submission.RecentErrorLog))
                {
                    log = "(none)";
                }
                else
                {
                    log = Redact(TailLines(submission.RecentErrorLog, MaxLogLines, MaxLogChars));
                }

                bodyLines.Add("");
                bodyLines.Add("--- Technical details ---");
                bodyLines.Add("App version: " + submission.AppVersion);
                bodyLines.Add("Windows: " + submission.WindowsVersion);
                bodyLines.Add("Hardware: " + submission.HardwareSummary);
                bodyLines.Add("Mode: " + submission.InferenceMode);
                bodyLines.Add("");
                bodyLines.Add("Recent errors:");
                bodyLines.Add(log);
            }

            return new FeedbackReport(subject, string.Join("\n", bodyLines));
        }

        public static string Redact(string text)
        {
            if (string.IsNullOrEmpty(text))
                return "";

            string result = text;
            result = Regex.Replace(result, @"sk-or-v1-[A-Za-z0-9]{16,}", "[redacted key]");
            result = Regex.Replace(result, @"sk-[A-Za-z0-9_\-]{16,}", "[redacted key]");
            result = Regex.Replace(result, @"(?i)bearer\s+[A-Za-z0-9._\-]{12,}", "Bearer [redacted]");
            result = Regex.Replace(result, @"gh[pousr]_[A-Za-z0-9]{20,}", "[redacted key]");
            result = Regex.Replace(result, @"github_pat_[A-Za-z0-9_]{20,}", "[redacted key]");
            result = Regex.Replace(result, @"AIza[0-9A-Za-z_\-]{30,}", "[redacted key]");
            result = Regex.Replace(result, @"(?i)((?:api[_-]?key|token|secret|password)[""']?\s*[:=]\s*[""']?)[^\s""',;]+", "$1[redacted]");
            result = Regex.Replace(result, @"(?i)([A-Z]:\\Users\\)[^\\\s]+", "$1<user>");

            return result;
        }

        public static string TailLines(string text, int maxLines, int maxChars)
        {
            if (string.IsNullOrWhiteSpace(text))
                return "";

            var lines = text.Split('\n');
            var lastLines = lines.Skip(Math.Max(0, lines.Length - maxLines));
            var cleaned = lastLines.Select(l => l.TrimEnd('\r'));
            var result = string.Join("\n", cleaned);

            if (result.Length > maxChars)
                result = result.Substring(result.Length - maxChars);

            return result;
        }

        public static string BuildMailtoUri(string toAddress, FeedbackReport report, int maxBodyChars)
        {
            string body = report.Body;
            if (body.Length > maxBodyChars)
                body = body.Substring(0, maxBodyChars) + "\n[...trimmed; the full report is on your clipboard]";

            return "mailto:" + toAddress + "?subject=" + Uri.EscapeDataString(report.Subject) + "&body=" + Uri.EscapeDataString(body);
        }

        /// <summary>
        /// A link that opens GitHub's new-issue form for this report, with the fields filled in.
        /// GitHub issues are public, so this uses the same scrubbed text as the email report.
        /// </summary>
        public static string BuildGitHubIssueUri(FeedbackSubmission submission, int maxFieldChars)
        {
            var report = Build(submission);
            string template = submission.Kind == FeedbackKind.Idea ? "feature_request.yml" : "bug_report.yml";
            string messageField = submission.Kind == FeedbackKind.Idea ? "idea" : "what-happened";
            string title = report.Subject;
            string message = Redact((submission.Message ?? "").Trim());
            if (message.Length > maxFieldChars)
                message = message.Substring(0, maxFieldChars);

            string details = "";
            if (submission.IncludeDiagnostics)
            {
                string technicalHeader = "--- Technical details ---";
                int idx = report.Body.IndexOf(technicalHeader, StringComparison.Ordinal);
                if (idx >= 0)
                {
                    details = report.Body.Substring(idx + technicalHeader.Length).Trim();
                    if (details.Length > maxFieldChars)
                        details = details.Substring(0, maxFieldChars);
                }
            }

            string uri = "https://github.com/" + GitHubRepository + "/issues/new?template=" + template
                + "&title=" + Uri.EscapeDataString(title)
                + "&" + messageField + "=" + Uri.EscapeDataString(message)
                + "&version=" + Uri.EscapeDataString(submission.AppVersion);

            if (!string.IsNullOrEmpty(details))
                uri += "&details=" + Uri.EscapeDataString(details);

            return uri;
        }

        public static bool IsPlausibleEmail(string? email)
        {
            if (string.IsNullOrWhiteSpace(email))
                return false;

            var trimmed = email.Trim();
            if (trimmed.Length > 254)
                return false;

            return Regex.IsMatch(trimmed, @"^[^@\s]+@[^@\s]+\.[^@\s]+$");
        }
    }
}
