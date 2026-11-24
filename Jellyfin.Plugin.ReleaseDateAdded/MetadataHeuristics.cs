using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.ReleaseDateAdded;

/// <summary>
/// Små hjälpfunktioner utan Jellyfin-beroenden.
/// </summary>
public static class MetadataHeuristics
{
    private static readonly Regex SeasonEpisodePattern = new(@"[Ss]\d{1,3}[ ._-]?[Ee]\d{1,4}", RegexOptions.Compiled);
    private static readonly Regex HtmlTag = new("<[^>]+>", RegexOptions.Compiled);

    /// <summary>
    /// Sant om titeln ser ut som ett filnamn (t.ex. "Sister.Wives.S21E06" eller "Temptation.Island"),
    /// dvs. Jellyfin hittade ingen riktig titel.
    /// </summary>
    public static bool LooksLikeFileName(string? name, string? path)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return true;
        }

        if (SeasonEpisodePattern.IsMatch(name))
        {
            return true;
        }

        if (!name.Contains(' ', StringComparison.Ordinal) && (name.Contains('.', StringComparison.Ordinal) || name.Contains('_', StringComparison.Ordinal)))
        {
            return true;
        }

        if (!string.IsNullOrEmpty(path))
        {
            var fileName = Path.GetFileNameWithoutExtension(path);
            if (string.Equals(fileName, name, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Normaliserar en titel för jämförelse ("The Traitors (UK)" → "traitorsuk").</summary>
    public static string NormalizeTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return string.Empty;
        }

        var t = title.Trim().ToLowerInvariant().Replace("&", "and", StringComparison.Ordinal);
        if (t.StartsWith("the ", StringComparison.Ordinal))
        {
            t = t[4..];
        }

        var sb = new StringBuilder(t.Length);
        foreach (var c in t.Normalize(NormalizationForm.FormD))
        {
            if (char.IsLetterOrDigit(c))
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }

    /// <summary>Tar bort HTML-taggar (TVmaze-sammanfattningar är HTML).</summary>
    public static string? StripHtml(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return null;
        }

        var text = WebUtility.HtmlDecode(HtmlTag.Replace(html, " "));
        text = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    /// <summary>Sant om en titel är en platshållare som "Episode 6" / "Avsnitt 6".</summary>
    public static bool IsPlaceholderTitle(string? name)
        => string.IsNullOrWhiteSpace(name)
           || Regex.IsMatch(name, @"^(episode|avsnitt|folge|épisode|episodio)\s*\d+$", RegexOptions.IgnoreCase);

    /// <summary>Hämtar ett provider-ID oberoende av skiftläge.</summary>
    public static string? GetProviderId(System.Collections.Generic.IDictionary<string, string>? ids, string name)
    {
        if (ids is null)
        {
            return null;
        }

        foreach (var kv in ids.Where(kv => string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase)))
        {
            return string.IsNullOrWhiteSpace(kv.Value) ? null : kv.Value.Trim();
        }

        return null;
    }
}
