using FreedomBlaze.Interfaces;
using FreedomBlaze.Models;

namespace FreedomBlaze;

public class AppState : IDisposable
{
    private readonly Timer _timer;
    private readonly CancellationTokenSource _cancellationTokenSource = new();
    private readonly IExchangeRateProvider _exchangeRateProvider;
    private readonly ILogger<AppState> _logger;

    public AppState(IExchangeRateProvider exchangeRateProvider, ILogger<AppState> logger)
    {
        _exchangeRateProvider = exchangeRateProvider;
        _logger = logger;

        // Initialize the timer to call UpdateExchangeRate every minute
        _timer = new Timer(async _ => await UpdateExchangeRateAsync(), null, TimeSpan.Zero, TimeSpan.FromMinutes(1));
    }

    BitcoinExchangeRateModel _bitcoinExchangeRate;
    public BitcoinExchangeRateModel BitcoinExchangeRate
    {
        get => _bitcoinExchangeRate;
        set
        {
            if (_bitcoinExchangeRate != value)
            {
                _bitcoinExchangeRate = value;
                NotifyStateChanged();
            }
        }
    }

    private DateTime _lastUpdate;
    public DateTime LastUpdate
    {
        get => _lastUpdate;
        set
        {
            if (_lastUpdate != value)
            {
                _lastUpdate = value;
                NotifyStateChanged();
            }
        }
    }

    public event Func<Task>? OnChange;

    private void NotifyStateChanged() => OnChange?.Invoke();

    private async Task UpdateExchangeRateAsync()
    {
        try
        {
            BitcoinExchangeRate = await _exchangeRateProvider.GetExchangeRateAsync(_cancellationTokenSource.Token);
            if (BitcoinExchangeRate != null)
            {
                LastUpdate = DateTime.Now;
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Exchange rate update was canceled.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "An error occurred while updating the exchange rate in AppState.");
        }
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _cancellationTokenSource.Cancel();
        _cancellationTokenSource.Dispose();
    }
}
