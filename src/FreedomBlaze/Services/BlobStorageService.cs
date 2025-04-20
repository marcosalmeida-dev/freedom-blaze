using System.Text;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;

namespace FreedomBlaze.Services;

public class BlobStorageService
{
    private readonly BlobServiceClient _blobServiceClient;

    public BlobStorageService(BlobServiceClient blobServiceClient)
    {
        _blobServiceClient = blobServiceClient;
    }

    private async Task<BlobContainerClient> GetContainerAsync(string containerName)
    {
        var containerClient = _blobServiceClient.GetBlobContainerClient(containerName);
        await containerClient.CreateIfNotExistsAsync(PublicAccessType.None);
        return containerClient;
    }

    public async Task UploadTextAsync(string containerName, string blobName, string content)
    {
        var containerClient = await GetContainerAsync(containerName);
        var blobClient = containerClient.GetBlobClient(blobName);

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));
        await blobClient.UploadAsync(stream, overwrite: true);
    }

    public async Task<string> UploadImageAsync(string containerName, string blobName, byte[] imageBytes)
    {
        var containerClient = await GetContainerAsync(containerName);
        var blobClient = containerClient.GetBlobClient(blobName);

        using var stream = new MemoryStream(imageBytes);
        await blobClient.UploadAsync(stream, overwrite: true);

        // Return the URI of the uploaded blob
        return blobClient.Uri.ToString();
    }

    public async Task<string?> DownloadTextAsync(string containerName, string blobName)
    {
        try
        {
            var containerClient = await GetContainerAsync(containerName);
            var blobClient = containerClient.GetBlobClient(blobName);

            if (await blobClient.ExistsAsync())
            {
                var downloadResult = await blobClient.DownloadContentAsync();
                return downloadResult.Value.Content.ToString();
            }
        }
        catch
        {
            throw;
        }

        return null;
    }

    public async Task DeleteBlobAsync(string containerName, string blobName)
    {
        var containerClient = await GetContainerAsync(containerName);
        var blobClient = containerClient.GetBlobClient(blobName);
        await blobClient.DeleteIfExistsAsync();
    }

    public async Task<bool> BlobExistsAsync(string containerName, string blobName)
    {
        var containerClient = await GetContainerAsync(containerName);
        var blobClient = containerClient.GetBlobClient(blobName);
        return await blobClient.ExistsAsync();
    }
}
