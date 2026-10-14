namespace FreedomBlaze.Data.Entities;

/// <summary>
/// What an API key may do. Master keys hold every scope (they administer the gateway); an ordinary key
/// holds only the scopes it was issued with.
/// </summary>
[Flags]
public enum ApiKeyScopes
{
    None = 0,

    /// <summary>Fiat ↔ BTC/SATS conversions (<c>/api/v1/convert</c>, <c>/api/v1/currencies</c>).</summary>
    Convert = 1,

    /// <summary>Issue Lightning invoices for the key's merchant.</summary>
    InvoicesWrite = 2,

    /// <summary>Read and list the key's merchant's invoices.</summary>
    InvoicesRead = 4,

    All = Convert | InvoicesWrite | InvoicesRead,
}

/// <summary>The names of <see cref="ApiKeyScopes"/> as API requests, responses and claims spell them.</summary>
public static class ApiKeyScopeNames
{
    public const string Convert = "convert";
    public const string InvoicesWrite = "invoices:write";
    public const string InvoicesRead = "invoices:read";

    private static readonly (ApiKeyScopes Scope, string Name)[] Names =
    [
        (ApiKeyScopes.Convert, Convert),
        (ApiKeyScopes.InvoicesWrite, InvoicesWrite),
        (ApiKeyScopes.InvoicesRead, InvoicesRead),
    ];

    /// <summary>Every name the API accepts.</summary>
    public static IReadOnlyList<string> All { get; } = Names.Select(entry => entry.Name).ToArray();

    public static IReadOnlyList<string> ToNames(ApiKeyScopes scopes) =>
        Names.Where(entry => (scopes & entry.Scope) == entry.Scope).Select(entry => entry.Name).ToArray();

    /// <summary>Parses scope names, case-insensitively; <paramref name="unknown"/> is the first name that is not one.</summary>
    public static bool TryParse(IEnumerable<string> names, out ApiKeyScopes scopes, out string? unknown)
    {
        scopes = ApiKeyScopes.None;
        unknown = null;
        foreach (var name in names)
        {
            var match = Names.FirstOrDefault(entry =>
                string.Equals(entry.Name, name?.Trim(), StringComparison.OrdinalIgnoreCase));
            if (match.Name is null)
            {
                unknown = name;
                return false;
            }

            scopes |= match.Scope;
        }

        return true;
    }
}
