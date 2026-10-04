using FreedomBlaze.Models.BitcoinTracking;

namespace FreedomBlaze.Interfaces;

public interface IBitcoinTrackingService
{
    Task<BitcoinTrackingResult> LookupAsync(string query, CancellationToken cancellationToken = default);
    Task<BitcoinTransactionPage> GetAddressTransactionsAsync(string address, string lastSeenTxId,
        CancellationToken cancellationToken = default);
}
