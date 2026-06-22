using System.Text;
using System.Text.RegularExpressions;

namespace AzureBackup.Core.Scan;

/// <summary>
/// Matches paths against gitignore-style rules (used by the exclude file and the
/// no-compress rules file). Supports: comments (#), blank lines, negation (!),
/// directory-only (trailing /), anchoring (leading or middle /), <c>**</c>, <c>*</c>,
/// <c>?</c>. Last matching rule wins (so <c>!pattern</c> re-includes).
///
/// Paths are evaluated relative to the root, with '/' separators and no leading '/'.
/// Ignored directories are pruned by the scanner, which handles "everything under
/// an excluded directory" (and, as in git, means you cannot re-include under one).
/// </summary>
public sealed class GitignoreMatcher
{
    private sealed record Rule(Regex Regex, bool Negate, bool DirOnly);

    private readonly List<Rule> _rules;

    private GitignoreMatcher(List<Rule> rules) => _rules = rules;

    public static GitignoreMatcher Empty { get; } = new([]);

    public static GitignoreMatcher Parse(IEnumerable<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var rules = new List<Rule>();
        foreach (string raw in lines)
        {
            Rule? rule = ParseLine(raw);
            if (rule is not null) rules.Add(rule);
        }
        return new GitignoreMatcher(rules);
    }

    public bool IsIgnored(string relativePath, bool isDirectory)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        string path = relativePath.Replace('\\', '/').TrimStart('/');

        bool ignored = false;
        foreach (Rule rule in _rules)
        {
            if (rule.DirOnly && !isDirectory) continue;
            if (rule.Regex.IsMatch(path))
                ignored = !rule.Negate;
        }
        return ignored;
    }

    private static Rule? ParseLine(string raw)
    {
        // Trim trailing whitespace (gitignore: trailing spaces are ignored unless escaped — escaping not supported here).
        string line = raw.TrimEnd();
        if (line.Length == 0) return null;
        if (line[0] == '#') return null; // comment

        bool negate = false;
        if (line[0] == '!')
        {
            negate = true;
            line = line[1..];
        }
        else if (line.StartsWith("\\#", StringComparison.Ordinal) || line.StartsWith("\\!", StringComparison.Ordinal))
        {
            line = line[1..]; // escaped leading # or !
        }
        if (line.Length == 0) return null;

        bool dirOnly = line.EndsWith('/');
        if (dirOnly) line = line[..^1];
        if (line.Length == 0) return null;

        bool anchored = line.Contains('/');
        if (line.StartsWith('/')) line = line[1..];

        string body = Translate(line);
        string pattern = anchored ? "^" + body + "$" : "^(?:.*/)?" + body + "$";
        return new Rule(new Regex(pattern, RegexOptions.Compiled | RegexOptions.CultureInvariant), negate, dirOnly);
    }

    /// <summary>Translates a gitignore glob (no leading '!'/'/', no trailing '/') to a regex body.</summary>
    private static string Translate(string p)
    {
        var sb = new StringBuilder();
        int i = 0, n = p.Length;
        while (i < n)
        {
            char c = p[i];
            if (c == '*')
            {
                if (i + 1 < n && p[i + 1] == '*')
                {
                    int j = i;
                    while (j < n && p[j] == '*') j++; // consume consecutive '*'
                    bool boundedStart = i == 0 || p[i - 1] == '/';
                    bool slashAfter = j < n && p[j] == '/';
                    bool atEnd = j == n;
                    if (boundedStart && slashAfter)
                    {
                        sb.Append("(?:.*/)?"); // "**/" => zero or more directories
                        i = j + 1;             // consume the following '/'
                    }
                    else if (boundedStart && atEnd)
                    {
                        sb.Append(".*");       // trailing "/**" or whole "**"
                        i = j;
                    }
                    else
                    {
                        sb.Append(".*");       // unbounded "**" => match across
                        i = j;
                    }
                }
                else
                {
                    sb.Append("[^/]*");
                    i++;
                }
            }
            else if (c == '?')
            {
                sb.Append("[^/]");
                i++;
            }
            else
            {
                sb.Append(Regex.Escape(c.ToString()));
                i++;
            }
        }
        return sb.ToString();
    }
}
