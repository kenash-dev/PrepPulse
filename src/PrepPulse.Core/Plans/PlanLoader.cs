using System.Globalization;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace PrepPulse.Core.Plans;

public sealed record PlanError(string File, string Message);

public sealed class PlanFormatException(string message) : Exception(message);

/// <summary>Everything loaded from the config folder at one moment: plans, their errors, and the shared distraction list.</summary>
public sealed class PlanCatalogSnapshot
{
    public static readonly PlanCatalogSnapshot Empty = new();

    public IReadOnlyList<Plan> Plans { get; init; } = [];
    public IReadOnlyList<PlanError> Errors { get; init; } = [];
    public IReadOnlyList<FocusEntry> Distractions { get; init; } = [];

    public Plan? Find(string? slug) =>
        slug is null ? null : Plans.FirstOrDefault(p => string.Equals(p.Slug, slug, StringComparison.OrdinalIgnoreCase));
}

public static class PlanLoader
{
    private static readonly IDeserializer Yaml = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    public static PlanCatalogSnapshot LoadFolder(string plansDir, string? distractionsFile)
    {
        var plans = new List<Plan>();
        var errors = new List<PlanError>();

        if (Directory.Exists(plansDir))
        {
            var files = Directory.EnumerateFiles(plansDir, "*.yaml")
                .Concat(Directory.EnumerateFiles(plansDir, "*.yml"))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase);
            foreach (var file in files)
            {
                try { plans.Add(LoadFile(file)); }
                catch (Exception ex) when (ex is PlanFormatException or IOException or UnauthorizedAccessException)
                {
                    errors.Add(new PlanError(Path.GetFileName(file), ex.Message));
                }
            }
        }

        IReadOnlyList<FocusEntry> distractions = [];
        if (distractionsFile is not null && File.Exists(distractionsFile))
        {
            try { distractions = ParseDistractions(File.ReadAllText(distractionsFile)); }
            catch (Exception ex) when (ex is PlanFormatException or IOException)
            {
                errors.Add(new PlanError(Path.GetFileName(distractionsFile), ex.Message));
            }
        }

