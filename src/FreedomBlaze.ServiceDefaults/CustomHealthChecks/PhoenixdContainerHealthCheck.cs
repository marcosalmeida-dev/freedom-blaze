using Microsoft.Extensions.Diagnostics.HealthChecks;
using Phoenixd.NET.Interfaces;
using Phoenixd.NET.Services;
using System.Threading;
using System.Threading.Tasks;

namespace FreedomBlaze.ServiceDefaults.CustomHealthChecks
{
    public class PhoenixdContainerHealthCheck : IHealthCheck
    {
        private readonly INodeService _nodeService;

        public PhoenixdContainerHealthCheck(INodeService nodeService)
        {
            _nodeService = nodeService;
        }

        public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
        {
            try
            {
                // Call GetNodeInfo to check if the service is running
                var nodeInfo = await _nodeService.GetNodeInfo();

                if (nodeInfo != null)
                {
                    return HealthCheckResult.Healthy("Phoenixd service is running.");
                }
                else
                {
                    return HealthCheckResult.Unhealthy("Phoenixd service returned null node info.");
                }
            }
            catch (Exception ex)
            {
                // Log the exception and return an unhealthy status
                return HealthCheckResult.Unhealthy($"Phoenixd service is not reachable. Exception: {ex.Message}");
            }
        }
    }
}
