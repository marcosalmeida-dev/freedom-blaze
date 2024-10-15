using System.ComponentModel.DataAnnotations;
using System.Net.Http.Json;

namespace FreedomBlaze.Client.Services;

public class ContactService
{
    private readonly HttpClient _httpClient;

    public ContactService(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<HttpResponseMessage> AddCommentAsync(ContactForm model)
    {
        return await _httpClient.PostAsJsonAsync("api/contact/submit", model);
    }
}

public class ContactForm
{
    [Required]
    [StringLength(128, MinimumLength = 3, ErrorMessage = "Title length can't be more than 128, and minimum 3.")]
    public string Title { get; set; }

    [Required]
    [StringLength(2048, MinimumLength = 3, ErrorMessage = "Title length can't be more than 2048, and minimum 3.")]
    public string Description { get; set; }
}
