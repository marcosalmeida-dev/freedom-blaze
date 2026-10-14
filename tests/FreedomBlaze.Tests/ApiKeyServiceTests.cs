using FreedomBlaze.Data.Entities;
using FreedomBlaze.Exceptions;
using FreedomBlaze.Models.Api;
using FreedomBlaze.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace FreedomBlaze.Tests;

public sealed class ApiKeyServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 15, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task AKeyWithoutAMerchantCanOnlyConvertByDefault()
    {
        using var db = new TestDatabase();

        var created = await Service(db).CreateAsync(new CreateApiKeyRequest { Name = "price widget" }, default);

        Assert.Null(created.MerchantId);
        Assert.Equal(new[] { "convert" }, created.Scopes);
        Assert.Equal(ApiKeyScopes.Convert, Stored(db, created.Id).Scopes);
    }

    [Fact]
    public async Task AMerchantKeyGetsEveryScopeByDefault()
    {
        using var db = new TestDatabase();
        var merchant = db.AddMerchant();

        var created = await Service(db).CreateAsync(new CreateApiKeyRequest { Name = "Artcana checkout", MerchantId = merchant.Id }, default);

        Assert.Equal(merchant.Id, created.MerchantId);
        Assert.Equal(new[] { "convert", "invoices:write", "invoices:read" }, created.Scopes);
        Assert.Equal(merchant.Id, Stored(db, created.Id).MerchantId);
    }

    [Fact]
    public async Task RequestedScopesAreHonoured()
    {
        using var db = new TestDatabase();
        var merchant = db.AddMerchant();

        var created = await Service(db).CreateAsync(new CreateApiKeyRequest
        {
            Name = "Artcana reports", MerchantId = merchant.Id, Scopes = ["invoices:read"]
        }, default);

        Assert.Equal(new[] { "invoices:read" }, created.Scopes);
        Assert.Equal(ApiKeyScopes.InvoicesRead, Stored(db, created.Id).Scopes);
    }

    [Fact]
    public async Task AMasterKeyHoldsEveryScopeAndNoMerchant()
    {
        using var db = new TestDatabase();

        var created = await Service(db).CreateAsync(new CreateApiKeyRequest { Name = "operator", IsMaster = true }, default);

        Assert.Equal(new[] { "convert", "invoices:write", "invoices:read" }, created.Scopes);
        Assert.Equal(ApiKeyScopes.All, Stored(db, created.Id).Scopes);
        Assert.Null(created.MerchantId);
    }

    [Theory]
    [InlineData("invoices:write")]
    [InlineData("invoices:read")]
    public async Task InvoiceScopesNeedAMerchant(string scope)
    {
        using var db = new TestDatabase();

        var refused = await Assert.ThrowsAsync<RequestValidationException>(() =>
            Service(db).CreateAsync(new CreateApiKeyRequest { Name = "no merchant", Scopes = ["convert", scope] }, default));

        Assert.Contains("merchantId", refused.Message);
        Assert.Empty(Keys(db));
    }

    [Fact]
    public async Task UnknownScopesAreRefused()
    {
        using var db = new TestDatabase();

        var refused = await Assert.ThrowsAsync<RequestValidationException>(() =>
            Service(db).CreateAsync(new CreateApiKeyRequest { Name = "greedy", Scopes = ["convert", "payments:send"] }, default));

        Assert.Contains("'payments:send'", refused.Message);
        Assert.Empty(Keys(db));
    }

    [Fact]
    public async Task AKeyNeedsAScope()
    {
        using var db = new TestDatabase();

        await Assert.ThrowsAsync<RequestValidationException>(() =>
            Service(db).CreateAsync(new CreateApiKeyRequest { Name = "nothing", Scopes = [] }, default));

        Assert.Empty(Keys(db));
    }

    [Fact]
    public async Task AMasterKeyCannotActForAMerchant()
    {
        using var db = new TestDatabase();
        var merchant = db.AddMerchant();

        await Assert.ThrowsAsync<RequestValidationException>(() =>
            Service(db).CreateAsync(new CreateApiKeyRequest { Name = "operator", IsMaster = true, MerchantId = merchant.Id }, default));

        Assert.Empty(Keys(db));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task KeysAreOnlyIssuedForAnActiveMerchant(bool merchantExists)
    {
        using var db = new TestDatabase();
        var merchantId = merchantExists ? db.AddMerchant(isActive: false).Id : 42;

        var refused = await Assert.ThrowsAsync<RequestValidationException>(() =>
            Service(db).CreateAsync(new CreateApiKeyRequest { Name = "Artcana checkout", MerchantId = merchantId }, default));

        Assert.Contains($"Merchant {merchantId}", refused.Message);
        Assert.Empty(Keys(db));
    }

    [Fact]
    public async Task DeactivatingAMerchantStopsItsKeysUntilItIsReactivated()
    {
        using var db = new TestDatabase();
        var merchant = db.AddMerchant();
        var keys = Service(db);
        var merchants = new MerchantService(db.Repository<Merchant>(), new ManualClock(Now), NullLogger<MerchantService>.Instance);
        var issued = await keys.CreateAsync(new CreateApiKeyRequest { Name = "Artcana checkout", MerchantId = merchant.Id }, default);
        var unrelated = await keys.CreateAsync(new CreateApiKeyRequest { Name = "price widget" }, default);

        Assert.NotNull(await keys.ValidateAsync(issued.Key, default));

        await merchants.UpdateAsync(merchant.Id, new UpdateMerchantRequest { IsActive = false }, default);

        Assert.Null(await keys.ValidateAsync(issued.Key, default));
        Assert.NotNull(await keys.ValidateAsync(unrelated.Key, default));

        await merchants.UpdateAsync(merchant.Id, new UpdateMerchantRequest { IsActive = true }, default);

        Assert.NotNull(await keys.ValidateAsync(issued.Key, default));
    }

    [Fact]
    public async Task TheListingShowsEachKeysMerchantAndScopes()
    {
        using var db = new TestDatabase();
        var merchant = db.AddMerchant();
        var service = Service(db);
        var master = await service.CreateAsync(new CreateApiKeyRequest { Name = "operator", IsMaster = true }, default);
        var checkout = await service.CreateAsync(new CreateApiKeyRequest { Name = "Artcana checkout", MerchantId = merchant.Id }, default);

        var listed = (await service.ListAsync(default)).ToDictionary(k => k.Id);

        Assert.Equal(new[] { "convert", "invoices:write", "invoices:read" }, listed[master.Id].Scopes);
        Assert.Null(listed[master.Id].MerchantId);
        Assert.Equal(merchant.Id, listed[checkout.Id].MerchantId);
        Assert.Equal(new[] { "convert", "invoices:write", "invoices:read" }, listed[checkout.Id].Scopes);
    }

    private static ApiKeyService Service(TestDatabase db) => new(
        db.Repository<ApiKey>(), db.Repository<Merchant>(), new ManualClock(Now), NullLogger<ApiKeyService>.Instance);

    private static ApiKey Stored(TestDatabase db, int id)
    {
        using var context = db.CreateDbContext();
        return context.ApiKeys.Single(k => k.Id == id);
    }

    private static List<ApiKey> Keys(TestDatabase db)
    {
        using var context = db.CreateDbContext();
        return context.ApiKeys.ToList();
    }
}
