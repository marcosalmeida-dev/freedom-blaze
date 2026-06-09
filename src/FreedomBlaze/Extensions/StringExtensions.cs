namespace FreedomBlaze.Extensions;

public static class StringExtensions
{
    /// <summary>Returns the input with any fractional part removed (e.g. <c>"123.45"</c> → <c>"123"</c>).</summary>
    public static string ToRemoveDecimalCase(this string value) => value.Split('.')[0].Trim();
}
