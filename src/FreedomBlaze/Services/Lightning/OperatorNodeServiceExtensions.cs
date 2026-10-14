using Phoenixd.NET;
using Phoenixd.NET.Interfaces;

namespace FreedomBlaze.Services.Lightning;

public static class OperatorNodeServiceExtensions
{
    /// <summary>
    /// Registers the phoenixd.NET clients for the operator's own node, without automatic retries. The
    /// service defaults give every HTTP client a retrying resilience handler, and phoenixd's payments are not
    /// idempotent: a retried <c>payinvoice</c>, <c>payoffer</c>, <c>paylnaddress</c> or <c>sendtoaddress</c>
    /// whose first attempt timed out but went through would pay twice.
    /// </summary>
    public static IServiceCollection AddOperatorNode(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSignalR();
        services.ConfigurePhoenixdServices(configuration);

        // phoenixd.NET registers typed clients, which are named after their interface.
#pragma warning disable EXTEXP0001 // RemoveAllResilienceHandlers is experimental; it is the only way to opt out of the defaults.
        services.AddHttpClient(nameof(IPaymentService)).RemoveAllResilienceHandlers();
        services.AddHttpClient(nameof(INodeService)).RemoveAllResilienceHandlers();
#pragma warning restore EXTEXP0001

        return services;
    }
}
