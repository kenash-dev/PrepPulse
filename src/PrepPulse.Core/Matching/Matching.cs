using PrepPulse.Core.Plans;

namespace PrepPulse.Core.Matching;

public enum ListKind { Focus, Distraction, Neither }

/// <summary>What was in focus at one moment. Process is the lower-case file name (chrome.exe); Domain is set for browsers when the address bar could be read.</summary>
public sealed record Observation(string Process, string? Title, string? Domain, bool IsBrowser, bool IsPrivate)
{
    public string Display => IsPrivate ? "Private browsing" : Domain ?? ProcessDisplay(Process);

    public static string ProcessDisplay(string process) =>
        process.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? process[..^4] : process;
}

public sealed record Classification(ListKind List, FocusEntry? Entry, Topic? Topic, string? PlanSlug)
{
    public static readonly Classification Neither = new(ListKind.Neither, null, null, null);
    public bool Passive => List == ListKind.Focus && Entry?.PassiveOk == true;
}

public static class Browsers
{
    private static readonly HashSet<string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrome.exe", "msedge.exe", "brave.exe", "firefox.exe", "opera.exe", "vivaldi.exe", "arc.exe", "iexplore.exe",
    };

    public static bool Is(string process) => Names.Contains(process);

    public static bool IsPrivateTitle(string? title) =>
        title is not null &&
        (title.Contains("InPrivate", StringComparison.OrdinalIgnoreCase) ||
         title.Contains("Incognito", StringComparison.OrdinalIgnoreCase) ||
         title.Contains("Private Browsing", StringComparison.OrdinalIgnoreCase));
}

/// <summary>Turns address-bar text into a domain: "leetcode.com/problems/3sum" → leetcode.com, "localhost:3000/x" → localhost:3000.</summary>
public static class DomainExtractor
{
    private static readonly string[] InternalSchemes = ["chrome:", "edge:", "brave:", "about:", "file:", "view-source:", "chrome-extension:", "moz-extension:", "devtools:"];

    public static string? FromAddress(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var t = text.Trim();
        if (t.Contains(' ')) return null; // someone is typing a search
        foreach (var s in InternalSchemes)
            if (t.StartsWith(s, StringComparison.OrdinalIgnoreCase)) return null;

        if (!t.Contains("://", StringComparison.Ordinal)) t = "https://" + t;
        if (!Uri.TryCreate(t, UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return null;

        var host = uri.Host.ToLowerInvariant();
        if (host.StartsWith("www.", StringComparison.Ordinal)) host = host[4..];
        if (host.Length == 0 || (!host.Contains('.') && host != "localhost")) return null;
        return uri.IsDefaultPort ? host : $"{host}:{uri.Port}";
    }
}

public static class EntryMatcher
{
    /// <summary>
    /// How specifically an entry matches, or -1. App = 1, site by title fallback = 1.5, site by domain = 2,
    /// +4 when the entry also requires title words. The highest score wins.
    /// </summary>
    public static double Score(FocusEntry e, Observation o)
    {
        double score;
        if (e.Kind == EntryKind.App)
        {
            if (!string.Equals(e.Pattern, o.Process, StringComparison.OrdinalIgnoreCase)) return -1;
            score = 1;
        }
        else
        {
            if (o.IsPrivate) return -1;
            if (o.Domain is { } domain)
            {
                if (!DomainMatches(e.Pattern, domain)) return -1;
                score = 2;
            }
            else if (o.IsBrowser && TitleMentionsSite(e.Pattern, o.Title)) score = 1.5;
            else return -1;
        }

        if (e.TitleContains is { } words)
        {
            if (o.Title is null || o.Title.IndexOf(words, StringComparison.OrdinalIgnoreCase) < 0) return -1;
            score += 4;
        }
        return score;
    }

    /// <summary>leetcode.com matches leetcode.com and any subdomain; a pattern with a port must match the port.</summary>
    public static bool DomainMatches(string pattern, string domain)
    {
        var d = domain.ToLowerInvariant();
        if (!pattern.Contains(':'))
        {
            var colon = d.IndexOf(':');
            if (colon >= 0) d = d[..colon];
        }
        return d == pattern || d.EndsWith("." + pattern, StringComparison.Ordinal);
    }

    /// <summary>Fallback when the address bar can't be read: "3Sum - LeetCode - Google Chrome" mentions leetcode.com.</summary>
    private static bool TitleMentionsSite(string pattern, string? title)
    {
        if (title is null) return false;
        var host = pattern.Split(':')[0];
        if (host == "localhost") return false;
        var brand = host.Split('.')[0];
        return brand.Length >= 4 && title.IndexOf(brand, StringComparison.OrdinalIgnoreCase) >= 0;
    }
}

public static class Classifier
{
    /// <summary>
    /// Decides whether an observation is focus (for the active plan), a distraction, or neither.
    /// Most specific entry wins; on a tie the focus list beats the distraction list, and a topic scheduled
    /// for the current week beats one that isn't.
    /// </summary>
    public static Classification Classify(Observation o, Plan? plan, IReadOnlyList<FocusEntry> distractions, DateOnly today)
    {
        double best = -1;
        var bestList = ListKind.Neither;
        FocusEntry? bestEntry = null;
        Topic? bestTopic = null;

        if (plan is not null)
        {
            var week = plan.WeekOf(today);
            foreach (var topic in plan.Topics)
            {
                foreach (var entry in topic.Entries)
                {
                    var s = EntryMatcher.Score(entry, o);
                    if (s < 0) continue;
                    var better = s > best ||
                                 (s == best && bestTopic is not null && !bestTopic.IsScheduled(week) && topic.IsScheduled(week));
                    if (!better) continue;
                    best = s; bestList = ListKind.Focus; bestEntry = entry; bestTopic = topic;
                }
            }
        }

        foreach (var entry in distractions)
        {
            var s = EntryMatcher.Score(entry, o);
            if (s > best)
            {
                best = s; bestList = ListKind.Distraction; bestEntry = entry; bestTopic = null;
            }
        }

        if (best < 0) return Classification.Neither;
        return new Classification(bestList, bestEntry, bestTopic, bestList == ListKind.Focus ? plan!.Slug : null);
    }

    /// <summary>Another (non-paused) plan whose focus list claims this observation, if any.</summary>
    public static Plan? OtherPlanFor(Observation o, IEnumerable<Plan> plans, Plan? active, IReadOnlyList<FocusEntry> distractions, DateOnly today)
    {
        foreach (var p in plans)
        {
            if (p.Paused || (active is not null && p.Slug == active.Slug)) continue;
            if (Classify(o, p, distractions, today).List == ListKind.Focus) return p;
        }
        return null;
    }
}
