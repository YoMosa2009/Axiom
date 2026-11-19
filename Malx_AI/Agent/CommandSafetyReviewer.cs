using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Malx_AI.Agent
{
    public enum CommandReviewMode
    {
        /// <summary>Automatically approves safe project commands and declines unsafe/destructive ones on the user's behalf.</summary>
        AutoReview,

        /// <summary>Asks the user before running every command or file edit.</summary>
        ManualReview,

        /// <summary>Runs commands automatically (still enforces hard safety blocks and project scope confinement).</summary>
        AutoApprove
    }

    /// <summary>
    /// Fast deterministic safety & scope reviewer for commands and tool calls in Workplace.
    /// Enforces strict folder confinement and decides safe vs unsafe commands autonomously in Auto-Review mode.
    /// </summary>
    public static class CommandSafetyReviewer
    {
        private sealed record RiskRule(Regex Pattern, string Reason);

        private static readonly RiskRule[] AutoDenyCommandRules =
        [
            new(new Regex(@"(?<![\w-])(Set-ExecutionPolicy|New-Service|Stop-Service|Stop-Process|taskkill)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
                "modifies system processes, services, or execution policy"),
            new(new Regex(@"(?<![\w-])(reg\s+(add|delete|import)|regedit|schtasks|netsh|icacls|takeown)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
                "alters Windows registry, permissions, or scheduled tasks"),
            new(new Regex(@"\bgit\s+(push|reset\s+--hard|clean\s+-[a-z]*f|rebase|branch\s+-D)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
                "runs a potentially destructive or remote-mutating git operation"),
            new(new Regex(@"(?<![\w-])(winget|choco|scoop|msiexec)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
                "installs or removes system-wide software"),
            new(new Regex(@"\b(npm|pnpm|yarn)\s+(?:install|i|add|uninstall|remove)\b[^\r\n]*\s(?:-g|--global)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
                "installs or removes global software packages"),
            new(new Regex(@"\b(pip|npm|pnpm|yarn)\s+uninstall\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
                "uninstalls packages"),
            new(new Regex(@"(?<![\w-])(Invoke-Expression|iex)\b|\s-(?:EncodedCommand|enc)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
                "runs dynamically evaluated or encoded shell code")
        ];

        private static readonly Regex DeleteCommandRegex = new(
            @"(?<![\w-])(Remove-Item|del|erase|rd|rmdir|rm)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex UnsafeDeleteTargetRegex = new(
            @"(?:^|[\s""'])(?:\.git(?=[\\/]|$|\s|""|')|\.{1,2}[\\/]?(?=\s|""|'|$)|\*|\.[\\/]\*)(?:$|[\s""'])",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex NetworkCommandRegex = new(
            @"(?<![\w-])(Invoke-WebRequest|Invoke-RestMethod|iwr|irm|curl|wget)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex HttpUrlRegex = new(
            @"https?://(?<host>[^\s/""'`;|><:]+)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex PipeToShellRegex = new(
            @"\|\s*(?:iex|Invoke-Expression|powershell|pwsh|cmd|sh|bash)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex StartProcessRegex = new(
            @"(?<![\w-])Start-Process\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex UnsafeStartProcessArgsRegex = new(
            @"-Verb\s+RunAs\b|Start-Process\s+[""']?(?:powershell|pwsh|cmd|wscript|cscript|msiexec)(?:\.exe)?\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex LocalInstallRegex = new(
            @"\b(pip|pip3|npm|pnpm|yarn)\s+(install|add|i)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex MoveRenameClearRegex = new(
            @"(?<![\w-])(Move-Item|Rename-Item|Clear-Content)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

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

                // AutoReview: decide autonomously on the user's behalf without interrupting them.
                if (AgentPermissionPolicy.MatchesAllowList(command, sessionAllowList))
                    return new AgentPermissionDecision(AgentPermission.Allow, "Allowed for this session.");

                if (EvaluateAutoReviewDangerReason(command, scope) is string denyReason)
                {
                    return new AgentPermissionDecision(
                        AgentPermission.Deny,
                        $"Auto-Review declined this command because it {denyReason}.");
                }

                return new AgentPermissionDecision(AgentPermission.Allow, "Auto-Review: approved safe project command.");
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
                    if (mode == CommandReviewMode.AutoReview)
                        return new AgentPermissionDecision(AgentPermission.Deny, "Auto-Review declined running an external installer automatically.");
                    return new AgentPermissionDecision(AgentPermission.Ask, "Review required before running an installer.");
                }

                if (mode == CommandReviewMode.ManualReview && action is "download" or "open" or "navigate")
                    return new AgentPermissionDecision(AgentPermission.Ask, $"Review Every Command: waiting for your approval for browser {action}.");

                return new AgentPermissionDecision(AgentPermission.Allow, "Browser lookup/navigation allowed.");
            }

            return baseline;
        }

        private static string? EvaluateAutoReviewDangerReason(string command, AgentScope scope)
        {
            RiskRule? alwaysDanger = AutoDenyCommandRules.FirstOrDefault(r => r.Pattern.IsMatch(command));
            if (alwaysDanger != null)
                return alwaysDanger.Reason;

            bool isUnscoped = scope.EntireComputer || string.IsNullOrWhiteSpace(scope.RootPath);

            if (DeleteCommandRegex.IsMatch(command))
            {
                if (isUnscoped)
                    return "deletes files without a confined project folder";
                if (UnsafeDeleteTargetRegex.IsMatch(command))
                    return "targets the project root, wildcards, or repository metadata for deletion";
            }

            if (MoveRenameClearRegex.IsMatch(command) && isUnscoped)
                return "modifies or clears files without a confined project folder";

            if (LocalInstallRegex.IsMatch(command) && isUnscoped)
                return "installs packages without a confined project folder";

            if (NetworkCommandRegex.IsMatch(command))
            {
                if (PipeToShellRegex.IsMatch(command))
                    return "pipes network content into a shell interpreter";

                MatchCollection urls = HttpUrlRegex.Matches(command);
                if (urls.Count == 0 || urls.Any(m => !IsLoopbackHost(m.Groups["host"].Value)))
                    return "makes external network requests or downloads content";
            }

            if (StartProcessRegex.IsMatch(command) && (isUnscoped || UnsafeStartProcessArgsRegex.IsMatch(command)))
                return "launches a privileged or external host process";

            return null;
        }

        private static bool IsLoopbackHost(string host)
            => string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
                || string.Equals(host, "127.0.0.1", StringComparison.OrdinalIgnoreCase)
                || string.Equals(host, "[::1]", StringComparison.OrdinalIgnoreCase)
                || string.Equals(host, "::1", StringComparison.OrdinalIgnoreCase);

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
