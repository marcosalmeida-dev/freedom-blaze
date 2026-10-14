using FreedomBlaze.Data.Entities;
using FreedomBlaze.Exceptions;
using FreedomBlaze.Models.Api;
using FreedomBlaze.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace FreedomBlaze.Tests;

public sealed class MerchantServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 15, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task RegistersAndListsMerchants()
    {
        using var db = new TestDatabase();
        var service = Service(db, new ManualClock(Now));

        var artcana = await service.CreateAsync(new CreateMerchantRequest { Name = "  Artcana  " }, default);
        var other = await service.CreateAsync(new CreateMerchantRequest { Name = "Side project" }, default);

        Assert.Equal("Artcana", artcana.Name);
        Assert.True(artcana.IsActive);
        Assert.Equal(Now, artcana.CreatedAtUtc);
        Assert.Null(artcana.DeactivatedAtUtc);
        Assert.Equal(new[] { artcana.Id, other.Id }, (await service.ListAsync(default)).Select(m => m.Id).ToArray());
        Assert.Equal("Side project", (await service.GetAsync(other.Id, default))!.Name);
    }

    [Fact]
    public async Task DeactivationIsStampedAndReactivationClearsIt()
    {
        using var db = new TestDatabase();
        var clock = new ManualClock(Now);
        var service = Service(db, clock);
        var merchant = await service.CreateAsync(new CreateMerchantRequest { Name = "Artcana" }, default);
        clock.Now = Now.AddDays(1);

        var deactivated = await service.UpdateAsync(merchant.Id, new UpdateMerchantRequest { IsActive = false }, default);
        var renamed = await service.UpdateAsync(merchant.Id, new UpdateMerchantRequest { Name = "Artcana Café" }, default);
        var reactivated = await service.UpdateAsync(merchant.Id, new UpdateMerchantRequest { IsActive = true }, default);

        Assert.False(deactivated!.IsActive);
        Assert.Equal(Now.AddDays(1), deactivated.DeactivatedAtUtc);
        Assert.False(renamed!.IsActive); // a rename leaves the state alone
        Assert.Equal("Artcana Café", renamed.Name);
        Assert.True(reactivated!.IsActive);
        Assert.Null(reactivated.DeactivatedAtUtc);
        Assert.Equal("Artcana Café", reactivated.Name);
    }

    [Fact]
    public async Task ABlankNameIsRefused()
    {
        using var db = new TestDatabase();
        var service = Service(db, new ManualClock(Now));
        var merchant = await service.CreateAsync(new CreateMerchantRequest { Name = "Artcana" }, default);

        await Assert.ThrowsAsync<RequestValidationException>(() =>
            service.CreateAsync(new CreateMerchantRequest { Name = "   " }, default));
        await Assert.ThrowsAsync<RequestValidationException>(() =>
            service.UpdateAsync(merchant.Id, new UpdateMerchantRequest { Name = " " }, default));

        Assert.Equal("Artcana", Assert.Single(await service.ListAsync(default)).Name);
    }

    [Fact]
    public async Task UnknownMerchantsAreNotFound()
    {
        using var db = new TestDatabase();
        var service = Service(db, new ManualClock(Now));

        Assert.Null(await service.GetAsync(42, default));
        Assert.Null(await service.UpdateAsync(42, new UpdateMerchantRequest { IsActive = false }, default));
    }

    private static MerchantService Service(TestDatabase db, TimeProvider clock) =>
        new(db.Repository<Merchant>(), clock, NullLogger<MerchantService>.Instance);
}
