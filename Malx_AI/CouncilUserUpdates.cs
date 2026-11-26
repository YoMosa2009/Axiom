using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Malx_AI
{
    /// <summary>
    /// Messages the user sends while a Workplace council or Single Model run (not Agent Access)
    /// is working.
    /// </summary>
    /// <remarks>
    /// Owner request (2026-11-26): sending a message mid-run should work like telling a person
    /// something while they work. Before this the council answered "Axiom is still working" and
    /// dropped the message. Now it is queued and folded into the next model call of the run
    /// (each later stage sees every update so far, with the original request kept); a message
    /// that arrives after the last stage started runs as a follow-up as soon as the run ends.
    /// The same shape as Codex's steer queue and Claude Code's queued messages, at the council's
    /// step boundaries (one model call each).
    /// </remarks>
    public sealed class CouncilUserUpdates
    {
        private readonly object _gate = new();
        private readonly List<string> _delivered = new();
        private readonly List<string> _waiting = new();

        public void Post(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
                return;
            lock (_gate)
                _waiting.Add(message.Trim());
        }

        /// <summary>Messages no model call has seen yet.</summary>
        public IReadOnlyList<string> Waiting
        {
            get { lock (_gate) return _waiting.ToList(); }
        }

        public bool HasWaiting
        {
            get { lock (_gate) return _waiting.Count > 0; }
        }

        /// <summary>Starts a new run: anything left over belongs to the follow-up, not this run.</summary>
        public void Reset()
        {
            lock (_gate)
            {
                _delivered.Clear();
                _waiting.Clear();
            }
        }

        /// <summary>Takes the messages still waiting (for a follow-up run), oldest first.</summary>
        public IReadOnlyList<string> TakeWaiting()
        {
            lock (_gate)
            {
                var taken = _waiting.ToList();
                _waiting.Clear();
                return taken;
            }
        }

        /// <summary>
        /// The prompt for a model call, with every update of this run appended. Waiting messages
        /// count as delivered from here on. Returns the payload unchanged when there are none.
        /// </summary>
        public string ApplyTo(string userPayload, string? originalRequest)
        {
            List<string> all;
            lock (_gate)
            {
                _delivered.AddRange(_waiting);
                _waiting.Clear();
                all = _delivered.ToList();
            }
            if (all.Count == 0)
                return userPayload;

            var builder = new StringBuilder((userPayload ?? string.Empty).TrimEnd());
            builder.Append("\n\n[USER UPDATE DURING THIS RUN]\n");
            builder.Append("The user sent ").Append(all.Count == 1 ? "this message" : "these messages, oldest first,")
                .Append(" while you were working:\n");
            foreach (string message in all)
                builder.Append("- \"").Append(message).Append("\"\n");
            builder.Append("It takes priority over the original request where they conflict. If it is a question, answer it briefly in your reply as well. ");
            builder.Append("Do not drop the original request unless the user says to stop it.");
            if (!string.IsNullOrWhiteSpace(originalRequest))
                builder.Append("\nThe original request is still: \"").Append(Clip(originalRequest.Trim(), 600)).Append('"');
            builder.Append("\n[END USER UPDATE]");
            return builder.ToString();
        }

        /// <summary>The follow-up message for updates that arrived after the run's last model call.</summary>
        public static string FollowUpText(IReadOnlyList<string> messages)
            => messages.Count == 1 ? messages[0] : string.Join("\n\n", messages);

        private static string Clip(string text, int max) => text.Length <= max ? text : text[..max] + "…";
    }
}
