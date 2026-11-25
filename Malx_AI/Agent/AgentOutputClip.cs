using System;

namespace Malx_AI.Agent
{
    /// <summary>
    /// Shortens long tool output by keeping its beginning and its end. Build and test logs put
    /// the error and the summary at the bottom; the old head-only cut dropped exactly that part.
    /// Codex and OpenCode keep head and tail the same way. The full text stays in the
    /// conversation evidence archive, which recall_context can page through.
    /// </summary>
    public static class AgentOutputClip
    {
        public static string HeadTail(string text, int maxChars, string omittedNote = "Use recall_context to read the full output.")
        {
            if (string.IsNullOrEmpty(text) || maxChars <= 0 || text.Length <= maxChars)
                return text ?? string.Empty;

            int budget = Math.Max(2, maxChars - 160);
            int headLength = budget * 2 / 5;
            int tailLength = budget - headLength;

            // Cut on line boundaries when one is near, so no half lines reach the model.
            int headEnd = text.LastIndexOf('\n', Math.Max(0, headLength - 1));
            if (headEnd < headLength / 2) headEnd = headLength;
            int tailStart = text.IndexOf('\n', text.Length - tailLength);
            if (tailStart < 0 || tailStart > text.Length - tailLength / 2) tailStart = text.Length - tailLength;
            else tailStart++;

            int omitted = tailStart - headEnd;
            return text[..headEnd].TrimEnd('\r', '\n')
                + $"\n… [{omitted:N0} characters omitted from the middle. {omittedNote}] …\n"
                + text[tailStart..];
        }
    }
}
