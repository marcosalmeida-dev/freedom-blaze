using FreedomBlaze.Data;
using FreedomBlaze.Data.Entities;
using FreedomBlaze.Exceptions;
using FreedomBlaze.Models.Api;
using FreedomBlaze.Options;
using FreedomBlaze.Services.Lightning;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace FreedomBlaze.Services;

/// <summary>Default <see cref="ILightningBackendService"/>.</summary>
public sealed class LightningBackendService(
    IDbContextFactory<FreedomBlazeDbContext> contexts,
    ILightningBackendFactory factory,
    ILightningCredentialProtector credentials,
    IOptions<LightningBackendOptions> options,
    TimeProvider timeProvider,
    ILogger<LightningBackendService> logger) : ILightningBackendService
{
    private const int MaxCheckErrorLength = 512;

    public async Task<LightningBackendInfo?> GetAsync(int merchantId, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var backend = await db.LightningBackends.AsNoTracking()
            .FirstOrDefaultAsync(b => b.MerchantId == merchantId && b.IsActive, cancellationToken);
        return backend is null ? null : LightningBackendInfo.From(backend);
    }

    public async Task<LightningBackendInfo> ConfigureAsync(
        int merchantId, ConfigureLightningBackendRequest request, CancellationToken cancellationToken)
    {
        if (!LightningBackendInfo.TryParseKind(request.Kind, out var kind))
            throw new RequestValidationException("kind must be 'phoenixd' or 'operator'.");

        Uri? url = null;
        ILightningBackend node;
        if (kind == LightningBackendKind.Phoenixd)
        {
            url = ParseNodeUrl(request.Url);
            if (string.IsNullOrWhiteSpace(request.Password))
                throw new RequestValidationException("password is required: phoenixd's http-password-limited-access from phoenix.conf.");
            if (!credentials.CanProtect)
                throw new RequestValidationException(
                    "This server cannot store node passwords yet: configure LightningBackends:CredentialKeys first.");

            node = factory.CreatePhoenixd(url, request.Password);
        }
        else
        {
            if (request.Url is not null || request.Password is not null)
                throw new RequestValidationException("The operator's node takes no url or password.");

            try
            {
                node = factory.Create(new LightningBackend { MerchantId = merchantId, Kind = LightningBackendKind.OperatorNode });
            }
            catch (LightningBackendUnavailableException ex)
            {
                throw new LightningBackendCheckException(ex.Message);
            }
        }

        BackendNodeInfo info;
        try
        {
            info = await node.GetInfoAsync(cancellationToken);
            if (kind == LightningBackendKind.Phoenixd && await node.CanSpendAsync(cancellationToken))
                throw new LightningBackendCheckException(
                    "This password can also spend: it is phoenixd's http-password. Give the gateway " +
                    "http-password-limited-access from phoenix.conf, which can only receive.");
        }
        catch (LightningBackendException ex)
        {
            throw new LightningBackendCheckException(ex.IsAuthenticationFailure ? "The node refused the password." : ex.Message);
        }

        var now = timeProvider.GetUtcNow();
        var backend = new LightningBackend
        {
            MerchantId = merchantId,
            Kind = kind,
            Url = url?.AbsoluteUri,
            ProtectedPassword = kind == LightningBackendKind.Phoenixd ? credentials.Protect(merchantId, request.Password!) : null,
            IsActive = true,
            CreatedAtUtc = now,
            NodeId = info.NodeId,
            Chain = info.Chain,
            CheckedAtUtc = now,
            Reachable = true,
        };

        var retired = await ReplaceAsync(merchantId, backend, now, cancellationToken);
        logger.LogInformation(
            "Merchant {MerchantId} now issues invoices on backend {BackendId}: {Kind} node {NodeId} ({Chain}) {Url}. Retired: {Retired}.",
            merchantId, backend.Id, kind, info.NodeId, info.Chain, backend.Url ?? "(operator)", retired);
        return LightningBackendInfo.From(backend);
    }

    public async Task<LightningBackendInfo?> CheckAsync(int merchantId, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var backend = await db.LightningBackends.FirstOrDefaultAsync(b => b.MerchantId == merchantId && b.IsActive, cancellationToken);
        if (backend is null)
            return null;

        try
        {
            var info = await factory.Create(backend).GetInfoAsync(cancellationToken);
            if (backend.NodeId is not null && !string.Equals(info.NodeId, backend.NodeId, StringComparison.OrdinalIgnoreCase))
            {
                backend.Reachable = false;
                backend.CheckError = Truncate(
                    $"The node now answers as {info.NodeId}, not the pinned {backend.NodeId}. " +
                    "If it was replaced on purpose, configure the backend again.");
            }
            else
            {
                backend.NodeId ??= info.NodeId;
                backend.Chain = info.Chain;
                backend.Reachable = true;
                backend.CheckError = null;
            }
        }
        catch (Exception ex) when (ex is LightningBackendException or LightningBackendUnavailableException)
        {
            backend.Reachable = false;
            backend.CheckError = Truncate(ex.Message);
        }

        backend.CheckedAtUtc = timeProvider.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken);

        if (backend.Reachable != true)
            logger.LogWarning("Backend {BackendId} of merchant {MerchantId} failed its check: {Problem}",
                backend.Id, merchantId, backend.CheckError);
        return LightningBackendInfo.From(backend);
    }

    public async Task<bool> RemoveAsync(int merchantId, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var backend = await db.LightningBackends.FirstOrDefaultAsync(b => b.MerchantId == merchantId && b.IsActive, cancellationToken);
        if (backend is null)
            return false;

        backend.IsActive = false;
        backend.RetiredAtUtc = timeProvider.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Retired backend {BackendId} of merchant {MerchantId}; it can no longer issue invoices.",
            backend.Id, merchantId);
        return true;
    }

    /// <summary>
    /// Retires the merchant's current backend and makes <paramref name="backend"/> current, in one
    /// transaction. Retiring is saved first so the one-active-backend index never sees two.
    /// </summary>
    private async Task<int> ReplaceAsync(int merchantId, LightningBackend backend, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        try
        {
            // The retrying execution strategy only allows a transaction it can replay as a whole.
            return await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
            {
                db.ChangeTracker.Clear();
                backend.Id = 0;
                await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

                var current = await db.LightningBackends
                    .Where(b => b.MerchantId == merchantId && b.IsActive)
                    .ToListAsync(cancellationToken);
                foreach (var old in current)
                {
                    old.IsActive = false;
                    old.RetiredAtUtc = now;
                }
                await db.SaveChangesAsync(cancellationToken);

                db.LightningBackends.Add(backend);
                await db.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return current.Count;
            });
        }
        catch (DbUpdateException)
        {
            throw new RequestValidationException("The merchant's backend was changed at the same time; try again.");
        }
    }

    private Uri ParseNodeUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri))
            throw new RequestValidationException("url must be the node's absolute HTTPS URL.");

        if (uri.Scheme != Uri.UriSchemeHttps && !(uri.Scheme == Uri.UriSchemeHttp && options.Value.AllowInsecureHttp))
            throw new RequestValidationException("url must use HTTPS: the node's password must not cross a network in clear text.");

        if (!string.IsNullOrEmpty(uri.UserInfo))
            throw new RequestValidationException("url must not embed credentials; send the password separately.");

        if (!string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new RequestValidationException("url must not have a query or fragment.");

        var path = uri.GetLeftPart(UriPartial.Path);
        return new Uri(path.EndsWith('/') ? path : path + "/");
    }

    private static string Truncate(string message) =>
        message.Length <= MaxCheckErrorLength ? message : message[..(MaxCheckErrorLength - 1)] + "…";
}
