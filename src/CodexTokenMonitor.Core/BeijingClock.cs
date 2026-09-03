namespace CodexTokenMonitor;

internal static class BeijingClock
{
    public static DateTimeOffset Now => DateTimeOffset.UtcNow.ToOffset(CodexUsageReader.BeijingOffset);

    public static DateTime DateTimeNow => Now.DateTime;

    public static DateTime Today => Now.Date;
}
