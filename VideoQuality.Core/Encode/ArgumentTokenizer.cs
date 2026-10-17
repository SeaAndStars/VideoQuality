using System.Text;

namespace VideoQuality.Core.Encode;

/// <summary>Splits user-typed arguments the way a shell would: whitespace, "double" and 'single' quotes.</summary>
public static class ArgumentTokenizer
{
    public static IReadOnlyList<string> Split(string? text)
    {
        var tokens = new List<string>();
        if (string.IsNullOrWhiteSpace(text))
        {
            return tokens;
        }

        var current = new StringBuilder();
        bool inToken = false;
        char quote = '\0';
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (quote != '\0')
            {
                if (c == quote)
                {
                    quote = '\0';
                }
                else if (c == '\\' && quote == '"' && i + 1 < text.Length && text[i + 1] is '"' or '\\')
                {
                    current.Append(text[++i]);
                }
                else
                {
                    current.Append(c);
                }
            }
            else if (c is '"' or '\'')
            {
                quote = c;
                inToken = true;
            }
            else if (char.IsWhiteSpace(c))
            {
                if (inToken)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                    inToken = false;
                }
            }
            else
            {
                current.Append(c);
                inToken = true;
            }
        }

        if (inToken)
        {
            tokens.Add(current.ToString());
        }

        return tokens;
    }
}
