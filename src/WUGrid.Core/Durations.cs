using System;

namespace WUGrid.Core
{
    public static class Durations
    {
        /// <summary>"1h05m" from an hour up, "3m07s" below.</summary>
        public static string Format(TimeSpan t)
        {
            if (t < TimeSpan.Zero) t = TimeSpan.Zero;
            return t.TotalHours >= 1
                ? $"{(int)Math.Floor(t.TotalHours)}h{t.Minutes:00}m"
                : $"{t.Minutes}m{t.Seconds:00}s";
        }
    }
}
