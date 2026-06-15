using FreedomBlaze.Data.Entities;
using FreedomBlaze.Models;

namespace FreedomBlaze.Data.Mappings;

/// <summary>
/// Maps the donation persistence entity (<see cref="Donation"/>) to its read DTO
/// (<see cref="DonationDto"/>). Keeping the two separate lets the storage schema and the transport
/// contract evolve independently.
/// </summary>
internal static class DonationMapping
{
    public static DonationDto ToDto(this Donation entity) => new()
    {
        Id = entity.Id,
        PaymentHash = entity.PaymentHash,
        AmountSat = entity.AmountSat,
        ReceivedSat = entity.ReceivedSat,
        Message = entity.Message,
        Description = entity.Description,
        FiatEstimate = entity.FiatEstimate,
        Status = entity.Status.ToString(),
        ErrorMessage = entity.ErrorMessage,
        CreatedAtUtc = entity.CreatedAtUtc,
        CompletedAtUtc = entity.CompletedAtUtc,
    };
}
