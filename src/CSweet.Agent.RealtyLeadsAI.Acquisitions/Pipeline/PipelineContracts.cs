using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CSweet.Agent.RealtyLeadsAI.Acquisitions;

/// <summary>The eleven reviewed RealtyLeadsAI fields, as published in a lead brief's data block.</summary>
public sealed record InheritanceLead(string InheritanceLeadId, string? PropertyId, string? Address, string? OwnerName, string? Status, double? Score,
    string? DeceasedId, string? DeceasedName, string? CityName, string? CountyName, string? StateAbbreviation);

/// <summary>Pipeline stages a lead card moves through. Names are stable keys shown in card titles.</summary>
public static class PipelineStages
{
    public const string New = "New";
    public const string Qualified = "Qualified";
    public const string OutreachPlanned = "Outreach planned";
    public const string Contacted = "Contacted";
    public const string Negotiating = "Negotiating";
    public const string Closed = "Closed";
    public const string Dead = "Dead";
    public static readonly IReadOnlyList<string> All = [New, Qualified, OutreachPlanned, Contacted, Negotiating, Closed, Dead];
    public static bool IsTerminal(string stage) => stage is Closed or Dead;
    /// <summary>Resolves a stage name case-insensitively, accepting hyphens or underscores for the space.</summary>
    public static string? Resolve(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var wanted = text.Trim().Replace('-', ' ').Replace('_', ' ');
        return All.FirstOrDefault(s => s.Equals(wanted, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>Shared one-line command grammar: <c>prefix key=value key="quoted value"</c>.</summary>
internal static class CommandLine
{
    private static readonly Regex Pair = new("""(?<key>[A-Za-z]+)=(?:"(?<quoted>[^"\r\n]{0,256})"|(?<bare>[^\s"]{1,256}))""", RegexOptions.CultureInvariant);

    /// <summary>Returns null when <paramref name="text"/> does not start with <paramref name="prefix"/>; otherwise the pairs or an error.</summary>
    public static (Dictionary<string, string>? Values, string? Error)? Parse(string? text, string prefix, params string[] allowedKeys)
    {
        if (text is null) return null;
        var line = text.Trim();
        if (!line.StartsWith(prefix, StringComparison.Ordinal)) return null;
        var rest = line[prefix.Length..];
        if (rest.Length > 2048 || rest.Any(char.IsControl)) return (null, "The command is too long or contains control characters.");
        var values = new Dictionary<string, string>(StringComparer.Ordinal); var consumed = 0;
        foreach (Match m in Pair.Matches(rest))
        {
            if (!string.IsNullOrWhiteSpace(rest[consumed..m.Index])) return (null, "The command contains text that is not a key=value pair.");
            consumed = m.Index + m.Length;
            var key = m.Groups["key"].Value;
            if (!allowedKeys.Contains(key, StringComparer.Ordinal)) return (null, $"Unknown key '{key}'. Allowed: {string.Join(", ", allowedKeys)}.");
            if (!values.TryAdd(key, m.Groups["quoted"].Success ? m.Groups["quoted"].Value : m.Groups["bare"].Value)) return (null, $"The command repeats '{key}'.");
        }
        if (!string.IsNullOrWhiteSpace(rest[consumed..])) return (null, "The command contains text that is not a key=value pair.");
        return (values, null);
    }
}

/// <summary><c>pipeline.v1 lead=&lt;uuid&gt; stage=Qualified [note="..."]</c> — the owner moves a lead card.</summary>
public sealed record PipelineCommand(string LeadId, string Stage, string? Note)
{
    public const string Prefix = "pipeline.v1";
    public static bool TryParse(string? text, out PipelineCommand? command, out string? error)
    {
        command = null; error = null;
        var parsed = CommandLine.Parse(text, Prefix, "lead", "stage", "note");
        if (parsed is null) return false;
        if (parsed.Value.Error is not null) { error = parsed.Value.Error; return true; }
        var v = parsed.Value.Values!;
        if (!v.TryGetValue("lead", out var lead) || !Guid.TryParseExact(lead, "D", out _)) { error = "lead must be the lead's UUID exactly as shown on its card."; return true; }
        if (PipelineStages.Resolve(v.GetValueOrDefault("stage")) is not { } stage) { error = $"stage must be one of: {string.Join(", ", PipelineStages.All)}."; return true; }
        command = new(lead.ToLowerInvariant(), stage, v.TryGetValue("note", out var note) && !string.IsNullOrWhiteSpace(note) ? note.Trim() : null);
        return true;
    }
}

/// <summary><c>outreach-draft.v1 lead=&lt;uuid&gt; [tone=warm|formal] [from="Your name"]</c> — draft a letter for owner approval.</summary>
public sealed record OutreachCommand(string LeadId, string Tone, string? From)
{
    public const string Prefix = "outreach-draft.v1";
    public static bool TryParse(string? text, out OutreachCommand? command, out string? error)
    {
        command = null; error = null;
        var parsed = CommandLine.Parse(text, Prefix, "lead", "tone", "from");
        if (parsed is null) return false;
        if (parsed.Value.Error is not null) { error = parsed.Value.Error; return true; }
        var v = parsed.Value.Values!;
        if (!v.TryGetValue("lead", out var lead) || !Guid.TryParseExact(lead, "D", out _)) { error = "lead must be the lead's UUID exactly as shown on its card."; return true; }
        var tone = v.GetValueOrDefault("tone", "warm").Trim().ToLowerInvariant();
        if (tone is not ("warm" or "formal")) { error = "tone must be warm or formal."; return true; }
        command = new(lead.ToLowerInvariant(), tone, v.TryGetValue("from", out var from) && !string.IsNullOrWhiteSpace(from) ? from.Trim() : null);
        return true;
    }
}

/// <summary>
/// The analyst's research brief line, re-implemented here so the manager can validate what it sends and what the model
/// proposes. Must stay byte-compatible with the analyst's <c>research-brief.v1</c> grammar.
/// </summary>
public sealed record ResearchBrief(string State, string? County, string? City, string? Status, double? MinScore, int Limit, string? Purpose)
{
    public const string Prefix = "research-brief.v1";
    public const int DefaultLimit = 10;
    public const int MaximumLimit = 100;

    public string ToInstruction()
    {
        var parts = new List<string> { Prefix, $"state={State}" };
        if (County is not null) parts.Add($"county=\"{County}\"");
        if (City is not null) parts.Add($"city=\"{City}\"");
        if (Status is not null) parts.Add($"status=\"{Status}\"");
        if (MinScore is { } s) parts.Add($"minScore={s.ToString("0.###", CultureInfo.InvariantCulture)}");
        parts.Add($"limit={Limit.ToString(CultureInfo.InvariantCulture)}");
        if (Purpose is not null) parts.Add($"purpose=\"{Purpose}\"");
        return string.Join(' ', parts);
    }

    public string Scope() => string.Join(", ", new[] { City, County is null ? null : County + " County", State }.Where(x => x is not null));

    public static bool TryParse(string? text, out ResearchBrief? brief, out string? error)
    {
        brief = null; error = null;
        var parsed = CommandLine.Parse(text, Prefix, "state", "county", "city", "status", "minScore", "limit", "purpose");
        if (parsed is null) return false;
        if (parsed.Value.Error is not null) { error = parsed.Value.Error; return true; }
        var v = parsed.Value.Values!;
        if (!v.TryGetValue("state", out var state) || state.Length != 2 || !state.All(char.IsAsciiLetter)) { error = "The research brief needs state=XX with a two-letter state abbreviation."; return true; }
        double? minScore = null;
        if (v.TryGetValue("minScore", out var scoreText))
        {
            if (!double.TryParse(scoreText, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var score) || !double.IsFinite(score) || score is < 0 or > 100)
            { error = "minScore must be a number from 0 to 100."; return true; }
            minScore = score;
        }
        var limit = DefaultLimit;
        if (v.TryGetValue("limit", out var limitText) && (!int.TryParse(limitText, NumberStyles.None, CultureInfo.InvariantCulture, out limit) || limit is < 1 or > MaximumLimit))
        { error = $"limit must be a whole number from 1 to {MaximumLimit}."; return true; }
        string? Clean(string key) => v.TryGetValue(key, out var s) && !string.IsNullOrWhiteSpace(s) && s.Length <= 128 ? s.Trim() : null;
        brief = new(state.ToUpperInvariant(), Clean("county"), Clean("city"), Clean("status"), minScore, limit, Clean("purpose"));
        return true;
    }
}

/// <summary>Reads the leads back out of a lead brief artifact's data block. Must match the analyst's renderer.</summary>
public static class LeadBriefData
{
    public const string DocumentType = "realtyleads.lead-brief.v1";
    public const string DataHeading = "## Data (realtyleads.lead-brief.v1)";
    public const string MarkerPrefix = "[lead-brief:";
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    public static Guid? ParseMarker(string? text)
    {
        if (text is null) return null;
        var start = text.IndexOf(MarkerPrefix, StringComparison.Ordinal);
        if (start < 0) return null;
        var end = text.IndexOf(']', start);
        return end > start && Guid.TryParseExact(text[(start + MarkerPrefix.Length)..end], "D", out var id) ? id : null;
    }

    public static IReadOnlyList<InheritanceLead>? ParseLeads(string? content)
    {
        if (content is null) return null;
        var start = content.IndexOf(DataHeading, StringComparison.Ordinal);
        if (start < 0) return null;
        var open = content.IndexOf("```json\n", start, StringComparison.Ordinal);
        var close = open < 0 ? -1 : content.IndexOf("\n```", open + 8, StringComparison.Ordinal);
        if (open < 0 || close < 0) return null;
        try
        {
            using var document = JsonDocument.Parse(content[(open + 8)..close]);
            if (!document.RootElement.TryGetProperty("leads", out var leads) || leads.ValueKind != JsonValueKind.Array) return null;
            return leads.Deserialize<InheritanceLead[]>(Web)?.Where(l => Guid.TryParseExact(l.InheritanceLeadId, "D", out _)).ToArray();
        }
        catch (JsonException) { return null; }
    }
}
