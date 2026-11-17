using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Malx_AI.Agent
{
    public enum CommandReviewMode
    {
        /// <summary>Automatically approves safe project commands and intercepts risky/destructive ones for user review.</summary>
        AutoReview,

        /// <summary>Asks the user before running every command or file edit.</summary>
        ManualReview,

        /// <summary>Runs commands automatically (still enforces hard safety blocks and project scope confinement).</summary>
        AutoApprove
    }

    /// <summary>
    /// Fast deterministic safety & scope reviewer for commands and tool calls in Workplace.
    /// Enforces strict folder confinement and flags potentially harmful commands in Auto-Review mode.
    /// </summary>
    public static class CommandSafetyReviewer
    {
        private sealed record RiskRule(Regex Pattern, string Reason);

        private static readonly RiskRule[] RiskyCommandRules =
        [
            new(new Regex(@"(?<![\w-])(Remove-Item|del|erase|rd|rmdir|rm)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
                "deletes files or directories"),
            new(new Regex(@"(?<![\w-])(Move-Item|Rename-Item|Clear-Content|Set-ExecutionPolicy|New-Service|Stop-Service|Stop-Process|taskkill)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
                "modifies system processes, services, or execution policy"),
            new(new Regex(@"(?<![\w-])(Invoke-WebRequest|Invoke-RestMethod|iwr|irm|curl|wget)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
                "makes network requests or downloads external content"),
            new(new Regex(@"(?<![\w-])(reg\s+(add|delete|import)|regedit|schtasks|netsh|icacls|takeown)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
                "alters Windows registry, permissions, or scheduled tasks"),
            new(new Regex(@"\bgit\s+(push|reset\s+--hard|clean\s+-[a-z]*f|rebase|branch\s+-D)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
                "runs a potentially destructive or remote-mutating git operation"),
            new(new Regex(@"\b(pip|npm|pnpm|yarn|winget|choco|scoop)\s+(install|uninstall|upgrade)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
                "installs or uninstalls software packages"),
            new(new Regex(@"(?<![\w-])(Invoke-Expression|iex)\b|\s-(?:EncodedCommand|enc)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
                "runs dynamically evaluated or encoded shell code"),
            new(new Regex(@"(?<![\w-])Start-Process\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
                "launches an external process")
        ];

        private static readonly Regex WindowsAbsolutePathRegex = new(
            @"""(?<path>(?<![A-Za-z0-9_])[A-Za-z]:(?!//)[\\/][^""\r\n]+)""|'(?<path>(?<![A-Za-z0-9_])[A-Za-z]:(?!//)[\\/][^'\r\n]+)'|(?<path>(?<![A-Za-z0-9_])[A-Za-z]:(?!//)[\\/][^\s""'`;|><\r\n]*)",
            RegexOptions.Compiled);

        private static readonly Regex RelativeEscapeRegex = new(
            @"(?:^|[\s""'=])(?<rel>(?:\.\.[\\/])+[^\s""'`;|><\r\n]*)",
            RegexOptions.Compiled);

        private static readonly string[] AllowedSystemExecutablePrefixes =
        [
            @"c:\windows\system32\",
            @"c:\windows\syswow64\",
            @"c:\program files\",
            @"c:\program files (x86)\"
        ];

        public static AgentPermissionDecision Evaluate(
            AgentToolCall call,
            AgentScope scope,
            CommandReviewMode mode,
            IReadOnlyCollection<string>? sessionAllowList = null)
        {
            ArgumentNullException.ThrowIfNull(call);

            // First apply baseline hard blocks & manual/auto checks.
            AgentApprovalMode baselineMode = mode == CommandReviewMode.ManualReview
                ? AgentApprovalMode.Manual
                : AgentApprovalMode.Auto;
            AgentPermissionDecision baseline = AgentPermissionPolicy.Evaluate(call, baselineMode, sessionAllowList);
            if (baseline.Permission == AgentPermission.Deny)
                return baseline;

            if (string.Equals(call.Tool, AgentToolNames.RunCommand, StringComparison.OrdinalIgnoreCase)
                || string.Equals(call.Tool, AgentToolNames.StartBackgroundTask, StringComparison.OrdinalIgnoreCase))
            {
                string command = call.Arg("command");
                if (!scope.EntireComputer && !string.IsNullOrWhiteSpace(scope.RootPath))
                {
                    if (ReferencesPathOutsideScope(command, scope.RootPath, out string? offendingPath))
                    {
                        return new AgentPermissionDecision(
                            AgentPermission.Deny,
                            $"Blocked by strict project scope: command references '{offendingPath}', which is outside '{scope.RootPath}'.");
                    }
                }

                if (mode == CommandReviewMode.ManualReview)
                    return new AgentPermissionDecision(AgentPermission.Ask, "Review Every Command: waiting for your approval.");

                if (mode == CommandReviewMode.AutoApprove)
                    return new AgentPermissionDecision(AgentPermission.Allow, "Auto-Run enabled.");

                // AutoReview: check allow-list first, then inspect for risky operations
                if (AgentPermissionPolicy.MatchesAllowList(command, sessionAllowList))
                    return new AgentPermissionDecision(AgentPermission.Allow, "Allowed for this session.");

                RiskRule? risk = RiskyCommandRules.FirstOrDefault(r => r.Pattern.IsMatch(command));
                if (risk != null)
                {
                    return new AgentPermissionDecision(
                        AgentPermission.Ask,
                        $"Auto-Review intercepted this command because it {risk.Reason}.");
                }

                return new AgentPermissionDecision(AgentPermission.Allow, "Auto-Review: safe project-local command.");
            }

            if (string.Equals(call.Tool, AgentToolNames.BrowserControl, StringComparison.OrdinalIgnoreCase))
            {
                string action = call.Arg("action", "open").Trim().ToLowerInvariant();
                string destination = call.Arg("destination");
                if (!scope.EntireComputer
                    && !string.IsNullOrWhiteSpace(destination)
                    && action is "download" or "install")
                {
                    string resolvedDest = Path.IsPathRooted(destination)
                        ? Path.GetFullPath(destination)
                        : Path.GetFullPath(Path.Combine(scope.WorkingDirectory, destination));
                    if (!scope.Allows(resolvedDest))
                    {
                        return new AgentPermissionDecision(
                            AgentPermission.Deny,
                            $"Blocked by strict project scope: destination '{resolvedDest}' is outside '{scope.RootPath}'.");
                    }
                }

                if (action == "install")
                {
                    if (mode == CommandReviewMode.AutoApprove)
                        return new AgentPermissionDecision(AgentPermission.Allow, "Auto-Run enabled for browser install.");
                    return new AgentPermissionDecision(AgentPermission.Ask, "Review required before running an installer.");
                }

                if (mode == CommandReviewMode.ManualReview && action is "download" or "open" or "navigate")
                    return new AgentPermissionDecision(AgentPermission.Ask, $"Review Every Command: waiting for your approval for browser {action}.");

                return new AgentPermissionDecision(AgentPermission.Allow, "Browser lookup/navigation allowed.");
            }

            return baseline;
        }

        /// <summary>
        /// Checks whether a shell command references a filesystem path outside <paramref name="scopeRoot"/>.
        /// Read-only invocation of standard system/Program Files binaries is allowed unless paired with write/delete commands.
        /// </summary>
        public static bool ReferencesPathOutsideScope(string command, string scopeRoot, out string? offendingPath)
        {
            offendingPath = null;
            if (string.IsNullOrWhiteSpace(command) || string.IsNullOrWhiteSpace(scopeRoot))
                return false;

            string normalizedRoot;
            try
            {
                normalizedRoot = Path.GetFullPath(scopeRoot).TrimEnd(Path.DirectorySeparatorChar);
            }
            catch
            {
                return false;
            }

            bool isDriveRootScope = normalizedRoot.Length == 2 && normalizedRoot[1] == ':';
            string rootPrefix = isDriveRootScope ? normalizedRoot + "\\" : normalizedRoot + Path.DirectorySeparatorChar;

            bool IsInsideRoot(string candidateFullPath)
            {
                string clean = candidateFullPath.TrimEnd(Path.DirectorySeparatorChar);
                return clean.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase)
                    || clean.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase);
            }

            foreach (Match match in WindowsAbsolutePathRegex.Matches(command))
            {
                string rawPath = match.Groups["path"].Value.TrimEnd('.', ',', ';', ')', ']', '}');
                if (rawPath.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                    || rawPath.Contains("://", StringComparison.Ordinal))
                    continue;

                string full;
                try
                {
                    full = Path.GetFullPath(rawPath);
                }
                catch
                {
                    offendingPath = rawPath;
                    return true;
                }

                if (IsInsideRoot(full))
                    continue;

                // Allow invoking standard installed tool binaries (e.g. C:\Program Files\nodejs\node.exe)
                // when not passing a destructive/write operation targeting those directories.
                string lowered = full.ToLowerInvariant();
                bool isSystemToolPath = AllowedSystemExecutablePrefixes.Any(p => lowered.StartsWith(p, StringComparison.Ordinal));
                bool commandLooksLikeDestructive = Regex.IsMatch(command, @"\b(Remove-Item|del|rm|rd|rmdir|Write|Set-Content|Add-Content|Out-File|Copy-Item|Move-Item)\b", RegexOptions.IgnoreCase);
                if (isSystemToolPath && !commandLooksLikeDestructive)
                    continue;

                offendingPath = rawPath;
                return true;
            }

            foreach (Match match in RelativeEscapeRegex.Matches(command))
            {
                string rel = match.Groups["rel"].Value;
                try
                {
                    string resolved = Path.GetFullPath(Path.Combine(normalizedRoot, rel));
                    if (!IsInsideRoot(resolved))
                    {
                        offendingPath = rel;
                        return true;
                    }
                }
                catch
                {
                    offendingPath = rel;
                    return true;
                }
            }

            return false;
        }
    }
}
