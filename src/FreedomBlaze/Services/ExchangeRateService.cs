using FreedomBlaze.Interfaces;
using FreedomBlaze.Models;
using Microsoft.Extensions.Caching.Memory;

namespace FreedomBlaze.Services;

/// <summary>
/// Aggregates usable BTC/USD sources and fiat rates into one cached snapshot, including source health.
/// The singleton's refresh gate prevents simultaneous page and API requests from flooding providers.
/// </summary>
public sealed class ExchangeRateService(
    IMemoryCache cache,
    ICurrencyExchangeRateClient currencyExchangeClient,
    IEnumerable<IBitcoinExchangeRateClient> bitcoinExchangeClients) : IExchangeRateService
{
    private const string CacheKey = "ExchangeRateService.Snapshot";
    private readonly SemaphoreSlim _refreshGate = new(1, 1);

    public List<BitcoinExchangeStatusModel> BitcoinExchangeStatusList { get; private set; } = [];

    public async Task<BitcoinExchangeRateModel?> GetExchangeRateAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (cache.TryGetValue<RateSnapshot>(CacheKey, out var snapshot))
            return ReadSnapshot(snapshot!);

        await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Another caller may have refreshed the shared cache while this one waited.
            if (cache.TryGetValue<RateSnapshot>(CacheKey, out snapshot))
                return ReadSnapshot(snapshot!);

            var exchangesTask = Task.WhenAll(bitcoinExchangeClients.Select(
                client => ReadExchangeAsync(client, cancellationToken)));
            var currenciesTask = ReadCurrenciesAsync(cancellationToken);
            await Task.WhenAll(exchangesTask, currenciesTask).ConfigureAwait(false);
            var results = await exchangesTask.ConfigureAwait(false);
            var currencies = await currenciesTask.ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            var successfulRates = results.Where(result => result.Rate > 0).ToArray();
            BitcoinExchangeRateModel? rate = null;
            if (successfulRates.Length > 0 && currencies is not null)
            {
                rate = new BitcoinExchangeRateModel
                {
                    BitcoinRateInUSD = successfulRates.Average(result => result.Rate),
                    CurrencyExchangeRate = currencies
                };
            }

            cancellationToken.ThrowIfCancellationRequested();
            snapshot = new RateSnapshot(rate, results.Select(result => new BitcoinExchangeStatusModel
            {
                ExchangeName = result.ExchangeName,
                IsExchangeAvailable = result.Rate > 0
            }).ToList());

            // Briefly cache failures too, so a provider outage doesn't trigger a request storm.
            cache.Set(CacheKey, snapshot, rate is null ? TimeSpan.FromSeconds(10) : TimeSpan.FromSeconds(55));
            return ReadSnapshot(snapshot);
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private BitcoinExchangeRateModel? ReadSnapshot(RateSnapshot snapshot)
    {
        BitcoinExchangeStatusList = snapshot.Statuses;
        return snapshot.Rate;
    }

    private static async Task<ExchangeResult> ReadExchangeAsync(
        IBitcoinExchangeRateClient client, CancellationToken cancellationToken)
    {
        try
        {
            var rate = await client.GetExchangeRateAsync(cancellationToken).ConfigureAwait(false);
            return new ExchangeResult(client.ExchangeName, rate?.BitcoinRateInUSD ?? 0);
        }
        catch (Exception)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new ExchangeResult(client.ExchangeName, 0);
        }
    }

    private async Task<CurrencyExchangeRateModel?> ReadCurrenciesAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await currencyExchangeClient.GetCurrencyRateAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return null;
        }
    }

    private sealed record RateSnapshot(BitcoinExchangeRateModel? Rate, List<BitcoinExchangeStatusModel> Statuses);
    private sealed record ExchangeResult(string ExchangeName, decimal Rate);
}
