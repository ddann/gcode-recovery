using System.Globalization;
using System.Text.RegularExpressions;

namespace GcodeRecovery.Core.Recovery;

/// <summary>Replaces <c>{name}</c> placeholders in profile templates.</summary>
public static partial class TemplateEngine
{
    [GeneratedRegex(@"\{([a-z_][a-z0-9_]*)\}", RegexOptions.IgnoreCase)]
    private static partial Regex Placeholder();

    public static string Format(double value)
    {
        var s = Math.Round(value, 3).ToString("0.###", CultureInfo.InvariantCulture);
        return s == "-0" ? "0" : s;
    }

    /// <summary>Renders a template; unknown placeholders are left in place and reported.</summary>
    public static IEnumerable<string> Render(string template, IReadOnlyDictionary<string, string> values, ICollection<string>? unknown = null)
    {
        if (string.IsNullOrWhiteSpace(template)) yield break;
        foreach (var rawLine in template.Replace("\r\n", "\n").Split('\n'))
        {
            var line = Placeholder().Replace(rawLine.TrimEnd(), m =>
            {
                if (values.TryGetValue(m.Groups[1].Value.ToLowerInvariant(), out var v)) return v;
                unknown?.Add(m.Groups[1].Value);
                return m.Value;
            });
            yield return line.TrimStart();
        }
    }
}
