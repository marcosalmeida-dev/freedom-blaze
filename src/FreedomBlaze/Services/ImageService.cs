namespace FreedomBlaze.Services;

public class ImageService
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    public ImageService(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    public string GetAbsoluteImageUri(string imageRelativePath)
    {
        var request = _httpContextAccessor.HttpContext?.Request;

        if (request == null)
            throw new InvalidOperationException("No HTTP context available.");

        // Build base URI (e.g., https://yourdomain.com/)
        var baseUri = $"{request.Scheme}://{request.Host.Value}";

        // Combine with image path (e.g., /images/logo.png)
        var absoluteUri = $"{baseUri}/{imageRelativePath.TrimStart('/')}";

        return absoluteUri;
    }
}
