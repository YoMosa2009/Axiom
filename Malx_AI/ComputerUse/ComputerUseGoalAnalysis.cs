using System;
using System.Text.RegularExpressions;

namespace Malx_AI.ComputerUse
{
    internal static class ComputerUseGoalAnalysis
    {
        private static readonly Regex OwnedGitHubRepositoryRegex = new(
            @"\b(?:my\s+(?:own\s+)?)?github\s+(?:repository|repo)\s+(?:(?:called|named)\s+(?:['""](?<name>[^'""]+?)['""]|(?<name>[A-Za-z0-9_.-]+))|['""](?<name>[^'""]+?)['""])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

        public static bool TryGetRequestedOwnedGitHubRepository(string? goal, out string repositoryName)
        {
            repositoryName = string.Empty;
            Match match = OwnedGitHubRepositoryRegex.Match(goal ?? string.Empty);
            if (!match.Success)
                return false;

            string candidate = match.Groups["name"].Value.Trim();
            if (candidate.Length == 0 || candidate.Length > 100)
                return false;

            repositoryName = candidate;
            return true;
        }
    }
}
