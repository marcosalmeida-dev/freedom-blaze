using FreedomBlaze.Models.BitcoinTracking;

namespace FreedomBlaze.Interfaces;

public interface IBitcoinHistoricalPriceService
{
    Task<BitcoinHistoricalPriceResult> GetDailyPriceAsync(DateOnly utcDate, string currency,
        CancellationToken cancellationToken = default);
}
