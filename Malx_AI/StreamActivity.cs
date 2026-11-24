using System;
using System.Threading;

namespace Malx_AI
{
    /// <summary>
    /// Lets a caller see that a streamed reply is still moving (answer text, reasoning or
    /// tool-call arguments), without threading a callback through every transport overload.
    /// </summary>
    /// <remarks>
    /// Agent turns used a fixed 3-minute deadline. A Hybrid Local model at Ultra effort thinks
    /// for minutes and writes whole files at ~30 tok/s, so healthy turns were killed mid-write
    /// (owner logs, 2026-11-23: three runs stopped at exactly 180 s). The agent now stops a turn
    /// only after a long silence, which needs to know when the stream last moved.
    /// </remarks>
    internal static class StreamActivity
    {
        private static readonly AsyncLocal<Action?> Listener = new();

        /// <summary>Calls <paramref name="onActivity"/> for every streamed chunk until disposed.</summary>
        public static IDisposable Use(Action onActivity)
        {
            Action? previous = Listener.Value;
            Listener.Value = onActivity;
            return new Restore(previous);
        }

        /// <summary>Called by the transport whenever a chunk arrives.</summary>
        public static void Ping()
        {
            try { Listener.Value?.Invoke(); }
            catch { /* a listener failure must never break the stream */ }
        }

        private sealed class Restore(Action? previous) : IDisposable
        {
            public void Dispose() => Listener.Value = previous;
        }
    }

    /// <summary>
    /// How long an agent turn may run. A turn is stopped when the model has been silent for
    /// <see cref="Idle"/> (nothing streamed, which on servers that hide thinking or send a tool
    /// call only when complete also covers the wait for the first chunk), or after
    /// <see cref="Total"/> regardless. Both grow with effort: a higher effort asks the model to
    /// think longer, so it must be allowed to.
    /// </summary>
    internal readonly record struct AgentTurnLimits(TimeSpan Idle, TimeSpan Total)
    {
        public static AgentTurnLimits For(EffortLevel effort, bool selfHosted)
        {
            // Self-hosted servers (llama.cpp / Ollama behind a proxy) may not stream reasoning or
            // tool arguments at all, so a long silent stretch is normal there. 8 minutes matches
            // the transport's own first-content deadline for custom-endpoint tool calls.
            TimeSpan idle = selfHosted ? TimeSpan.FromMinutes(8) : TimeSpan.FromMinutes(4);
            TimeSpan total = effort switch
            {
                EffortLevel.Light => TimeSpan.FromMinutes(10),
                EffortLevel.Medium => TimeSpan.FromMinutes(12),
                EffortLevel.High => TimeSpan.FromMinutes(15),
                EffortLevel.ExtraHigh => TimeSpan.FromMinutes(20),
                _ => TimeSpan.FromMinutes(25)
            };
            return new AgentTurnLimits(idle, total);
        }

        /// <summary>Plain wording for the run card and the chat when a turn is stopped.</summary>
        public static string Describe(TimeSpan span)
            => span.TotalMinutes >= 1 ? $"{(int)Math.Round(span.TotalMinutes)} minutes" : $"{(int)span.TotalSeconds} seconds";
    }
}
