using PrepPulse.Core;
using PrepPulse.Core.Plans;

namespace PrepPulse.Tests;

public class PlanTests
{
    [Theory]
    [InlineData("2h", 120)]
    [InlineData("90m", 90)]
    [InlineData("1h30m", 90)]
    [InlineData("1h 30 min", 90)]
    [InlineData("1.5h", 90)]
    [InlineData("45", 45)]
    [InlineData("0.5", 0.5)]
    public void Durations_parse(string text, double minutes)
    {
        Assert.True(Durations.TryParse(text, out var value));
        Assert.Equal(minutes, value.TotalMinutes, 3);
    }

    [Theory]
    [InlineData("")]
    [InlineData("soon")]
    [InlineData("2 days")]
    public void Durations_reject_nonsense(string text) => Assert.False(Durations.TryParse(text, out _));

    [Theory]
    [InlineData(30, "30 seconds")]
    [InlineData(90, "90 seconds")]
    [InlineData(120, "2 minutes")]
    [InlineData(300, "5 minutes")]
    public void Durations_spoken(int seconds, string text) => Assert.Equal(text, Durations.Spoken(TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void Durations_format() =>
        Assert.Equal(new[] { "45m", "2h", "2h 05m" }, new[] { 45, 120, 125 }.Select(m => Durations.Format(TimeSpan.FromMinutes(m))).ToArray());

    [Theory]
    [InlineData("interview-prep.yaml", "Interview prep — AI engineer", 7)]
    [InlineData("learning-ai.yaml", "Learning AI", 4)]
    public void Example_plans_in_the_repo_load(string file, string name, int topics)
    {
        var plan = PlanLoader.LoadFile(Path.Combine(AppContext.BaseDirectory, "plans", file));
        Assert.Equal(name, plan.Name);
        Assert.Equal(topics, plan.Topics.Count);
        Assert.All(plan.Topics, t => Assert.NotEmpty(t.Entries));
    }

    [Fact]
    public void Example_interview_plan_details()
    {
        var plan = PlanLoader.LoadFile(Path.Combine(AppContext.BaseDirectory, "plans", "interview-prep.yaml"));
        Assert.Equal(new DateOnly(2026, 12, 18), plan.End);
        Assert.Equal(136.0, plan.TotalTopicHours ?? 0);
        var mock = plan.Topics.Single(t => t.Name == "Mock interviews");
        Assert.Equal(Category.Mock, mock.Category);
        Assert.Contains(mock.Entries, e => e.Kind == EntryKind.Site && e.Pattern == "localhost:3000");
        var llm = plan.Topics.Single(t => t.Name.StartsWith("LLM"));
        Assert.Contains(llm.Entries, e => e.PassiveOk && e.Pattern == "youtube.com" && e.TitleContains == "LLM");
        Assert.Equal(3, plan.Weeks[1].Count);
    }

    [Fact]
    public void Daily_targets_follow_weekdays_days_off_and_overrides()
    {
        var plan = TestPlans.Interview();
        Assert.Equal(TimeSpan.FromHours(2), plan.ExpectedOn(new DateOnly(2026, 11, 9)));   // Monday
        Assert.Equal(TimeSpan.FromHours(4), plan.ExpectedOn(new DateOnly(2026, 11, 14)));  // Saturday
        Assert.Equal(TimeSpan.Zero, plan.ExpectedOn(new DateOnly(2026, 11, 29)));          // day off
        Assert.Equal(TimeSpan.FromHours(6), plan.ExpectedOn(new DateOnly(2026, 12, 13)));  // override
        Assert.Equal(TimeSpan.Zero, plan.ExpectedOn(new DateOnly(2026, 11, 1)));           // before start
        Assert.Equal(TimeSpan.Zero, plan.ExpectedOn(new DateOnly(2026, 12, 19)));          // after end
    }

    [Fact]
    public void Weekly_target_is_spread_evenly()
    {
        var plan = TestPlans.Learning();
        Assert.True(plan.IsOpenEnded);
        Assert.Equal(TimeSpan.FromHours(1), plan.ExpectedOn(new DateOnly(2026, 11, 9)));
        Assert.Equal(TimeSpan.FromHours(7), plan.ExpectedBetween(new DateOnly(2026, 11, 9), new DateOnly(2026, 11, 15)));
    }

    [Fact]
    public void Week_one_starts_on_the_start_date()
    {
        var plan = TestPlans.Interview();
        Assert.Equal(1, plan.WeekOf(new DateOnly(2026, 11, 2)));
        Assert.Equal(1, plan.WeekOf(new DateOnly(2026, 11, 8)));
        Assert.Equal(2, plan.WeekOf(new DateOnly(2026, 11, 9)));
        Assert.Equal(0, plan.WeekOf(new DateOnly(2026, 11, 1)));
    }

    [Fact]
    public void Missing_pieces_are_reported_together()
    {
        var ex = Assert.Throws<PlanFormatException>(() => PlanLoader.Parse("name: Broken\n", "broken"));
        Assert.Contains("start:", ex.Message);
        Assert.Contains("targets:", ex.Message);
        Assert.Contains("topics:", ex.Message);
    }

    [Fact]
    public void Bad_values_name_the_field()
    {
        const string yaml = """
            name: Bad
            start: 2026-11-01
            end: 2026-10-01
            targets: { weekdays: lots }
            topics:
              - name: A
                hours: many
                weeks: 5-2
                focus: [a.com]
            """;
        var ex = Assert.Throws<PlanFormatException>(() => PlanLoader.Parse(yaml, "bad"));
        Assert.Contains("end: is before start", ex.Message);
        Assert.Contains("targets.weekdays", ex.Message);
        Assert.Contains("hours 'many'", ex.Message);
        Assert.Contains("weeks '5-2'", ex.Message);
    }

    [Fact]
    public void Checklist_lines_with_a_colon_are_kept_as_text()
    {
        var plan = PlanLoader.Parse(TestPlans.InterviewYaml + "\nweeks:\n  2:\n    - Timed design: rate limiter (45 min)\n    - BFS drills\n", "x");
        Assert.Equal(new[] { "Timed design: rate limiter (45 min)", "BFS drills" }, plan.Weeks[2]);
    }

    [Fact]
    public void Invalid_yaml_is_a_plan_error_not_a_crash()
    {
        var ex = Assert.Throws<PlanFormatException>(() => PlanLoader.Parse("name: [unclosed\nstart: 2026-01-01", "x"));
        Assert.Contains("Not valid YAML", ex.Message);
    }

    [Fact]
    public void Folder_load_keeps_good_plans_and_lists_bad_ones()
    {
        var root = Directory.CreateTempSubdirectory("pp-config").FullName;
        var dir = Directory.CreateDirectory(Path.Combine(root, "plans")).FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, "good.yaml"), TestPlans.InterviewYaml);
            File.WriteAllText(Path.Combine(dir, "bad.yaml"), "name: nope");
            var distractions = Path.Combine(root, "distractions.yaml");
            File.WriteAllText(distractions, "distractions: [WhatsApp.exe, reddit.com]");

            var snap = PlanLoader.LoadFolder(dir, distractions);

            Assert.Single(snap.Plans);
            Assert.Equal("good", snap.Plans[0].Slug);
            Assert.Single(snap.Errors);
            Assert.Equal("bad.yaml", snap.Errors[0].File);
            Assert.Equal(2, snap.Distractions.Count);
            Assert.NotNull(snap.Find("GOOD"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Settings_template_is_valid_json_with_comments()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pp-settings-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, SettingsFile.Template);
        try
        {
            var s = SettingsFile.Load(path, out var error);
            Assert.Null(error);
            Assert.NotNull(s);
            Assert.Equal(2, s!.Alerts.DistractionMinutes);
            Assert.Equal(30, s.Session.EndAfterLockedMinutes);
        }
        finally { File.Delete(path); }
    }
}
