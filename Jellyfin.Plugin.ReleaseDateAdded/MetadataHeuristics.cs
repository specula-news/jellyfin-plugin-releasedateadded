using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.ReleaseDateAdded;

/// <summary>
/// Small helper functions without Jellyfin dependencies.
/// </summary>
public static class MetadataHeuristics
{
    private static readonly Regex SeasonEpisodePattern = new(@"[Ss]\d{1,3}[ ._-]?[Ee]\d{1,4}", RegexOptions.Compiled);
    private static readonly Regex HtmlTag = new("<[^>]+>", RegexOptions.Compiled);

    /// <summary>
    /// True if the title looks like a file name (e.g. "Sister.Wives.S21E06" or "Temptation.Island"),
    /// i.e. Jellyfin did not find a real title.
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

    /// <summary>Normalizes a title for comparison ("The Traitors (UK)" → "traitorsuk").</summary>
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

    /// <summary>Removes HTML tags (TVmaze summaries are HTML).</summary>
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

    /// <summary>True if a title is a placeholder like "Episode 6".</summary>
    public static bool IsPlaceholderTitle(string? name)
        => string.IsNullOrWhiteSpace(name)
           || Regex.IsMatch(name, @"^(episode|avsnitt|folge|épisode|episodio)\s*\d+$", RegexOptions.IgnoreCase);

    /// <summary>Gets a provider ID case-insensitively.</summary>
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
