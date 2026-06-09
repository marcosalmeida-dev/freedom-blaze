using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace FreedomBlaze.Data;

/// <summary>
/// Design-time factory used by the EF Core tools (e.g. <c>dotnet ef migrations add</c> and
/// <c>dotnet ef database update</c>). The application's runtime startup has dependencies that are
/// awkward at design time (required <c>BaseUrl</c>, external clients, etc.), so the tools build the
/// context from here instead — reading the <c>FreedomBlazeDb</c> connection string from
/// configuration, with a LocalDB fallback so a clean checkout can scaffold migrations out of the box.
/// </summary>
public class FreedomBlazeDbContextFactory : IDesignTimeDbContextFactory<FreedomBlazeDbContext>
{
    public FreedomBlazeDbContext CreateDbContext(string[] args)
    {
        var environment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Development";

        var configuration = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: false)
            .AddJsonFile($"appsettings.{environment}.json", optional: true)
            .AddEnvironmentVariables()
            .Build();

        var connectionString = configuration.GetConnectionString("FreedomBlazeDb")
            ?? "Server=(localdb)\\MSSQLLocalDB;Database=FreedomBlazeDb;Trusted_Connection=True;Encrypt=False";

        var options = new DbContextOptionsBuilder<FreedomBlazeDbContext>()
            .UseSqlServer(connectionString, sql =>
                sql.MigrationsAssembly(typeof(FreedomBlazeDbContext).Assembly.FullName))
            .Options;

        return new FreedomBlazeDbContext(options);
    }
}
