using System;
using System.Linq;
using System.Text;
using Malx_AI;
using Xunit;

namespace Malx_AI.Tests
{
    public class FeedbackReportTests
    {
        private static FeedbackSubmission Submission(string message, bool diagnostics = true, string? email = null, string log = "") =>
            new(FeedbackKind.Problem, message, email, diagnostics, "1.9.15", "Windows 11 (10.0.26200)", "16 cores, 32 GB RAM, RTX 3060 12 GB", "Local", log);

        [Fact]
        public void SubjectNamesTheVersionKindAndFirstLine()
        {
            var report = FeedbackReportBuilder.Build(Submission("The app froze\nwhen I clicked Stop"));
            Assert.Equal("[Axiom 1.9.15] Problem: The app froze", report.Subject);
        }

        [Fact]
        public void LongFirstLinesAreShortenedInTheSubject()
        {
            string message = new string('a', 80);
            var report = FeedbackReportBuilder.Build(Submission(message));
            Assert.EndsWith("\u2026", report.Subject);
            string expectedPrefix = "[Axiom 1.9.15] Problem: ";
            Assert.Equal(expectedPrefix.Length + 61, report.Subject.Length);
        }

        [Fact]
        public void DiagnosticsAreLeftOutWhenNotWanted()
        {
            var report = FeedbackReportBuilder.Build(Submission("hi", diagnostics: false));
            Assert.DoesNotContain("Technical details", report.Body);
            Assert.DoesNotContain("RTX 3060", report.Body);
        }

        [Fact]
        public void DiagnosticsIncludeOnlyTheLastLogLines()
        {
            string log = string.Join("\n", Enumerable.Range(1, 100).Select(i => "line " + i));
            var report = FeedbackReportBuilder.Build(Submission("hi", log: log));
            Assert.Contains("line 61", report.Body);
            Assert.Contains("line 100", report.Body);
            Assert.DoesNotContain("line 59", report.Body);
        }

        [Fact]
        public void AnEmptyLogIsShownAsNone()
        {
            var report = FeedbackReportBuilder.Build(Submission("hi"));
            Assert.Contains("Recent errors:\n(none)", report.Body);
        }

        [Fact]
        public void RedactRemovesOpenRouterKeys()
        {
            var result = FeedbackReportBuilder.Redact("key sk-or-v1-abcdef0123456789abcdef here");
            Assert.Contains("[redacted key]", result);
            Assert.DoesNotContain("abcdef0123", result);
        }

        [Fact]
        public void RedactRemovesBearerTokens()
        {
            var result = FeedbackReportBuilder.Redact("Authorization: Bearer abc.def-ghi_jkl123");
            Assert.DoesNotContain("abc.def", result);
            Assert.Contains("Bearer [redacted]", result);
        }

        [Fact]
        public void RedactRemovesGitHubTokens()
        {
            var result = FeedbackReportBuilder.Redact("token ghp_" + new string('x', 36));
            Assert.DoesNotContain("xxxxxxxx", result);
        }

        [Fact]
        public void RedactMasksApiKeyAssignments()
        {
            var result = FeedbackReportBuilder.Redact("{\"api_key\": \"abc123secret\"}");
            Assert.DoesNotContain("abc123secret", result);
        }

        [Fact]
        public void RedactHidesTheWindowsUserName()
        {
            var result = FeedbackReportBuilder.Redact(@"C:\Users\Mosa\AppData\Local\Axiom\logs");
            Assert.Equal(@"C:\Users\<user>\AppData\Local\Axiom\logs", result);
        }

        [Fact]
        public void RedactingAUserFolderKeepsTheRestOfTheLine()
        {
            var result = FeedbackReportBuilder.Redact(@"Saved to C:\Users\Mosa then exited");
            Assert.Equal(@"Saved to C:\Users\<user> then exited", result);
        }

