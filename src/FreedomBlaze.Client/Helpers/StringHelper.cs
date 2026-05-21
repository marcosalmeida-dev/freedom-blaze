namespace FreedomBlaze.Client.Helpers;

public static class StringHelper
{
    public static string Truncate(this string text, int maxLength, string suffix = "...")
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxLength)
            return text ?? string.Empty;
        return text[..maxLength] + suffix;
    }
}
