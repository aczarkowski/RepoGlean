using System.Text;
using System.Text.RegularExpressions;

namespace DevCleaner.Rules;

public static class GlobMatcher
{
    public static bool IsMatch(string pattern, string repositoryRelativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern);
        ArgumentNullException.ThrowIfNull(repositoryRelativePath);
        return Regex.IsMatch(Normalize(repositoryRelativePath), ToRegex(Normalize(pattern)), RegexOptions.CultureInvariant);
    }

    private static string Normalize(string value) => value.Replace('\\', '/');

    private static string ToRegex(string pattern)
    {
        var expression = new StringBuilder("\\A");
        for (var index = 0; index < pattern.Length; index++)
        {
            var character = pattern[index];
            if (character == '/' && index + 2 < pattern.Length && pattern[index + 1] == '*' && pattern[index + 2] == '*' && index + 3 == pattern.Length)
            {
                expression.Append("(?:/.*)?");
                index += 2;
            }
            else if (character == '*' && index + 1 < pattern.Length && pattern[index + 1] == '*')
            {
                index++;
                if (index + 1 < pattern.Length && pattern[index + 1] == '/')
                {
                    expression.Append("(?:[^/]+/)*");
                    index++;
                }
                else
                {
                    expression.Append(".*");
                }
            }
            else if (character == '*') expression.Append("[^/]*");
            else if (character == '?') expression.Append("[^/]");
            else expression.Append(Regex.Escape(character.ToString()));
        }

        expression.Append("\\z");
        return expression.ToString();
    }
}