        [Fact]
        public void SecretsTypedIntoTheMessageAreRedacted()
        {
            var report = FeedbackReportBuilder.Build(Submission("my key is sk-or-v1-abcdef0123456789abcdef"));
            Assert.DoesNotContain("abcdef0123", report.Body);
        }

        [Fact]
        public void MailtoLinksAreEscapedAndTrimmed()
        {
            var report = new FeedbackReport("Hello world", new string('b', 3000));
            var uri = FeedbackReportBuilder.BuildMailtoUri("a@b.com", report, 1000);
            Assert.StartsWith("mailto:a@b.com?subject=Hello%20world&body=", uri);
            Assert.Contains(Uri.EscapeDataString("[...trimmed"), uri);
            Assert.DoesNotContain(new string('b', 1001), uri);
        }

        [Fact]
        public void ReplyEmailMustLookLikeAnEmail()
        {
            Assert.True(FeedbackReportBuilder.IsPlausibleEmail("me@example.com"));
            Assert.False(FeedbackReportBuilder.IsPlausibleEmail("not an email"));
            Assert.False(FeedbackReportBuilder.IsPlausibleEmail(""));
            Assert.False(FeedbackReportBuilder.IsPlausibleEmail(null));
        }

        [Fact]
        public void AnInvalidReplyEmailIsNotUsed()
        {
            var report1 = FeedbackReportBuilder.Build(Submission("hi", email: "nope"));
            Assert.Contains("Reply to: (not given)", report1.Body);

            var report2 = FeedbackReportBuilder.Build(Submission("hi", email: " me@example.com "));
            Assert.Contains("Reply to: me@example.com", report2.Body);
        }

        [Fact]
        public void GitHubLinkOpensTheBugFormForAProblem()
        {
            var uri = FeedbackReportBuilder.BuildGitHubIssueUri(Submission("The app froze"), 2000);
            Assert.StartsWith("https://github.com/YoMosa2009/Axiom/issues/new?template=bug_report.yml&title=", uri);
            Assert.Contains("&what-happened=The%20app%20froze", uri);
            Assert.Contains("&version=1.9.15", uri);
        }

        [Fact]
        public void GitHubLinkOpensTheIdeaFormForAnIdea()
        {
            var submission = Submission("Add voice input") with { Kind = FeedbackKind.Idea };
            var uri = FeedbackReportBuilder.BuildGitHubIssueUri(submission, 2000);
            Assert.Contains("template=feature_request.yml", uri);
            Assert.Contains("&idea=Add%20voice%20input", uri);
            Assert.DoesNotContain("what-happened", uri);
        }

        [Fact]
        public void GitHubLinkLeavesOutDetailsWhenNotWanted()
        {
            var uri = FeedbackReportBuilder.BuildGitHubIssueUri(Submission("hi", diagnostics: false), 2000);
            Assert.DoesNotContain("&details=", uri);
        }

        [Fact]
        public void GitHubLinkCarriesTechnicalDetails()
        {
            var uri = FeedbackReportBuilder.BuildGitHubIssueUri(Submission("hi", log: "boom"), 2000);
            Assert.Contains("&details=", uri);
            Assert.Contains(Uri.EscapeDataString("RTX 3060"), uri);
            Assert.Contains("boom", uri);
            Assert.DoesNotContain(Uri.EscapeDataString("--- Technical details ---"), uri);
        }

        [Fact]
        public void GitHubLinkScrubsSecrets()
        {
            var uri = FeedbackReportBuilder.BuildGitHubIssueUri(Submission("key sk-or-v1-abcdef0123456789abcdef"), 2000);
            Assert.DoesNotContain("abcdef0123", uri);
        }

        [Fact]
        public void GitHubLinkFieldsAreCapped()
        {
            var uri = FeedbackReportBuilder.BuildGitHubIssueUri(Submission(new string('a', 5000)), 100);
            Assert.DoesNotContain(new string('a', 101), uri);
        }
    }
}