        return new PlanCatalogSnapshot
        {
            Plans = plans.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList(),
            Errors = errors,
            Distractions = distractions,
        };
    }

    public static Plan LoadFile(string path) =>
        Parse(File.ReadAllText(path), Path.GetFileNameWithoutExtension(path).ToLowerInvariant(), path);

    public static Plan Parse(string yaml, string slug, string filePath = "")
    {
        PlanDto? dto;
        try { dto = Yaml.Deserialize<PlanDto>(yaml); }
        catch (YamlException ex) { throw new PlanFormatException($"Not valid YAML near line {ex.Start.Line}: {(ex.InnerException ?? ex).Message}"); }
        if (dto is null) throw new PlanFormatException("The file is empty.");

        var errors = new List<string>();

        if (!TryDate(dto.Start, out var start)) errors.Add("start: missing or not a date (use YYYY-MM-DD).");
        DateOnly? end = null;
        if (!string.IsNullOrWhiteSpace(dto.End))
        {
            if (TryDate(dto.End, out var e)) end = e;
            else errors.Add("end: not a date (use YYYY-MM-DD).");
        }
        if (end is { } endDate && endDate < start && errors.Count == 0) errors.Add("end: is before start.");

        var t = dto.Targets ?? new TargetsDto();
        var weekday = Dur(t.Weekdays, "targets.weekdays", errors);
        var weekend = Dur(t.Weekends, "targets.weekends", errors);
        var weekly = Dur(t.Weekly, "targets.weekly", errors);
        if (weekday is null && weekend is null && weekly is null)
            errors.Add("targets: set weekdays and weekends (e.g. 2h and 4h), or weekly (e.g. 7h).");

        var daysOff = new HashSet<DateOnly>();
        foreach (var d in t.DaysOff ?? [])
        {
            if (TryDate(d, out var day)) daysOff.Add(day);
            else errors.Add($"targets.days_off: '{d}' is not a date.");
        }

        var overrides = new Dictionary<DateOnly, TimeSpan>();
        foreach (var (k, v) in t.Overrides ?? [])
        {
            if (!TryDate(k, out var day)) { errors.Add($"targets.overrides: '{k}' is not a date."); continue; }
            if (!Durations.TryParse(v, out var len)) { errors.Add($"targets.overrides.{k}: '{v}' is not a duration."); continue; }
            overrides[day] = len;
        }

        var topics = new List<Topic>();
        if (dto.Topics is null || dto.Topics.Count == 0)
            errors.Add("topics: add at least one topic with the apps or sites that count as studying it.");
        foreach (var (td, i) in (dto.Topics ?? []).Select((x, i) => (x, i)))
        {
            var where = $"topics[{i + 1}]";
            if (string.IsNullOrWhiteSpace(td.Name)) { errors.Add($"{where}: name is missing."); continue; }

            double? hours = null;
            if (!string.IsNullOrWhiteSpace(td.Hours))
            {
                if (double.TryParse(td.Hours, NumberStyles.Float, CultureInfo.InvariantCulture, out var h)) hours = h;
                else if (Durations.TryParse(td.Hours, out var hs)) hours = hs.TotalHours;
                else errors.Add($"{where} ({td.Name}): hours '{td.Hours}' is not a number.");
            }

            int? from = null, to = null;
            if (!string.IsNullOrWhiteSpace(td.Weeks) && !TryWeeks(td.Weeks, out from, out to))
                errors.Add($"{where} ({td.Name}): weeks '{td.Weeks}' should look like 3 or 2-5.");

            var entries = new List<FocusEntry>();
            foreach (var f in td.Focus ?? []) if (!string.IsNullOrWhiteSpace(f)) entries.Add(FocusEntry.Parse(f));
            foreach (var f in td.Passive ?? []) if (!string.IsNullOrWhiteSpace(f)) entries.Add(FocusEntry.Parse(f, passiveOk: true));

            topics.Add(new Topic
            {
                Name = td.Name.Trim(),
                Category = Categories.Parse(td.Category),
                Hours = hours,
                WeekFrom = from,
                WeekTo = to,
                Entries = entries,
            });
        }

        if (topics.Count > 0 && topics.All(x => x.Entries.Count == 0))
            errors.Add("topics: list at least one app or site under focus: so PrepPulse can tell when you're studying.");

        var weeks = new Dictionary<int, IReadOnlyList<string>>();
        foreach (var (k, v) in dto.Weeks ?? [])
        {
            if (int.TryParse(k, NumberStyles.Integer, CultureInfo.InvariantCulture, out var w) && v is not null)
                weeks[w] = v.Select(Flatten).Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
        }

        if (errors.Count > 0) throw new PlanFormatException(string.Join(" ", errors));

        return new Plan
        {
            Slug = slug,
            Name = string.IsNullOrWhiteSpace(dto.Name) ? slug : dto.Name.Trim(),
            Colour = (dto.Colour ?? dto.Color ?? "blue").Trim(),
            Start = start,
            End = end,
            Paused = dto.Paused,
            Weekday = weekday,
            Weekend = weekend,
            Weekly = weekly,
            DaysOff = daysOff,
            Overrides = overrides,
            Topics = topics,
            Weeks = weeks,
            FilePath = filePath,
        };
    }

    public static IReadOnlyList<FocusEntry> ParseDistractions(string yaml)
    {
        DistractionsDto? dto;
        try { dto = Yaml.Deserialize<DistractionsDto>(yaml); }
        catch (YamlException ex) { throw new PlanFormatException($"Not valid YAML near line {ex.Start.Line}: {(ex.InnerException ?? ex).Message}"); }
        return (dto?.Distractions ?? [])
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => FocusEntry.Parse(x))
            .ToList();
    }

    /// <summary>A checklist line like "Timed design: rate limiter" is a mapping in YAML; turn it back into text.</summary>
    private static string Flatten(object? item) => item switch
    {
        null => "",
        string s => s,
        IDictionary<object, object> map => string.Join(", ", map.Select(kv => $"{kv.Key}: {Flatten(kv.Value)}")),
        IEnumerable<object> list => string.Join(", ", list.Select(Flatten)),
        _ => item.ToString() ?? "",
    };

    private static TimeSpan? Dur(string? text, string field, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (Durations.TryParse(text, out var v)) return v;
        errors.Add($"{field}: '{text}' is not a duration (use e.g. 2h, 90m, 1h30m).");
        return null;
    }

    private static bool TryDate(string? s, out DateOnly date)
    {
        date = default;
        if (string.IsNullOrWhiteSpace(s)) return false;
        var t = s.Trim();
        return DateOnly.TryParseExact(t, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date)
            || DateOnly.TryParse(t, CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
    }

    private static bool TryWeeks(string s, out int? from, out int? to)
    {
        from = to = null;
        var parts = s.Replace('–', '-').Replace('—', '-').Split('-', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 1 && int.TryParse(parts[0], out var one) && one > 0) { from = to = one; return true; }
        if (parts.Length == 2 && int.TryParse(parts[0], out var a) && int.TryParse(parts[1], out var b) && a > 0 && b >= a)
        {
            from = a; to = b; return true;
        }
        return false;
    }
}

// YAML shapes. Scalars are read as strings and validated by hand so errors can name the field.
public sealed class PlanDto
{
    public string? Name { get; set; }
    public string? Colour { get; set; }
    public string? Color { get; set; }
    public string? Start { get; set; }
    public string? End { get; set; }
    public bool Paused { get; set; }
    public TargetsDto? Targets { get; set; }
    public List<TopicDto>? Topics { get; set; }
    public Dictionary<string, List<object>>? Weeks { get; set; }
}

public sealed class TargetsDto
{
    public string? Weekdays { get; set; }
    public string? Weekends { get; set; }
    public string? Weekly { get; set; }
    public List<string>? DaysOff { get; set; }
    public Dictionary<string, string>? Overrides { get; set; }
}

public sealed class TopicDto
{
    public string? Name { get; set; }
    public string? Category { get; set; }
    public string? Hours { get; set; }
    public string? Weeks { get; set; }
    public List<string>? Focus { get; set; }
    public List<string>? Passive { get; set; }
}

public sealed class DistractionsDto
{
    public List<string>? Distractions { get; set; }
}
