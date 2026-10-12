using FreedomBlaze.Data;
using Microsoft.EntityFrameworkCore;

namespace FreedomBlaze.Tests;

public sealed class BitcoinNewsMigrationTests
{
    [Fact]
    public void GenerationAttemptMigrationMatchesTheCurrentModelWithoutOpeningADatabase()
    {
        var options = new DbContextOptionsBuilder<FreedomBlazeDbContext>()
            .UseSqlServer("Server=sql.invalid;Database=offline_model_check;Integrated Security=true;").Options;
        using var db = new FreedomBlazeDbContext(options);

        Assert.False(db.Database.HasPendingModelChanges());
        Assert.Contains("20261012000000_AddNewsGenerationAttempts", db.Database.GetMigrations());
    }
}
