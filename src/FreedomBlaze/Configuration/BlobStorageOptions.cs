namespace FreedomBlaze.Configuration;

public class BlobStorageOptions
{
    public string AccountName { get; set; } = default!;
    public string? AccessKey { get; set; } // optional, used when not using DefaultAzureCredential
}
