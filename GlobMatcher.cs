using System.Text;
using System.Text.RegularExpressions;

internal static class GlobMatcher
{
    public static string NormalizeGlob(string glob) => glob.Replace('\\', '/').Trim();

    public static bool IsMatch(string value, string glob)
    {
        var regex = GlobToRegex(glob);
        return Regex.IsMatch(value, regex, RegexOptions.IgnoreCase);
    }

    private static string GlobToRegex(string glob)
    {
        var sb = new StringBuilder();
        sb.Append('^');

        for (var i = 0; i < glob.Length; i++)
        {
            var c = glob[i];
            if (c == '*')
            {
                if (i + 1 < glob.Length && glob[i + 1] == '*')
                {
                    sb.Append(".*");
                    i++;
                }
                else
                {
                    sb.Append("[^/]*");
                }

                continue;
            }

            if (c == '?')
            {
                sb.Append('.');
                continue;
            }

            if (".+()^$|{}[]".Contains(c, StringComparison.Ordinal))
            {
                sb.Append('\\');
            }

            sb.Append(c);
        }

        sb.Append('$');
        return sb.ToString();
    }
}
