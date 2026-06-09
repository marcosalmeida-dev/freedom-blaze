using System.Net.Http.Json;
using FreedomBlaze.Models;

namespace FreedomBlaze.Client.Services;

public class ContactService(HttpClient httpClient)
{
    public Task<HttpResponseMessage> AddCommentAsync(ContactFormModel model) =>
        httpClient.PostAsJsonAsync("api/contact/submit", model);
}
