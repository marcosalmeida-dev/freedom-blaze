using FreedomBlaze.Interfaces;
using FreedomBlaze.Models;

namespace FreedomBlaze;

public class AppState : IDisposable
{
    private readonly Timer _timer;
    private readonly CancellationTokenSource _cancellationTokenSource = new();
    private readonly IExchangeRateService _exchangeRateService;
    private readonly ILogger<AppState> _logger;
    private readonly TimeProvider _timeProvider;

    public AppState(IExchangeRateService exchangeRateService, ILogger<AppState> logger, TimeProvider timeProvider)
    {
        _exchangeRateService = exchangeRateService;
        _logger = logger;
        _timeProvider = timeProvider;
        _timer = new Timer(async _ => await UpdateExchangeRateAsync(), null, TimeSpan.Zero, TimeSpan.FromMinutes(1));
    }

    private BitcoinExchangeRateModel? _bitcoinExchangeRate;
    public BitcoinExchangeRateModel? BitcoinExchangeRate
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

    private DateTimeOffset _lastUpdate;
    public DateTimeOffset LastUpdate
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

    public List<BitcoinExchangeStatusModel> ExchangeStatusList { get; private set; } = [];

    public event Func<Task>? OnChange;

    private void NotifyStateChanged() => _ = OnChange?.Invoke();

    private async Task UpdateExchangeRateAsync()
    {
        try
        {
            BitcoinExchangeRate = await _exchangeRateService.GetExchangeRateAsync(_cancellationTokenSource.Token);
            if (BitcoinExchangeRate != null)
            {
                LastUpdate = _timeProvider.GetUtcNow();
                ExchangeStatusList = _exchangeRateService.BitcoinExchangeStatusList;
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
        _timer.Dispose();
        _cancellationTokenSource.Cancel();
        _cancellationTokenSource.Dispose();
    }
}
