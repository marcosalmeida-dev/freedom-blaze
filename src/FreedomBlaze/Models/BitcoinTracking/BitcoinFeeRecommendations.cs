namespace FreedomBlaze.Models.BitcoinTracking;

/// <summary>
/// Current provider recommendations in satoshis per virtual byte. No priority uses the
/// economy rate; the minimum relay rate is separate. These are estimates, not guarantees.
/// </summary>
public sealed record BitcoinFeeRecommendations
{
    public decimal NoPrioritySatPerVByte { get; init; }
    public decimal LowPrioritySatPerVByte { get; init; }
    public decimal MediumPrioritySatPerVByte { get; init; }
    public decimal HighPrioritySatPerVByte { get; init; }
    public decimal MinimumSatPerVByte { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}
