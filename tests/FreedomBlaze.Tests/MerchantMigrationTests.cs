using FreedomBlaze.Data;
using FreedomBlaze.Data.Entities;
using FreedomBlaze.Data.Migrations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace FreedomBlaze.Tests;

public sealed class MerchantMigrationTests
{
    [Fact]
    public void ExistingKeysKeepConvertAndMasterKeysAreMarkedWithEveryScope()
    {
        var operations = new AddMerchantsAndInvoices().UpOperations.ToList();

        var scopes = Assert.Single(operations.OfType<AddColumnOperation>(), c => c is { Table: "ApiKeys", Name: "Scopes" });
        var masters = Assert.Single(operations.OfType<SqlOperation>());

        Assert.Equal((int)ApiKeyScopes.Convert, scopes.DefaultValue);
        Assert.Equal($"UPDATE [ApiKeys] SET [Scopes] = {(int)ApiKeyScopes.All} WHERE [IsMaster] = 1;", masters.Sql);
        Assert.True(operations.IndexOf(scopes) < operations.IndexOf(masters));
    }

    [Fact]
    public void TheMigrationMatchesTheModelWithoutOpeningADatabase()
    {
        var options = new DbContextOptionsBuilder<FreedomBlazeDbContext>()
            .UseSqlServer("Server=sql.invalid;Database=offline_model_check;Integrated Security=true;").Options;
        using var db = new FreedomBlazeDbContext(options);

        Assert.False(db.Database.HasPendingModelChanges());
        Assert.Contains("20261014004338_AddMerchantsAndInvoices", db.Database.GetMigrations());
    }
}
