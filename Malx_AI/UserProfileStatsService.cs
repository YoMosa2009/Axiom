using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Malx_AI
{
    public enum TokenActivityViewMode
    {
        Daily,
        Weekly,
        Cumulative
    }

    public sealed class UserProfileStatsSnapshot
    {
        public string DisplayName { get; set; } = "Your Name";
        public long LifetimeTokens { get; set; }
        public Dictionary<string, long> DailyTokens { get; set; } = new(StringComparer.Ordinal);
    }

    public sealed record UserProfileSummary(
        string DisplayName,
        long LifetimeTokens,
        string FormattedLifetimeTokens,
        int CurrentStreakDays,
        int LongestStreakDays);

    public sealed record TokenHeatmapCell(
        DateTime Date,
        long Tokens,
        int IntensityLevel,
        string TooltipText);

    public sealed record TokenHeatmapGrid(
        IReadOnlyList<string> MonthLabels,
        IReadOnlyList<TokenHeatmapCell> Cells,
        int Weeks,
        int DaysPerWeek);

    /// <summary>
    /// Persists lifetime token usage across Chat & Workplace, daily active streaks, display name,
    /// and 52-week Daily / Weekly / Cumulative heatmap data for the Settings -> Your Profile view.
    /// </summary>
    public sealed class UserProfileStatsService
    {
        private static readonly Lazy<UserProfileStatsService> LazyShared =
            new(() => new UserProfileStatsService(Path.Combine(AppDataPaths.ChatHistory, "user_profile_stats.json")));

        public static UserProfileStatsService Shared => LazyShared.Value;

        private readonly string _storagePath;
        private readonly object _gate = new();
        private UserProfileStatsSnapshot? _snapshot;

        public event Action? Changed;

        public UserProfileStatsService(string storagePath)
        {
            _storagePath = storagePath ?? throw new ArgumentNullException(nameof(storagePath));
        }

        public void RecordTokens(long tokens, DateTime? localDate = null)
        {
            if (tokens <= 0)
                return;

            DateTime date = (localDate ?? DateTime.Now).Date;
            string key = date.ToString("yyyy-MM-dd");

            lock (_gate)
            {
                UserProfileStatsSnapshot snap = EnsureLoadedUnsafe();
                snap.LifetimeTokens = Math.Max(0, snap.LifetimeTokens + tokens);
                snap.DailyTokens[key] = snap.DailyTokens.TryGetValue(key, out long prev) ? prev + tokens : tokens;
                SaveUnsafe(snap);
            }

            NotifyChanged();
        }

        public void SetDisplayName(string? displayName)
        {
            string cleaned = string.IsNullOrWhiteSpace(displayName) ? "Your Name" : displayName.Trim();
            if (cleaned.Length > 48)
                cleaned = cleaned[..48];

            lock (_gate)
            {
                UserProfileStatsSnapshot snap = EnsureLoadedUnsafe();
                if (string.Equals(snap.DisplayName, cleaned, StringComparison.Ordinal))
                    return;
                snap.DisplayName = cleaned;
                SaveUnsafe(snap);
            }

            NotifyChanged();
        }

        public UserProfileSummary GetSummary(DateTime? todayLocal = null)
        {
            DateTime today = (todayLocal ?? DateTime.Now).Date;
            lock (_gate)
            {
                UserProfileStatsSnapshot snap = EnsureLoadedUnsafe();
                var (current, longest) = ComputeStreaks(snap.DailyTokens, today);
                long lifetime = Math.Max(snap.LifetimeTokens, snap.DailyTokens.Values.Sum());
                string name = string.IsNullOrWhiteSpace(snap.DisplayName) ? "Your Name" : snap.DisplayName;
                return new UserProfileSummary(
                    name,
                    lifetime,
                    FormatTokens(lifetime),
                    current,
                    longest);
            }
        }

        public TokenHeatmapGrid BuildHeatmap(TokenActivityViewMode mode, DateTime? todayLocal = null, int weeks = 52)
        {
            DateTime today = (todayLocal ?? DateTime.Now).Date;
            int totalWeeks = Math.Clamp(weeks, 12, 53);
            const int daysPerWeek = 7;

            // Align end of grid to the week containing `today` (Sunday-start weeks)
            int endOffsetToSaturday = 6 - (int)today.DayOfWeek;
            DateTime gridEnd = today.AddDays(endOffsetToSaturday);
            DateTime gridStart = gridEnd.AddDays(-(totalWeeks * daysPerWeek) + 1);

            Dictionary<string, long> dailyCopy;
            lock (_gate)
            {
                UserProfileStatsSnapshot snap = EnsureLoadedUnsafe();
                dailyCopy = new Dictionary<string, long>(snap.DailyTokens, StringComparer.Ordinal);
            }

            var rawValues = new long[totalWeeks * daysPerWeek];
            long runningCumulative = 0;

            // Pre-accumulate tokens strictly before gridStart for Cumulative mode
            foreach (var kvp in dailyCopy)
            {
                if (DateTime.TryParseExact(kvp.Key, "yyyy-MM-dd", null, System.Globalization.DateTimeStyles.None, out DateTime dt)
                    && dt.Date < gridStart
                    && kvp.Value > 0)
                {
                    runningCumulative += kvp.Value;
                }
            }

            bool seenAnyActivity = runningCumulative > 0;
            for (int w = 0; w < totalWeeks; w++)
            {
                long weekSum = 0;
                if (mode == TokenActivityViewMode.Weekly)
                {
                    for (int d = 0; d < daysPerWeek; d++)
                    {
                        DateTime cellDate = gridStart.AddDays(w * daysPerWeek + d);
                        if (cellDate <= today && dailyCopy.TryGetValue(cellDate.ToString("yyyy-MM-dd"), out long val) && val > 0)
                            weekSum += val;
                    }
                }

                for (int d = 0; d < daysPerWeek; d++)
                {
                    int idx = w * daysPerWeek + d;
                    DateTime cellDate = gridStart.AddDays(idx);
                    if (cellDate > today)
                    {
                        rawValues[idx] = -1; // future date in the current week
                        continue;
                    }

                    long dayTokens = dailyCopy.TryGetValue(cellDate.ToString("yyyy-MM-dd"), out long val) && val > 0 ? val : 0;
                    if (dayTokens > 0)
                        seenAnyActivity = true;

                    runningCumulative += dayTokens;
                    rawValues[idx] = mode switch
                    {
                        TokenActivityViewMode.Daily => dayTokens,
                        TokenActivityViewMode.Weekly => weekSum,
                        TokenActivityViewMode.Cumulative => seenAnyActivity ? runningCumulative : 0,
                        _ => dayTokens
                    };
                }
            }

            long maxVal = rawValues.Where(v => v > 0).DefaultIfEmpty(0).Max();
            var cells = new List<TokenHeatmapCell>(totalWeeks * daysPerWeek);
            for (int i = 0; i < rawValues.Length; i++)
            {
                DateTime cellDate = gridStart.AddDays(i);
                long val = rawValues[i];
                int level;
                if (val <= 0 || maxVal <= 0)
                {
                    level = 0;
                }
                else
                {
                    double ratio = val / (double)maxVal;
                    level = ratio switch
                    {
                        < 0.25 => 1,
                        < 0.50 => 2,
                        < 0.78 => 3,
                        _ => 4
                    };
                }

                string tooltip = val < 0
                    ? cellDate.ToString("MMM d, yyyy")
                    : $"{cellDate:MMM d, yyyy}: {FormatTokens(val)} tokens ({mode})";
                cells.Add(new TokenHeatmapCell(cellDate, Math.Max(0, val), level, tooltip));
            }

            var monthLabels = new List<string>(totalWeeks);
            int lastMonth = -1;
            for (int w = 0; w < totalWeeks; w++)
            {
                DateTime weekStart = gridStart.AddDays(w * daysPerWeek);
                if (weekStart.Month != lastMonth)
                {
                    monthLabels.Add(weekStart.ToString("MMM", System.Globalization.CultureInfo.InvariantCulture));
                    lastMonth = weekStart.Month;
                }
                else
                {
                    monthLabels.Add(string.Empty);
                }
            }

            return new TokenHeatmapGrid(monthLabels, cells, totalWeeks, daysPerWeek);
        }

        public static string FormatTokens(long tokens)
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            if (tokens < 1_000)
                return tokens.ToString(inv);
            if (tokens < 1_000_000)
                return (tokens / 1_000.0).ToString(tokens < 10_000 ? "0.0" : "0.#", inv) + "K";
            if (tokens < 1_000_000_000)
                return (tokens / 1_000_000.0).ToString("0.0", inv) + "M";
            return (tokens / 1_000_000_000.0).ToString("0.0", inv) + "B";
        }

        internal static (int CurrentStreak, int LongestStreak) ComputeStreaks(
            IReadOnlyDictionary<string, long> dailyTokens,
            DateTime today)
        {
            var activeDates = new HashSet<DateTime>();
            foreach (var kvp in dailyTokens)
            {
                if (kvp.Value > 0 && DateTime.TryParseExact(kvp.Key, "yyyy-MM-dd", null, System.Globalization.DateTimeStyles.None, out DateTime dt))
                    activeDates.Add(dt.Date);
            }

            if (activeDates.Count == 0)
                return (0, 0);

            int current = 0;
            DateTime cursor = activeDates.Contains(today)
                ? today
                : (activeDates.Contains(today.AddDays(-1)) ? today.AddDays(-1) : DateTime.MinValue);

            if (cursor != DateTime.MinValue)
            {
                while (activeDates.Contains(cursor))
                {
                    current++;
                    cursor = cursor.AddDays(-1);
                }
            }

            int longest = 0;
            int run = 0;
            DateTime? previous = null;
            foreach (DateTime date in activeDates.OrderBy(d => d))
            {
                if (previous.HasValue && (date - previous.Value).Days == 1)
                    run++;
                else
                    run = 1;

                if (run > longest)
                    longest = run;
                previous = date;
            }

            return (current, longest);
        }

        private UserProfileStatsSnapshot EnsureLoadedUnsafe()
        {
            if (_snapshot != null)
                return _snapshot;

            try
            {
                var loaded = JsonPersistenceRecovery.Load<UserProfileStatsSnapshot>(_storagePath);
                if (loaded.Value != null)
                {
                    loaded.Value.DailyTokens ??= new Dictionary<string, long>(StringComparer.Ordinal);
                    _snapshot = loaded.Value;
                    return _snapshot;
                }
            }
            catch { }

            _snapshot = new UserProfileStatsSnapshot();
            return _snapshot;
        }

        private void SaveUnsafe(UserProfileStatsSnapshot snapshot)
        {
            try
            {
                string? dir = Path.GetDirectoryName(_storagePath);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);
                AtomicFileWriter.WriteAllText(_storagePath, JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }
        }

        private void NotifyChanged()
        {
            try { Changed?.Invoke(); }
            catch { }
        }
    }
}
