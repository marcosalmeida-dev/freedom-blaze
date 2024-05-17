namespace FreedomBlaze.Extensions;

public static class LinqExtensions
{
    public static bool NotNullAndNotEmpty<T>(this IEnumerable<T> source)
        => source?.Any() is true;
}
