using System;

namespace Malx_AI
{
    internal static class WorkplaceRenderedHistoryWindow
    {
        public const int PageSize = 80;

        public static int CalculateStartIndex(int totalCount, int visibleLimit)
            => Math.Max(0, Math.Max(0, totalCount) - Math.Max(1, visibleLimit));

        public static int IncreaseLimit(int currentLimit, int totalCount)
            => Math.Min(Math.Max(0, totalCount), Math.Max(PageSize, currentLimit) + PageSize);
    }
}
