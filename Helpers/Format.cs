using System.Globalization;

namespace Talky.Helpers;

public static class Format
{
    /// <summary>"Today at 14:32", "Yesterday at 09:10" or "11/09/2026 14:32".</summary>
    public static string MessageTime(DateTimeOffset timestamp)
    {
        var local = timestamp.ToLocalTime();
        var today = DateTime.Today;
        var time = local.ToString("t", CultureInfo.CurrentCulture);
        if (local.Date == today)
        {
            return $"Today at {time}";
        }

        if (local.Date == today.AddDays(-1))
        {
            return $"Yesterday at {time}";
        }

        return $"{local.ToString("d", CultureInfo.CurrentCulture)} {time}";
    }

    /// <summary>Divider text between days, e.g. "Wednesday, November 11, 2026".</summary>
    public static string DateDivider(DateTimeOffset timestamp) =>
        timestamp.ToLocalTime().ToString("D", CultureInfo.CurrentCulture);

    public static string ShortTime(DateTimeOffset timestamp) =>
        timestamp.ToLocalTime().ToString("t", CultureInfo.CurrentCulture);

    public static string Duration(TimeSpan duration) =>
        duration.TotalHours >= 1 ? duration.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture) : duration.ToString(@"m\:ss", CultureInfo.InvariantCulture);

    /// <summary>Up to two initials for avatars.</summary>
    public static string Initials(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "?";
        }

        var parts = name.Split([' ', '.', '_', '-'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2)
        {
            return string.Concat(char.ToUpperInvariant(parts[0][0]), char.ToUpperInvariant(parts[1][0]));
        }

        var word = parts.Length == 1 ? parts[0] : name.Trim();
        var first = StringInfo.GetNextTextElementLength(word);
        return word[..first].ToUpperInvariant();
    }

    public static string Preview(string text, int max = 80)
    {
        var singleLine = text.ReplaceLineEndings(" ");
        return singleLine.Length > max ? singleLine[..max] + "…" : singleLine;
    }
}
