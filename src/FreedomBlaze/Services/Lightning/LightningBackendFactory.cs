using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using FreedomBlaze.Data.Entities;
using Phoenixd.NET.Models;

namespace FreedomBlaze.Services.Lightning;

/// <summary>Builds the client for the node a backend points at.</summary>
public interface ILightningBackendFactory
{
    /// <exception cref="LightningBackendUnavailableException">The backend cannot be used on this server.</exception>
    ILightningBackend Create(LightningBackend backend);

    /// <summary>A merchant's phoenixd from settings that are not stored yet, to check them before saving.</summary>
    ILightningBackend CreatePhoenixd(Uri url, string password);
}

/// <summary>
/// Default <see cref="ILightningBackendFactory"/>. Every node is reached through the
/// <see cref="HttpClientName"/> client, which follows no redirects, so a password is only ever sent to
/// the address it was configured for.
/// </summary>
public sealed class LightningBackendFactory(
    IHttpClientFactory httpClients,
    ILightningCredentialProtector credentials,
    PhoenixConfig? operatorNode = null) : ILightningBackendFactory
{
    public const string HttpClientName = "LightningBackend";

    public ILightningBackend Create(LightningBackend backend) => backend.Kind switch
    {
        LightningBackendKind.OperatorNode => OperatorNode(),
        LightningBackendKind.Phoenixd => MerchantPhoenixd(backend),
        _ => throw new LightningBackendUnavailableException($"Backend kind {backend.Kind} is not supported."),
    };

    public ILightningBackend CreatePhoenixd(Uri url, string password) =>
        new PhoenixdBackend(Client(url, "phoenix", password));

    private PhoenixdBackend OperatorNode()
    {
        if (operatorNode is null)
            throw new LightningBackendUnavailableException("The gateway's own node is not configured on this server (PhoenixConfig:Host).");

        // Invoicing never spends, so the limited-access password is preferred when one is configured.
        var password = string.IsNullOrEmpty(operatorNode.LimitedAccessToken) ? operatorNode.Token : operatorNode.LimitedAccessToken;
        return new PhoenixdBackend(Client(new Uri(operatorNode.Host), operatorNode.Username, password));
    }

    private ILightningBackend MerchantPhoenixd(LightningBackend backend)
    {
        if (backend.Url is null || backend.ProtectedPassword is null)
            throw new LightningBackendUnavailableException("The merchant's node has no URL or password stored.");

        string password;
        try
        {
            password = credentials.Unprotect(backend.MerchantId, backend.ProtectedPassword);
        }
        catch (CryptographicException ex)
        {
            throw new LightningBackendUnavailableException(
                "The merchant's node password cannot be decrypted on this server (LightningBackends:CredentialKeys is missing " +
                "the key it was encrypted with). Configure the backend again.", ex);
        }

        return CreatePhoenixd(new Uri(backend.Url), password);
    }

    private HttpClient Client(Uri baseAddress, string username, string password)
    {
        var client = httpClients.CreateClient(HttpClientName);
        client.BaseAddress = baseAddress.AbsoluteUri.EndsWith('/') ? baseAddress : new Uri(baseAddress.AbsoluteUri + "/");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}")));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return client;
    }
}
