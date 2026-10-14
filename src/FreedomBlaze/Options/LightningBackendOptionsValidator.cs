using FreedomBlaze.Services.Lightning;
using Microsoft.Extensions.Options;

namespace FreedomBlaze.Options;

/// <summary>
/// Refuses to start with a malformed credential key, rather than failing the first time a merchant's node
/// is configured or reached. Having no key at all is fine: only the operator's node can be used then.
/// </summary>
public sealed class LightningBackendOptionsValidator : IValidateOptions<LightningBackendOptions>
{
    public ValidateOptionsResult Validate(string? name, LightningBackendOptions options)
    {
        if (options.RequestTimeoutSeconds <= 0)
            return ValidateOptionsResult.Fail("LightningBackends:RequestTimeoutSeconds must be positive.");

        return AesGcmCredentialProtector.Validate(options) is { } problem
            ? ValidateOptionsResult.Fail(problem)
            : ValidateOptionsResult.Success;
    }
}
