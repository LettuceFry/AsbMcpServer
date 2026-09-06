using System.Globalization;
using System.Text;

namespace MyMcpServer;

internal static class LocalizedNameNormalizer
{
    public static string Normalize(string value)
    {
        var normalized = value.Normalize(NormalizationForm.FormKC).ToLower(CultureInfo.InvariantCulture);
        var result = new StringBuilder(normalized.Length);
        foreach (var character in normalized)
        {
            var normalizedCharacter = character is >= '\u3041' and <= '\u3096'
                or >= '\u309d' and <= '\u309f'
                    ? (char)(character + 0x60)
                    : character;
            if (char.IsLetterOrDigit(normalizedCharacter))
            {
                result.Append(normalizedCharacter);
            }
        }
        return result.ToString();
    }
}
