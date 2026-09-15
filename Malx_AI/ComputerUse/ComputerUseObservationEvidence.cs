using System;

namespace Malx_AI.ComputerUse
{
    internal static class ComputerUseObservationEvidence
    {
        public static bool HasVisualChange(byte[]? before, byte[]? after)
        {
            if (before == null || after == null || before.Length != after.Length)
                return before != null && after != null;

            return !before.AsSpan().SequenceEqual(after);
        }
    }
}
