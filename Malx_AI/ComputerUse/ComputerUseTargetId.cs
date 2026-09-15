using System;
using System.Linq;

namespace Malx_AI.ComputerUse
{
    /// <summary>
    /// The format of accessible-target ids, owned in one place so issuing and recognising them
    /// cannot drift apart.
    /// </summary>
    /// <remarks>
    /// Telling an issued id from an invented one matters, because the two mean opposite things. An
    /// id in this format that is absent from the current capture is genuinely STALE: the control
    /// was there and has gone, so acting on it would hit the wrong thing. An id in any other
    /// shape — a descriptive "ui_brush_tool", say — was never issued at all; the model named the
    /// control it had in mind. That says nothing about the screen, and rejecting the action for it
    /// discards a perfectly good click along with the coordinates supplied beside it.
    /// </remarks>
    public static class ComputerUseTargetId
    {
        private const string Prefix = "ui";

        /// <summary>The id for the target at <paramref name="zeroBasedIndex"/>.</summary>
        public static string Create(int zeroBasedIndex) => Prefix + (zeroBasedIndex + 1);

        public static bool IsIssued(string? targetId)
        {
            string id = (targetId ?? string.Empty).Trim();
            if (id.Length <= Prefix.Length || !id.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
                return false;

            return id[Prefix.Length..].All(char.IsAsciiDigit);
        }
    }
}
