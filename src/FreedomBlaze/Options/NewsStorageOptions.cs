namespace FreedomBlaze.Options;

/// <summary>Where generated daily Bitcoin news is persisted between runs.</summary>
public enum NewsStorageProvider
{
    /// <summary>Persist to JSON files on the local filesystem.</summary>
    LocalFile,

    /// <summary>Persist to Azure Blob Storage (requires the "BlobStorage" section).</summary>
    AzureBlob,

    /// <summary>
    /// Persist to a SQL Server database via EF Core (default). Uses the "FreedomBlazeDb"
    /// connection string and applies code-first migrations on startup.
    /// </summary>
    SqlServer,
}

/// <summary>
/// Configuration for daily news persistence, bound from the "NewsStorage" section.
/// Switch the backend with <see cref="Provider"/> — e.g. the env var
/// <c>NewsStorage__Provider=AzureBlob</c> — with no code changes.
/// </summary>
public class NewsStorageOptions
{
    public const string Section = "NewsStorage";

    /// <summary>The active persistence backend. Defaults to a SQL Server database.</summary>
    public NewsStorageProvider Provider { get; set; } = NewsStorageProvider.SqlServer;

    /// <summary>
    /// Directory for <see cref="NewsStorageProvider.LocalFile"/> storage. Relative paths resolve
    /// against the app's content root. Defaults to "App_Data/bitcoin-news".
    /// </summary>
    public string LocalPath { get; set; } = "App_Data/bitcoin-news";
}
