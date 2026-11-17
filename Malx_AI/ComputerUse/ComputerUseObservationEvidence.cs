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

        /// <summary>How many pixels differ between two captures of the same screen area.</summary>
        public static int CountChangedPixels(int[]? before, int[]? after)
        {
            if (before == null || after == null || before.Length != after.Length)
                return -1;

            int changed = 0;
            for (int i = 0; i < before.Length; i++)
            {
                if (before[i] != after[i])
                    changed++;
            }

            return changed;
        }

        /// <summary>
        /// Whether a drag left a mark along its path, measured rather than judged.
        /// </summary>
        /// <remarks>
        /// A one-pixel pencil line changes about as many pixels as it is long, and anything wider
        /// changes more. A quarter of the path length is far above the noise a blinking caret or
        /// a hover outline produces, and far below what any real stroke, shape, selection or
        /// moved window leaves. -1 means the area could not be captured, which proves nothing
        /// either way and is treated as visible so a capture fault never fails a real stroke.
        /// </remarks>
        public static bool IsDragEffectVisible(int changedPixels, double pathLengthPixels)
        {
            if (changedPixels < 0)
                return true;
            return changedPixels >= Math.Max(24, (int)(pathLengthPixels * 0.25));
        }
    }
}
