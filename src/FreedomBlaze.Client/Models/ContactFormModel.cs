using System.ComponentModel.DataAnnotations;

namespace FreedomBlaze.Models;

/// <summary>
/// Contact form payload shared by the Blazor UI (<c>Contact.razor</c>) and the server endpoint
/// (<c>ContactController</c>).
/// </summary>
public class ContactFormModel
{
    [Required]
    [StringLength(128, MinimumLength = 3, ErrorMessage = "Title length can't be more than 128, and minimum 3.")]
    public string Title { get; set; } = string.Empty;

    [Required]
    [StringLength(2048, MinimumLength = 3, ErrorMessage = "Description length can't be more than 2048, and minimum 3.")]
    public string Description { get; set; } = string.Empty;
}
