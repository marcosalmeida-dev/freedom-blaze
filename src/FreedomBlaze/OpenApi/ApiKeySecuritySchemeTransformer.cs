using FreedomBlaze.Authentication;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace FreedomBlaze.OpenApi;

/// <summary>
/// Registers the <c>ApiKey</c> security scheme in the OpenAPI components section so Scalar can
/// display the auth input. A companion <see cref="ApiKeySecurityOperationTransformer"/> annotates
/// individual operations with the security requirement.
/// </summary>
internal sealed class ApiKeySecuritySchemeTransformer(IAuthenticationSchemeProvider schemeProvider)
    : IOpenApiDocumentTransformer
{
    public async Task TransformAsync(
        OpenApiDocument document,
        OpenApiDocumentTransformerContext context,
        CancellationToken cancellationToken)
    {
        var schemes = await schemeProvider.GetAllSchemesAsync();
        if (!schemes.Any(s => s.Name == ApiKeyDefaults.Scheme))
            return;

        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
        document.Components.SecuritySchemes[ApiKeyDefaults.Scheme] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.ApiKey,
            In = ParameterLocation.Header,
            Name = ApiKeyDefaults.HeaderName,
            Description =
                "API key issued by Freedom Blaze. Pass your `fb_live_*` key in the `X-Api-Key` header. " +
                "Alternatively accepted as `Authorization: Bearer <key>`. " +
                "Master keys additionally unlock the `/api/admin/api-keys` management endpoints.",
        };
    }
}

/// <summary>
/// Annotates each operation that requires authentication with the <c>ApiKey</c> security
/// requirement so Scalar displays the lock icon and pre-populates the auth header in the UI.
/// </summary>
internal sealed class ApiKeySecurityOperationTransformer : IOpenApiOperationTransformer
{
    public Task TransformAsync(
        OpenApiOperation operation,
        OpenApiOperationTransformerContext context,
        CancellationToken cancellationToken)
    {
        var metadata = context.Description.ActionDescriptor.EndpointMetadata;

        var requiresAuth = metadata.OfType<IAuthorizeData>().Any()
            && !metadata.OfType<IAllowAnonymous>().Any();

        if (requiresAuth)
        {
            var schemeRef = new OpenApiSecuritySchemeReference(
                ApiKeyDefaults.Scheme, context.Document, null!);

            operation.Security ??= [];
            operation.Security.Add(new OpenApiSecurityRequirement { [schemeRef] = [] });
        }

        return Task.CompletedTask;
    }
}
