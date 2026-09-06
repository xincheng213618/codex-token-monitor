using Xunit;

namespace CodexTokenMonitor;

public sealed class BeijingClockTests
{
    [Fact]
    public void NowUsesBeijingOffset()
    {
        Assert.Equal(CodexUsageReader.BeijingOffset, BeijingClock.Now.Offset);
    }

    [Fact]
    public void DateTimeAndDatePropertiesUseTheSameBeijingCalendar()
    {
        var now = BeijingClock.Now;

        Assert.Equal(now.DateTime.Date, BeijingClock.Today);
        Assert.Equal(now.DateTime.Date, BeijingClock.DateTimeNow.Date);
    }
}
