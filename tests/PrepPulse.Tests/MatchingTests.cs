using PrepPulse.Core.Matching;
using PrepPulse.Core.Plans;
using static PrepPulse.Tests.TestPlans;

namespace PrepPulse.Tests;

public class MatchingTests
{
    private static readonly DateOnly Monday = new(2026, 11, 9); // week 2 of the interview plan

    [Theory]
    [InlineData("leetcode.com/problems/3sum/", "leetcode.com")]
    [InlineData("https://www.leetcode.com/problems", "leetcode.com")]
    [InlineData("http://localhost:3000/mock", "localhost:3000")]
    [InlineData("localhost:3000", "localhost:3000")]
    [InlineData("old.reddit.com/r/cscareerquestions", "old.reddit.com")]
    [InlineData("chrome://newtab", null)]
    [InlineData("edge://settings", null)]
    [InlineData("how to reverse a linked list", null)]
    [InlineData("", null)]
    public void Domains_are_read_from_address_bar_text(string text, string? expected) =>
        Assert.Equal(expected, DomainExtractor.FromAddress(text));

    [Theory]
    [InlineData("leetcode.com", "leetcode.com", true)]
    [InlineData("leetcode.com", "www.leetcode.com", true)]
    [InlineData("reddit.com", "old.reddit.com", true)]
    [InlineData("reddit.com", "notreddit.com", false)]
    [InlineData("localhost:3000", "localhost:3000", true)]
    [InlineData("localhost:3000", "localhost:5173", false)]
    [InlineData("example.com", "example.com:8080", true)]
    public void Domain_patterns_cover_subdomains_and_ports(string pattern, string domain, bool match) =>
        Assert.Equal(match, EntryMatcher.DomainMatches(pattern, domain));

    [Fact]
    public void Focus_site_is_attributed_to_the_topic_scheduled_this_week()
    {
        var c = Classifier.Classify(Site("leetcode.com", "3Sum - LeetCode"), Interview(), Distractions(), Monday);
        Assert.Equal(ListKind.Focus, c.List);
        Assert.Equal("Graphs", c.Topic!.Name); // weeks 1-3; DP (weeks 4-7) also lists leetcode.com

        var later = Classifier.Classify(Site("leetcode.com"), Interview(), Distractions(), new DateOnly(2026, 11, 30)); // week 5
        Assert.Equal("Dynamic programming", later.Topic!.Name);
    }

    [Fact]
    public void Apps_match_by_program_name_ignoring_case()
    {
        Assert.Equal(ListKind.Focus, Classifier.Classify(App("NotePad.exe"), Interview(), Distractions(), Monday).List);
        Assert.Equal(ListKind.Distraction, Classifier.Classify(App("whatsapp.exe"), Interview(), Distractions(), Monday).List);
        Assert.Equal(ListKind.Neither, Classifier.Classify(App("outlook.exe"), Interview(), Distractions(), Monday).List);
    }

    [Fact]
    public void Most_specific_entry_wins_across_lists()
    {
        var talk = Classifier.Classify(Site("youtube.com", "System Design Interview – YouTube"), Interview(), Distractions(), Monday);
        Assert.Equal(ListKind.Focus, talk.List);
        Assert.True(talk.Passive);

        var cats = Classifier.Classify(Site("youtube.com", "Funny cats – YouTube"), Interview(), Distractions(), Monday);
        Assert.Equal(ListKind.Distraction, cats.List);
    }

    [Fact]
    public void Site_rule_beats_app_rule_for_browsers()
    {
        var distractions = PlanLoader.ParseDistractions("distractions: [chrome.exe]");
        var c = Classifier.Classify(Site("leetcode.com"), Interview(), distractions, Monday);
        Assert.Equal(ListKind.Focus, c.List);
    }

    [Fact]
    public void When_the_address_bar_cant_be_read_the_title_is_used()
    {
        var obs = new Observation("msedge.exe", "3Sum - LeetCode - Microsoft Edge", null, true, false);
        Assert.Equal(ListKind.Focus, Classifier.Classify(obs, Interview(), Distractions(), Monday).List);
    }

    [Fact]
    public void Private_windows_are_never_matched_to_sites()
    {
        var obs = new Observation("chrome.exe", "LeetCode - Incognito", "leetcode.com", true, true);
        Assert.Equal(ListKind.Neither, Classifier.Classify(obs, Interview(), Distractions(), Monday).List);
        Assert.True(Browsers.IsPrivateTitle("New tab - Personal - Microsoft Edge [InPrivate]"));
    }

    [Fact]
    public void Another_plan_can_claim_a_site()
    {
        var plans = new[] { Interview(), Learning() };
        var other = Classifier.OtherPlanFor(Site("huggingface.co"), plans, plans[0], Distractions(), Monday);
        Assert.Equal("learning-ai", other!.Slug);
    }
}
