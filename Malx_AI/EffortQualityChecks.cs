using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace Malx_AI
{
    /// <summary>
    /// What Extra High and Ultra do for a Workplace answer, in place of the Critic.
    /// </summary>
    /// <remarks>
    /// The research behind this (2026-11-24): a model reviewing and rewriting its own draft without
    /// outside evidence gives no gain or a loss, worst on code and below ~10B (Huang et al. 2023;
    /// a 7B two-call refinement cut HumanEval from 96 to 66). What works is feedback grounded in
    /// evidence (compiling, running, validating the artifact) turned into a concrete retry, and,
    /// with weaker evidence, a review in a fresh context that sees only the request and the
    /// result. So:
    /// <list type="bullet">
    /// <item>Extra High: Axiom's own checks (static validation, sandbox and acceptance runs,
    /// fulfillment of the request, artifact validation) decide whether the Builder repairs.</item>
    /// <item>Ultra: the same, plus one fresh-context review on models of 4B and up.</item>
    /// </list>
    /// No Critic card is shown; the user sees the checks and the repaired result.
    /// </remarks>
    internal static class EffortQualityChecks
    {
        public static bool UsesEvidenceChecks(EffortLevel effort) => effort >= EffortLevel.ExtraHigh;

        /// <summary>Small models review their own kind poorly, so the fresh review starts at 4B.</summary>
        public static bool UsesFreshReview(EffortLevel effort, bool cloudOrHybrid, bool localModelAtLeastFourB)
            => effort == EffortLevel.Ultra && (cloudOrHybrid || localModelAtLeastFourB);

        public const int MaxFindings = 8;

        public const string AnswerReviewSystemPrompt =
            "You review an answer for a user, in a fresh context. You get the user's request and the answer. "
            + "List only concrete defects that make the answer wrong or incomplete for the request: factual or logic errors, bugs in code, "
            + "missing parts the user explicitly asked for, broken output format. Do not list style preferences or optional extras. "
            + "Reply with one defect per line, each starting with \"- \". If there are none, reply with exactly: NO ISSUES";

        public static string BuildAnswerReviewPayload(string request, string answer)
        {
            const int Budget = 24_000;
            var builder = new StringBuilder();
            builder.AppendLine("USER REQUEST:");
            builder.AppendLine(request.Length > 4000 ? request[..4000] + "…" : request);
            builder.AppendLine();
            builder.AppendLine("ANSWER:");
            builder.AppendLine(answer.Length > Budget ? answer[..Budget] + "\n… (truncated)" : answer);
            builder.AppendLine();
            builder.AppendLine("List the concrete defects, or reply NO ISSUES.");
            return builder.ToString();
        }

        /// <summary>
        /// Turns check findings into the structured review the existing repair flow reads
        /// (<c>CriticContractParser</c>), so evidence, not opinion, decides whether to repair.
        /// </summary>
        public static string ToReviewContract(IEnumerable<string> findings)
        {
            List<string> distinct = findings
                .Where(f => !string.IsNullOrWhiteSpace(f))
                .Select(f => f.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(MaxFindings)
                .ToList();
            var contract = new
            {
                status = distinct.Count == 0 ? "ok" : "issues",
                issues = distinct.Select(f => new
                {
                    severity = "high",
                    summary = f.Length > 300 ? f[..300] + "…" : f,
                    evidence = "Found by Axiom's checks of the output",
                    suggestedFix = "Fix this in the output and keep everything else unchanged.",
                }).ToList(),
            };
            return JsonSerializer.Serialize(contract);
        }

        /// <summary>One line for the chat about what the checks found.</summary>
        public static string DescribeForChat(int findings, bool reviewed)
        {
            string what = reviewed ? "Checks and a fresh review" : "Checks";
            return findings == 0
                ? $"{what} found no problems."
                : $"{what} found {findings} problem{(findings == 1 ? "" : "s")}; fixing {(findings == 1 ? "it" : "them")}.";
        }
    }
}
