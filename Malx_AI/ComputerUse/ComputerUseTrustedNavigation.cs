using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Malx_AI.ComputerUse
{
    /// <summary>
    /// Parses controller-owned navigation evidence.  It deliberately does not infer an
    /// account/owner from a natural-language request: an incorrect destination is worse
    /// than a blocked destination for resources that belong to a particular user.
    /// </summary>
    internal static class ComputerUseTrustedNavigation
    {
        public const string VerifiedUrlPrefix = "[VERIFIED NAVIGATION URL]";

        private static readonly Regex RepoListEntry = new(
            @"full_name=(?<fullName>[^\s]+)\s+private=(?<private>[^\s]+).*?\surl=(?<url>https?://[^\s]+)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

        public static string CreateContext(string url)
            => VerifiedUrlPrefix + " " + url;

        public static bool TryGetVerifiedUrl(string? context, out string url)
        {
            url = string.Empty;
            string value = context ?? string.Empty;
            int prefix = value.IndexOf(VerifiedUrlPrefix, StringComparison.Ordinal);
            if (prefix < 0)
                return false;

            string candidate = value[(prefix + VerifiedUrlPrefix.Length)..]
                .Split([' ', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault() ?? string.Empty;
            return ComputerUseBrowserVerification.TryGetAbsoluteHttpUrl(candidate, out url);
        }

        public static bool TryGetGitHubRepositoryUrl(string? repositoryUrl, string repositoryName, out string url)
        {
            url = string.Empty;
            if (!Uri.TryCreate(repositoryUrl, UriKind.Absolute, out Uri? parsed)
                || !string.Equals(parsed.Host, "github.com", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            string[] segments = parsed.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            string repository = segments.Length >= 2 && segments[1].EndsWith(".git", StringComparison.OrdinalIgnoreCase)
                ? segments[1][..^4]
                : segments.Length >= 2 ? segments[1] : string.Empty;
            if (segments.Length < 2
                || !string.Equals(repository, repositoryName, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            url = $"https://github.com/{segments[0]}/{repository}";
            return true;
        }

        public static bool TryFindUniquePublicRepositoryUrl(string? repositoryList, string repositoryName, out string url)
        {
            url = string.Empty;
            var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match entry in RepoListEntry.Matches(repositoryList ?? string.Empty))
            {
                if (!string.Equals(entry.Groups["private"].Value, "false", StringComparison.OrdinalIgnoreCase)
                    || !TryGetGitHubRepositoryUrl(entry.Groups["url"].Value, repositoryName, out string candidate))
                {
                    continue;
                }

                string[] fullName = entry.Groups["fullName"].Value.Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (fullName.Length == 2 && string.Equals(fullName[1], repositoryName, StringComparison.OrdinalIgnoreCase))
                    candidates.Add(candidate);
            }

            if (candidates.Count != 1)
                return false;

            url = candidates.Single();
            return true;
        }

        public static bool IsVerifiedDestinationAction(string? navigationContext, bool verifiedDestinationAlreadyReached, string? actionText, out string observation)
        {
            observation = string.Empty;
            if (verifiedDestinationAlreadyReached
                || !TryGetVerifiedUrl(navigationContext, out string verifiedUrl)
                || !ComputerUseBrowserVerification.TryGetAbsoluteHttpUrl(actionText, out string requestedUrl))
            {
                return false;
            }

            if (ComputerUseBrowserVerification.AreEquivalentUrls(verifiedUrl, requestedUrl))
                return false;

            observation = "[TRUSTED DESTINATION BLOCK] The requested URL differs from the controller-verified destination. Do not replace an owner, path, or query with an inferred value. Use exactly " + verifiedUrl + ".";
            return true;
        }

        public static bool IsUnverifiedOwnedRepositoryNavigation(string? goal, string? navigationContext, string? actionText)
        {
            if (!ComputerUseGoalAnalysis.TryGetRequestedOwnedGitHubRepository(goal, out _)
                || TryGetVerifiedUrl(navigationContext, out _)
                || !ComputerUseBrowserVerification.TryGetAbsoluteHttpUrl(actionText, out string requestedUrl)
                || !Uri.TryCreate(requestedUrl, UriKind.Absolute, out Uri? requested))
            {
                return false;
            }

            return string.Equals(requested.Host, "github.com", StringComparison.OrdinalIgnoreCase);
        }
    }
}
