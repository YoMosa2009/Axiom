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
        private static readonly Regex StructuredFieldRegex = new(@"^(?<field>Reference|Issue|Problem|Fix|SuggestedFix|Suggested Fix|Exact Builder Fix|Exact_Builder_Fix|Severity|Evidence|Location)\s*:\s*(?<value>.+)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex SeverityRegex = new(@"\b(low|medium|high|critical)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public static string ContractInstruction =>
            "\n[STRUCTURED OUTPUT CONTRACT] Output valid JSON only with this schema: " +
            "{\"status\":\"ok|issues\",\"issues\":[{\"severity\":\"low|medium|high|critical\",\"summary\":\"...\",\"evidence\":\"...\",\"suggestedFix\":\"...\"}]} " +
            "If there are no issues, output exactly: {\"status\":\"ok\",\"issues\":[]}.";

        public static CriticReport Parse(string criticOutput)
        {
            if (TryParseArtifactHandoff(criticOutput, out var handoffReport))
            {
                NormalizeReportState(handoffReport, criticOutput);
                return handoffReport;
            }

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

        public static bool IsStructuredArtifactPass(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return false;

            return text.Contains("CRITIC_HANDOFF", StringComparison.OrdinalIgnoreCase)
                && text.Contains("END_CRITIC_HANDOFF", StringComparison.OrdinalIgnoreCase)
                && Regex.IsMatch(text, @"(?im)^\s*overall\s*:\s*pass\s*$")
                && !Regex.IsMatch(text, @"(?im)^\s*status\s*:\s*fail\s*$")
                && !Regex.IsMatch(text, @"(?im)^\s*overall\s*:\s*fail\s*$");
        }

        private static bool TryParseArtifactHandoff(string text, out CriticReport report)
        {
            report = new CriticReport();
            if (string.IsNullOrWhiteSpace(text)
                || !text.Contains("CRITIC_HANDOFF", StringComparison.OrdinalIgnoreCase)
                || !text.Contains("END_CRITIC_HANDOFF", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (IsStructuredArtifactPass(text))
            {
                report.Status = "ok";
                report.Issues = new List<CriticIssue>();
                return true;
            }

            var issues = new List<CriticIssue>();
            var blocks = Regex.Split(text, @"(?im)^\s*-\s+severity\s*:")
                .Skip(1)
                .Select(block => "severity:" + block.Trim())
                .ToList();

            foreach (string block in blocks)
            {
                string severity = ExtractField(block, "severity");
                string evidence = ExtractField(block, "evidence");
                string fix = ExtractField(block, "exact_builder_fix");
                if (string.IsNullOrWhiteSpace(fix))
                    fix = ExtractField(block, "exact builder fix");
                string summary = ExtractField(block, "issue");
                if (string.IsNullOrWhiteSpace(summary))
                    summary = string.IsNullOrWhiteSpace(evidence) ? "Artifact requirement failed." : evidence;

                if (!string.IsNullOrWhiteSpace(summary) || !string.IsNullOrWhiteSpace(evidence) || !string.IsNullOrWhiteSpace(fix))
                {
                    issues.Add(new CriticIssue
                    {
                        Severity = string.IsNullOrWhiteSpace(severity) ? "medium" : GuessSeverity(severity),
                        Summary = string.IsNullOrWhiteSpace(summary) ? "Artifact requirement failed." : summary,
                        Evidence = evidence,
                        SuggestedFix = fix
                    });
                }
            }

            bool overallFail = Regex.IsMatch(text, @"(?im)^\s*overall\s*:\s*fail\s*$")
                || Regex.IsMatch(text, @"(?im)^\s*status\s*:\s*fail\s*$");
            if (overallFail && issues.Count == 0)
            {
                issues.Add(new CriticIssue
                {
                    Severity = "medium",
                    Summary = "Critic handoff marked the artifact as failed without a parseable issue.",
                    Evidence = text.Length > 240 ? text[..240] + "..." : text,
                    SuggestedFix = "Review failed requirement checks and revise the artifact."
                });
            }

            report.Status = issues.Count > 0 || overallFail ? "issues" : "ok";
            report.Issues = issues;
            return true;
        }

        private static string ExtractField(string block, string field)
        {
            Match match = Regex.Match(block ?? string.Empty, @"(?im)^\s*" + Regex.Escape(field).Replace("_", "[_ ]") + @"\s*:\s*(?<value>.+?)\s*$");
            return match.Success ? match.Groups["value"].Value.Trim() : string.Empty;
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
                    case "exact builder fix":
                    case "exact_builder_fix":
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

            if ((IsExplicitCleanPass(rawOutput) && !ContainsNumberedFindingPattern(rawOutput)) || IsStructuredArtifactPass(rawOutput))
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
