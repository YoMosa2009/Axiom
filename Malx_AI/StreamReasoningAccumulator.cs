using System.Text;

namespace Malx_AI
{
    /// <summary>
    /// Collects a streamed model's reasoning from whichever field the provider uses, without
    /// counting the same tokens twice.
    /// </summary>
    /// <remarks>
    /// OpenRouter sends the same chain-of-thought as "reasoning" (a string) and as
    /// "reasoning_details" (structured parts), and some providers put reasoning parts inside
    /// "content" as well. The old per-chunk check only skipped a duplicate when both fields
    /// arrived in the SAME chunk with identical text. When a provider sent them in alternating
    /// chunks, every fragment was appended twice and the run card showed mushed words
    /// ("statsstats", "andand", "Let me sleep 900 and check.900 and check."; owner logs
    /// 2026-11-26, DeepSeek V4.1 Flash). The fix: the first field that carries reasoning in a
    /// stream becomes its only source; the others are ignored for the rest of that stream.
    /// </remarks>
    public sealed class StreamReasoningAccumulator
    {
        public enum Source { None, Plain, Details, Content }

        private readonly StringBuilder _builder = new();

        /// <summary>The field this stream's reasoning is read from, once one has been seen.</summary>
        public Source LockedSource { get; private set; } = Source.None;

        public int Length => _builder.Length;

        /// <summary>
        /// Adds one chunk's reasoning. <paramref name="plain"/> is "reasoning" /
        /// "reasoning_content" / "thinking"; <paramref name="details"/> is "reasoning_details";
        /// <paramref name="fromContent"/> is reasoning parts inside "content".
        /// Returns the text actually appended (empty when it was a duplicate source).
        /// </summary>
        public string Add(string? plain, string? details, string? fromContent)
        {
            if (LockedSource == Source.None)
            {
                // In one chunk, the plain string is preferred: it is what every provider sends.
                if (!string.IsNullOrEmpty(plain))
                    LockedSource = Source.Plain;
                else if (!string.IsNullOrEmpty(details))
                    LockedSource = Source.Details;
                else if (!string.IsNullOrEmpty(fromContent))
                    LockedSource = Source.Content;
                else
                    return string.Empty;
            }

            string piece = LockedSource switch
            {
                Source.Plain => plain ?? string.Empty,
                Source.Details => details ?? string.Empty,
                Source.Content => fromContent ?? string.Empty,
                _ => string.Empty
            };
            _builder.Append(piece);
            return piece;
        }

        public override string ToString() => _builder.ToString();
    }
}
