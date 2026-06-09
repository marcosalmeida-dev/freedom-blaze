using System.Text.Json;

namespace FreedomBlaze.Extensions;

public static class HttpContentExtensions
{
    private static readonly JsonSerializerOptions Settings = new();

    /// <exception cref="JsonException">If JSON deserialization fails for any reason.</exception>
    /// <exception cref="InvalidOperationException">If the JSON string is <c>"null"</c> (a valid JSON value, but forbidden here).</exception>
    public static async Task<T> ReadAsJsonAsync<T>(this HttpContent content)
    {
        var json = await content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<T>(json, Settings)
            ?? throw new InvalidOperationException("'null' is forbidden.");
    }
}
