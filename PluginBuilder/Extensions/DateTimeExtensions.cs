namespace PluginBuilder
{
    public static class DateTimeExtensions
    {
        public static string ToTimeAgo(this TimeSpan diff) => diff.TotalSeconds > 0 ? $"{diff.TimeString()} ago" : $"in {diff.Negate().TimeString()}";

        public static string TimeString(this TimeSpan timeSpan)
        {
            if (timeSpan.TotalMinutes < 1)
            {
                return $"{(int)timeSpan.TotalSeconds} second{Plural((int)timeSpan.TotalSeconds)}";
            }
            if (timeSpan.TotalHours < 1)
            {
                return $"{(int)timeSpan.TotalMinutes} minute{Plural((int)timeSpan.TotalMinutes)}";
            }
            return timeSpan.Days < 1
                ? $"{(int)timeSpan.TotalHours} hour{Plural((int)timeSpan.TotalHours)}"
                : $"{(int)timeSpan.TotalDays} day{Plural((int)timeSpan.TotalDays)}";
        }
        private static string Plural(int value)
        {
            return value == 1 ? string.Empty : "s";
        }
    }
}
