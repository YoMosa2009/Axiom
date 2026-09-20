using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Malx_AI.Agent
{
    /// <summary>How much the agent may do on its own.</summary>
    public enum AgentApprovalMode
    {
        /// <summary>Every command and every file change waits for the user to approve it.</summary>
        Manual,

        /// <summary>The agent runs without stopping, apart from the hard-blocked operations.</summary>
        Auto
    }

    /// <summary>The three outcomes, matching the allow / ask / deny vocabulary these agents share.</summary>
    public enum AgentPermission
    {
        Allow,
        Ask,
        Deny
    }

    public sealed record AgentPermissionDecision(AgentPermission Permission, string Reason);

    /// <summary>
    /// Decides whether a tool call runs, waits for approval, or is refused outright.
    /// </summary>
    /// <remarks>
    /// Auto mode means "do not interrupt the user", not "no limits". A small set of whole-machine,
    /// irreversible operations stays blocked in both modes: formatting a disk, deleting a drive
    /// root, rewriting the boot configuration. A model that reaches for one of those has
    /// misunderstood the task, and no plausible chat request is served by letting it through. The
    /// list is deliberately short so it never obstructs ordinary work.
    /// </remarks>
    public static class AgentPermissionPolicy
    {
        private sealed record BlockedPattern(Regex Pattern, string Reason);

        private static readonly BlockedPattern[] AlwaysBlocked =
        [
            new(new Regex(@"(?<![\w-])format\s+[a-z]:", RegexOptions.IgnoreCase | RegexOptions.Compiled),
                "formats a drive"),
            new(new Regex(@"(?<![\w-])mkfs(\.\w+)?\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
                "creates a filesystem over an existing one"),
            new(new Regex(@"(?<![\w-])diskpart\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
                "repartitions disks"),
            // rm -rf / and rm -rf /* but not rm -rf ./build or rm -rf /home/me/project
            new(new Regex(@"(?<![\w-])rm\s+(-\w+\s+)*-\w*[rR]\w*f\w*\s+/(\s|\*|$)", RegexOptions.Compiled),
                "deletes the entire filesystem"),
            // del /s /q C:\  and  rd /s C:\  addressed at a bare drive root
            new(new Regex(@"(?<![\w-])(del|rd|rmdir)\s+(/\w+\s+)*[a-z]:\\?(\s|\*|$)", RegexOptions.IgnoreCase | RegexOptions.Compiled),
                "deletes a drive root"),
            new(new Regex(@"(?<![\w-])bcdedit\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
                "rewrites the Windows boot configuration"),
            new(new Regex(@"(?<![\w-])reg\s+delete\s+HK(LM|EY_LOCAL_MACHINE)\s*(\\)?\s*($|\S*\s*/f)", RegexOptions.IgnoreCase | RegexOptions.Compiled),
                "deletes a machine-wide registry hive"),
            new(new Regex(@"(?<![\w-])(shutdown|Stop-Computer|Restart-Computer)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
                "shuts down or restarts the computer"),
            new(new Regex(@":\(\)\s*\{\s*:\|:\s*&\s*\}\s*;\s*:", RegexOptions.Compiled),
                "is a fork bomb"),
            new(new Regex(@"(?<![\w-])cipher\s+/w", RegexOptions.IgnoreCase | RegexOptions.Compiled),
                "wipes free space irreversibly"),
        ];

        /// <summary>Paths the agent may never write to, whatever the mode.</summary>
        private static readonly string[] ProtectedWriteFragments =
        [
            @"\windows\system32",
            @"\windows\syswow64",
            @"\system volume information",
            @"\$recycle.bin",
        ];

        /// <summary>
        /// Evaluates one call. <paramref name="sessionAllowList"/> holds the command prefixes the
        /// user chose to stop being asked about for this session.
        /// </summary>
        public static AgentPermissionDecision Evaluate(
            AgentToolCall call,
            AgentApprovalMode mode,
            IReadOnlyCollection<string>? sessionAllowList = null)
        {
            ArgumentNullException.ThrowIfNull(call);

            if (!AgentToolNames.IsKnown(call.Tool))
                return new AgentPermissionDecision(AgentPermission.Deny, $"'{call.Tool}' is not an Axiom agent tool.");

            if (string.Equals(call.Tool, AgentToolNames.RunCommand, StringComparison.OrdinalIgnoreCase))
            {
                string command = call.Arg("command");
                if (string.IsNullOrWhiteSpace(command))
                    return new AgentPermissionDecision(AgentPermission.Deny, "The command was empty.");

                BlockedPattern? blocked = AlwaysBlocked.FirstOrDefault(entry => entry.Pattern.IsMatch(command));
                if (blocked != null)
                {
                    return new AgentPermissionDecision(
                        AgentPermission.Deny,
                        $"Blocked: this command {blocked.Reason}. Axiom refuses it in both Auto and Manual mode.");
                }

                if (mode == AgentApprovalMode.Auto)
                    return new AgentPermissionDecision(AgentPermission.Allow, "Auto mode.");

                if (MatchesAllowList(command, sessionAllowList))
                    return new AgentPermissionDecision(AgentPermission.Allow, "You allowed this command for this session.");

                return new AgentPermissionDecision(AgentPermission.Ask, "Manual mode: commands need your approval.");
            }

            if (AgentToolNames.IsReadOnly(call.Tool))
            {
                // Reads never change anything, and asking for each one makes Manual mode unusable
                // without making it safer.
                return new AgentPermissionDecision(AgentPermission.Allow, "Read-only.");
            }

            // write_file / edit_file
            string path = call.Arg("path");
            if (string.IsNullOrWhiteSpace(path))
                return new AgentPermissionDecision(AgentPermission.Deny, "No file path was supplied.");

            string lowered = path.Replace('/', '\\').ToLowerInvariant();
            foreach (string fragment in ProtectedWriteFragments)
            {
                if (lowered.Contains(fragment, StringComparison.Ordinal))
                {
                    return new AgentPermissionDecision(
                        AgentPermission.Deny,
                        "Blocked: writing inside protected Windows system folders is refused in both modes.");
                }
            }

            return mode == AgentApprovalMode.Auto
                ? new AgentPermissionDecision(AgentPermission.Allow, "Auto mode.")
                : new AgentPermissionDecision(AgentPermission.Ask, "Manual mode: file changes need your approval.");
        }

        /// <summary>
        /// True when the user has already approved this shape of command. Matching is on the
        /// leading token run so approving "git status" also covers "git status --short", while
        /// approving "git" does not silently cover "git push --force".
        /// </summary>
        public static bool MatchesAllowList(string command, IReadOnlyCollection<string>? allowList)
        {
            if (allowList == null || allowList.Count == 0 || string.IsNullOrWhiteSpace(command))
                return false;

            string normalized = NormalizeCommand(command);
            return allowList
                .Select(NormalizeCommand)
                .Where(entry => entry.Length > 0)
                .Any(entry => normalized == entry || normalized.StartsWith(entry + " ", StringComparison.Ordinal));
        }

        /// <summary>The allow-list key for a command: its first two tokens, lowercased.</summary>
        public static string AllowListKeyFor(string command)
        {
            string[] tokens = NormalizeCommand(command)
                .Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return tokens.Length <= 1 ? string.Join(' ', tokens) : string.Join(' ', tokens.Take(2));
        }

        private static string NormalizeCommand(string command)
        {
            string flat = (command ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim().ToLowerInvariant();
            while (flat.Contains("  ", StringComparison.Ordinal))
                flat = flat.Replace("  ", " ", StringComparison.Ordinal);
            return flat;
        }
    }
}
