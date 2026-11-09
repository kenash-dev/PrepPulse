using PrepPulse.Core;

namespace PrepPulse.Tests;

public class HousekeepingTests
{
    [Fact]
    public void Only_preppulse_logs_older_than_the_cutoff_expire()
    {
        var dir = Path.Combine("C:", "data", "logs");
        string P(string name) => Path.Combine(dir, name);
        var files = new[]
        {
            P("preppulse-20261009.log"),   // 31 days old: expired
            P("preppulse-20261010.log"),   // exactly 30 days old: kept
            P("preppulse-20261109.log"),   // today
            P("preppulse-20250101.log"),   // very old: expired
            P("preppulse-notadate.log"),   // not ours to judge
            P("other-20200101.log"),       // someone else's file
            P("preppulse-20200101.txt"),   // wrong extension
        };

        var expired = Housekeeping.ExpiredLogs(files, new DateOnly(2026, 11, 9), keepDays: 30);

        Assert.Equal(new[] { P("preppulse-20261009.log"), P("preppulse-20250101.log") }, expired);
    }

    [Theory]
    [InlineData(10, 1, true)]     // active: every second
    [InlineData(119, 1, true)]    // still under the 2-minute idle threshold
    [InlineData(120, 1, false)]   // idle: skip until 5 s have passed
    [InlineData(600, 4, false)]
    [InlineData(600, 5, true)]
    [InlineData(600, 30, true)]
    public void Sampling_slows_to_every_5_seconds_only_while_idle(int idleSeconds, int sinceLastSeconds, bool expected)
    {
        var sample = Housekeeping.ShouldSample(
            TimeSpan.FromSeconds(idleSeconds), TimeSpan.FromSeconds(sinceLastSeconds), idleAfter: TimeSpan.FromMinutes(2));
        Assert.Equal(expected, sample);
    }

    [Fact]
    public void Idle_dashboard_timeout_defaults_to_10_minutes_and_is_in_the_template()
    {
        Assert.Equal(10, new AppSettings().DashboardIdleMinutes);
        var path = Path.Combine(Path.GetTempPath(), $"preppulse-settings-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, SettingsFile.Template.Replace("\"DashboardIdleMinutes\": 10", "\"DashboardIdleMinutes\": 3"));
            var s = SettingsFile.Load(path, out var error);
            Assert.Null(error);
            Assert.Equal(3, s!.DashboardIdleMinutes);
        }
        finally { File.Delete(path); }
    }
}
