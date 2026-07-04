namespace SessionMeter.Core.Util;

/// <summary>
/// Derives a stable, filesystem-safe label from a project working directory (ported from MO's
/// <c>WorkerNaming</c>). Used as the display name for a context reading when the caller does not supply one.
/// The name contains only lowercase ASCII letters/digits.
/// </summary>
public static class Naming
{
    /// <summary>
    /// Turns a path or arbitrary label into a slug: the last path segment, lower-cased, with every
    /// non-alphanumeric character dropped (e.g. <c>C:\Dev\PAV\PAVBrain</c> ⇒ <c>pavbrain</c>,
    /// <c>"My Project"</c> ⇒ <c>myproject</c>). Falls back to <c>worker</c> when nothing usable remains.
    /// </summary>
    /// <param name="pathOrLabel">A directory path or label to slug.</param>
    public static string Slug(string? pathOrLabel)
    {
        if (string.IsNullOrWhiteSpace(pathOrLabel)) return "worker";

        // Take the last segment of a path-like input (handles both separators, trailing slashes).
        string trimmed = pathOrLabel.Trim().TrimEnd('\\', '/', ' ');
        int cut = trimmed.LastIndexOfAny(new[] { '\\', '/' });
        string leaf = cut >= 0 ? trimmed[(cut + 1)..] : trimmed;

        var sb = new System.Text.StringBuilder(leaf.Length);
        foreach (char c in leaf)
            if (char.IsAsciiLetterOrDigit(c))
                sb.Append(char.ToLowerInvariant(c));

        return sb.Length == 0 ? "worker" : sb.ToString();
    }
}
