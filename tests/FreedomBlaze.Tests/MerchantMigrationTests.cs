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
    public void ExistingMerchantsKeepTheOperatorsNodeAndTheirInvoicesPointAtIt()
    {
        var operations = new AddLightningBackends().UpOperations.ToList();

        var table = Assert.Single(operations.OfType<CreateTableOperation>());
        var sql = operations.OfType<SqlOperation>().ToList();
        var column = Assert.Single(operations.OfType<AddColumnOperation>());
        var required = Assert.Single(operations.OfType<AlterColumnOperation>());
        var foreignKey = Assert.Single(operations.OfType<AddForeignKeyOperation>());

        Assert.Equal(2, sql.Count);
        Assert.Contains("INSERT INTO [LightningBackends]", sql[0].Sql);
        Assert.Contains("SELECT [Id], 0, 1", sql[0].Sql); // OperatorNode, active
        Assert.Contains("UPDATE [i] SET [BackendId]", sql[1].Sql);
        Assert.True(column.IsNullable);
        Assert.False(required.IsNullable);

        // Each step needs the one before it: rows to point at, a column to fill, values before NOT NULL and the key.
        Assert.True(operations.IndexOf(table) < operations.IndexOf(sql[0]));
        Assert.True(operations.IndexOf(sql[0]) < operations.IndexOf(column));
        Assert.True(operations.IndexOf(column) < operations.IndexOf(sql[1]));
        Assert.True(operations.IndexOf(sql[1]) < operations.IndexOf(required));
        Assert.True(operations.IndexOf(required) < operations.IndexOf(foreignKey));
    }

    [Fact]
    public void TheMigrationMatchesTheModelWithoutOpeningADatabase()
    {
        var options = new DbContextOptionsBuilder<FreedomBlazeDbContext>()
            .UseSqlServer("Server=sql.invalid;Database=offline_model_check;Integrated Security=true;").Options;
        using var db = new FreedomBlazeDbContext(options);

        Assert.False(db.Database.HasPendingModelChanges());
        Assert.Contains("20261014004338_AddMerchantsAndInvoices", db.Database.GetMigrations());
        Assert.Contains("20261014022116_AddLightningBackends", db.Database.GetMigrations());
    }
}
