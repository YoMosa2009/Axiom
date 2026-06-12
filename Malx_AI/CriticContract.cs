using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Malx_AI
{
    public sealed class CriticIssue
    {
        public string Severity { get; set; } = "medium";
        public string Summary { get; set; } = "";
        public string Evidence { get; set; } = "";
        public string SuggestedFix { get; set; } = "";
    }

    public sealed class CriticReport
    {
        public string Status { get; set; } = "issues";
        public List<CriticIssue>? Issues { get; set; } = new();
        public bool HasIssues { get; set; }
        public int FindingsCount { get; set; }
    }

    public static class CriticContractParser
    {
        private static readonly string[] CleanPassPhrases =
        [
            "no issues found",
            "no issues detected",
            "no problems found",
            "no problems detected",
            "output is correct",
            "the output is correct",
            "everything looks correct",
            "everything is correct",
            "looks good",
            "no errors found",
            "meets the requirements",
            "fulfills the requirements",
            "no changes needed",
            "no revisions needed"
        ];

        private static readonly Regex NumberedFindingPattern = new(@"\b\d+[\.|\)]", RegexOptions.Compiled);
        private static readonly Regex StructuredFieldRegex = new(@"^(?<field>Reference|Issue|Problem|Fix|SuggestedFix|Suggested Fix|Severity|Evidence|Location)\s*:\s*(?<value>.+)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex SeverityRegex = new(@"\b(low|medium|high|critical)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public static string ContractInstruction =>
            "\n[STRUCTURED OUTPUT CONTRACT] Output valid JSON only with this schema: " +
            "{\"status\":\"ok|issues\",\"issues\":[{\"severity\":\"low|medium|high|critical\",\"summary\":\"...\",\"evidence\":\"...\",\"suggestedFix\":\"...\"}]} " +
            "If there are no issues, output exactly: {\"status\":\"ok\",\"issues\":[]}.";

        public static CriticReport Parse(string criticOutput)
        {
            if (TryParseJson(criticOutput, out var report))
            {
                NormalizeReportState(report, criticOutput);
                return report;
            }

            var fallback = ParseFallback(criticOutput);
            NormalizeReportState(fallback, criticOutput);
            return fallback;
        }

        public static bool ContainsNumberedFindingPattern(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return false;

            return NumberedFindingPattern.IsMatch(text);
        }

        public static bool IsExplicitCleanPass(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return false;

            bool hasCleanPhrase = CleanPassPhrases.Any(p => text.Contains(p, StringComparison.OrdinalIgnoreCase));
            if (!hasCleanPhrase)
                return false;

            return !ContainsNumberedFindingPattern(text);
        }

        private static bool TryParseJson(string text, out CriticReport report)
        {
            report = new CriticReport();
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            string payload = text.Trim();
            int first = payload.IndexOf('{');
            int last = payload.LastIndexOf('}');
            if (first < 0 || last <= first)
            {
                return false;
            }

            payload = payload[first..(last + 1)];

            try
            {
                report = JsonSerializer.Deserialize<CriticReport>(payload, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                }) ?? new CriticReport();

                if (report.Issues == null)
                {
                    report.Issues = new List<CriticIssue>();
                    report.HasIssues = false;
                }

                report.Issues = report.Issues
                    .Where(i => i != null && !string.IsNullOrWhiteSpace(i.Summary))
                    .ToList();

                if (string.Equals(report.Status, "ok", StringComparison.OrdinalIgnoreCase) && report.Issues.Count == 0)
                    return true;

                if (report.Issues.Count > 0)
                    return true;

                return IsExplicitCleanPass(text);
            }
            catch
            {
                return false;
            }
        }

        private static CriticReport ParseFallback(string text)
        {
            var report = new CriticReport();
            if (string.IsNullOrWhiteSpace(text))
            {
                return report;
            }

            if (IsExplicitCleanPass(text) || text.Contains("\"status\":\"ok\"", StringComparison.OrdinalIgnoreCase))
            {
                report.Status = "ok";
                report.Issues = new List<CriticIssue>();
                return report;
            }

            List<CriticIssue> structuredIssues = ExtractStructuredIssues(text);
            if (structuredIssues.Count > 0)
            {
                report.Status = "issues";
                report.Issues = structuredIssues;
                return report;
            }

            var lines = text.Split('\n')
                .Select(l => l.Trim())
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .ToList();

            foreach (var line in lines)
            {
                bool numbered = line.Length > 1 && char.IsDigit(line[0]) && line.IndexOfAny(['.', ')']) is int sep && sep > 0 && sep < 4;
                if (!numbered)
                {
                    continue;
                }

                report.Issues.Add(new CriticIssue
                {
                    Severity = GuessSeverity(line),
                    Summary = StripNumbering(line),
                    Evidence = ExtractEvidence(line),
                    SuggestedFix = ExtractSuggestedFix(line)
                });
            }

            if (report.Issues.Count == 0)
            {
                report.Issues.Add(new CriticIssue
                {
                    Severity = GuessSeverity(text),
                    Summary = text.Length > 200 ? text[..200] : text
                });
            }

            return report;
        }

        private static List<CriticIssue> ExtractStructuredIssues(string text)
        {
            var issues = new List<CriticIssue>();
            if (string.IsNullOrWhiteSpace(text))
                return issues;

            var groups = Regex.Split(text.Trim(), @"\r?\n\s*\r?\n")
                .Select(group => group.Trim())
                .Where(group => !string.IsNullOrWhiteSpace(group))
                .ToList();

            foreach (string group in groups)
            {
                CriticIssue? issue = TryParseStructuredGroup(group);
                if (issue != null)
                    issues.Add(issue);
            }

            return issues;
        }

        private static CriticIssue? TryParseStructuredGroup(string group)
        {
            var lines = group.Split('\n')
                .Select(line => line.Trim())
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .ToList();
            if (lines.Count == 0)
                return null;

            string summary = string.Empty;
            string evidence = string.Empty;
            string fix = string.Empty;
            string severity = "medium";
            bool parsedAnyField = false;

            foreach (string line in lines)
            {
                Match fieldMatch = StructuredFieldRegex.Match(StripNumbering(line));
                if (!fieldMatch.Success)
                    continue;

                parsedAnyField = true;
                string field = fieldMatch.Groups["field"].Value;
                string value = fieldMatch.Groups["value"].Value.Trim();
                switch (field.ToLowerInvariant())
                {
                    case "issue":
                    case "problem":
                        summary = value;
                        break;
                    case "reference":
                    case "location":
                    case "evidence":
                        evidence = string.IsNullOrWhiteSpace(evidence) ? value : evidence + " | " + value;
                        break;
                    case "fix":
                    case "suggestedfix":
                    case "suggested fix":
                        fix = value;
                        break;
                    case "severity":
                        severity = GuessSeverity(value);
                        break;
                }
            }

            if (!parsedAnyField)
                return null;

            if (string.IsNullOrWhiteSpace(summary))
                summary = lines.Select(StripNumbering).FirstOrDefault(line => !StructuredFieldRegex.IsMatch(line)) ?? "Issue reported";

            return new CriticIssue
            {
                Severity = severity,
                Summary = summary,
                Evidence = evidence,
                SuggestedFix = fix
            };
        }

        private static void NormalizeReportState(CriticReport report, string rawOutput)
        {
            report.Issues ??= new List<CriticIssue>();

            if (IsExplicitCleanPass(rawOutput) && !ContainsNumberedFindingPattern(rawOutput))
            {
                report.Status = "ok";
                report.Issues.Clear();
                report.HasIssues = false;
                report.FindingsCount = 0;
                return;
            }

            report.FindingsCount = report.Issues.Count;
            report.HasIssues = !string.Equals(report.Status, "ok", StringComparison.OrdinalIgnoreCase) && report.FindingsCount > 0;
        }

        private static string GuessSeverity(string text)
        {
            string lower = text.ToLowerInvariant();
            Match severityMatch = SeverityRegex.Match(lower);
            if (severityMatch.Success)
                return severityMatch.Groups[1].Value.ToLowerInvariant();
            if (lower.Contains("critical") || lower.Contains("runtime") || lower.Contains("syntax")) return "critical";
            if (lower.Contains("high") || lower.Contains("broken") || lower.Contains("incorrect")) return "high";
            if (lower.Contains("low")) return "low";
            return "medium";
        }

        private static string StripNumbering(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return string.Empty;

            string trimmed = text.Trim();
            if (trimmed.Length > 1 && char.IsDigit(trimmed[0]) && trimmed.IndexOfAny(['.', ')']) is int sep && sep > 0 && sep < 4)
                return trimmed[(sep + 1)..].Trim();

            return trimmed;
        }

        private static string ExtractEvidence(string text)
        {
            string trimmed = StripNumbering(text);
            int becauseIndex = trimmed.IndexOf(" because ", StringComparison.OrdinalIgnoreCase);
            if (becauseIndex >= 0)
                return trimmed[(becauseIndex + 9)..].Trim();

            return string.Empty;
        }

        private static string ExtractSuggestedFix(string text)
        {
            string trimmed = StripNumbering(text);
            int fixIndex = trimmed.IndexOf("fix", StringComparison.OrdinalIgnoreCase);
            if (fixIndex >= 0)
                return trimmed[fixIndex..].Trim([' ', ':', '-', '.']);

            return string.Empty;
        }
    }
}
