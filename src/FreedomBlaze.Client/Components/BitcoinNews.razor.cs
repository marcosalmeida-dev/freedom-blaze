using FreedomBlaze.Client.Interfaces;
using FreedomBlaze.Client.Models;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

namespace FreedomBlaze.Client.Components;

public partial class BitcoinNews : IDisposable
{
    private const int SkeletonCount = 6;
    private const int MaxHistoryDays = 30;
    private const string StateKey = "BitcoinNews.InitialState";
    private const string LoadFailedMessage = "Bitcoin news is temporarily unavailable. Please try again in a moment.";

    [Inject] private IBitcoinNewsApiService NewsApi { get; set; } = default!;
    [Inject] private PersistentComponentState ComponentState { get; set; } = default!;

    private readonly CancellationTokenSource _lifetimeCts = new();
    private readonly Dictionary<DateOnly, IReadOnlyList<NewsArticleModel>> _loadedDays = [];
    private CancellationTokenSource? _loadCts;
    private PersistingComponentStateSubscription? _persistingSubscription;
    private DateTime? _selectedDate = DateTime.Today;
    private IReadOnlyList<NewsArticleModel> _articles = [];
    private HashSet<DateOnly> _availableDates = [];
    private string? _errorMessage;
    private bool _loading = true;
    private bool _disposed;

    private static DateOnly TodayDate => DateOnly.FromDateTime(DateTime.Today);
    private DateOnly SelectedDate => DateOnly.FromDateTime(_selectedDate ?? DateTime.Today);
    private bool IsToday => SelectedDate == TodayDate;
    private static DateTime MaxSelectableDate => DateTime.Today;
    private static DateTime MinSelectableDate => DateTime.Today.AddDays(-MaxHistoryDays);

    private string DayLabel
    {
        get
        {
            var formatted = SelectedDate.ToString("MMMM d, yyyy");
            return (SelectedDate.DayNumber - TodayDate.DayNumber) switch
            {
                0 => $"{formatted} · today",
                -1 => $"{formatted} · yesterday",
                _ => formatted,
            };
        }
    }

    private string ResultsLabel => _loading ? "Loading headlines…"
        : _errorMessage is not null ? "Unable to load"
        : _articles.Count == 1 ? "1 story" : $"{_articles.Count} stories";

    private DateOnly? PreviousDay => FindAdjacentDay(earlier: true);
    private DateOnly? NextDay => FindAdjacentDay(earlier: false);

    protected override async Task OnInitializedAsync()
    {
        if (ComponentState.TryTakeFromJson<InitialNewsState>(StateKey, out var restored) && restored is not null)
        {
            _selectedDate = restored.Date.ToDateTime(TimeOnly.MinValue);
            _articles = restored.Articles;
            _availableDates = [.. restored.AvailableDates];
            _errorMessage = restored.ErrorMessage;
            _loading = false;

            if (_articles.Count > 0)
            {
                _loadedDays[restored.Date] = _articles;
            }
            else if (_errorMessage is null && RendererInfo.IsInteractive)
            {
                // An empty prerender only checked storage. Generation is allowed once interactive.
                await LoadAsync();
            }
        }
        else
        {
            // Date metadata and headlines are independent; do not delay first content on metadata.
            await Task.WhenAll(LoadAvailableDatesAsync(), LoadAsync(allowGeneration: RendererInfo.IsInteractive));
        }

        if (!_disposed)
        {
            // Register after initialization so prerender persists complete data. InteractiveAuto
            // restores it in either renderer, avoiding a second fetch and a loading flash.
            _persistingSubscription = ComponentState.RegisterOnPersisting(PersistStateAsync, RenderMode.InteractiveAuto);
        }
    }

    private Task PersistStateAsync()
    {
        ComponentState.PersistAsJson(StateKey,
            new InitialNewsState(SelectedDate, [.. _articles], [.. _availableDates], _errorMessage));
        return Task.CompletedTask;
    }

    private Task OnDateChangedAsync(DateTime? date) =>
        GoToAsync(date is null ? null : DateOnly.FromDateTime(date.Value));

    private async Task GoToAsync(DateOnly? date)
    {
        if (_disposed || date is null || date.Value == SelectedDate || IsDateDisabled(date.Value.ToDateTime(TimeOnly.MinValue)))
        {
            return;
        }

        _selectedDate = date.Value.ToDateTime(TimeOnly.MinValue);
        await LoadAsync();
    }

    private Task RetryAsync() => Task.WhenAll(LoadAvailableDatesAsync(), LoadAsync(forceReload: true));
    private Task OnEmptyStateActionAsync() => IsToday ? RetryAsync() : GoToAsync(TodayDate);

    private async Task LoadAsync(bool allowGeneration = true, bool forceReload = false)
    {
        if (_disposed)
        {
            return;
        }

        _loadCts?.Cancel();
        using var request = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
        _loadCts = request;
        var date = SelectedDate;
        _loading = true;
        _errorMessage = null;
        _articles = [];

        try
        {
            if (!forceReload && _loadedDays.TryGetValue(date, out var cached))
            {
                _articles = cached;
                return;
            }

            var result = await NewsApi.GetNewsAsync(date, allowGeneration, request.Token);
            if (request.IsCancellationRequested || _disposed || !ReferenceEquals(_loadCts, request))
            {
                return;
            }

            if (result.Status == NewsStatus.Unavailable)
            {
                _errorMessage = result.Message ?? LoadFailedMessage;
                return;
            }

            _articles = result.Articles;
            if (result.HasArticles)
            {
                // Successful days are immutable during a visit. Empty/error results remain retryable.
                _loadedDays[date] = result.Articles;
                _availableDates.Add(date);
                var oldestDate = DateOnly.FromDateTime(MinSelectableDate);
                foreach (var expired in _loadedDays.Keys.Where(day => day < oldestDate).ToArray())
                {
                    _loadedDays.Remove(expired);
                }
            }
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested)
        {
            // Navigating away or selecting another date cancels work without showing an error.
        }
        catch (Exception)
        {
            if (!_disposed && !request.IsCancellationRequested && ReferenceEquals(_loadCts, request))
            {
                _errorMessage = LoadFailedMessage;
                _articles = [];
            }
        }
        finally
        {
            if (!_disposed && ReferenceEquals(_loadCts, request))
            {
                _loading = false;
                _loadCts = null;
            }
        }
    }

    private async Task LoadAvailableDatesAsync()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            var dates = await NewsApi.GetAvailableDatesAsync(_lifetimeCts.Token);
            if (!_disposed)
            {
                // Union preserves a freshly generated day if its request beat this metadata read.
                _availableDates.UnionWith(dates);
            }
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            // Existing dates remain available if the optional metadata request fails.
        }
    }

    private DateOnly? FindAdjacentDay(bool earlier)
    {
        DateOnly? nearest = null;
        var selected = SelectedDate;
        var today = TodayDate;
        var oldest = today.AddDays(-MaxHistoryDays);

        foreach (var day in _availableDates)
        {
            if (day < oldest || day > today)
            {
                continue;
            }

            if (earlier ? day < selected && (nearest is null || day > nearest)
                : day > selected && (nearest is null || day < nearest))
            {
                nearest = day;
            }
        }

        return !earlier && selected < today && nearest is null ? today : nearest;
    }

    private bool IsDateDisabled(DateTime date)
    {
        var day = DateOnly.FromDateTime(date);
        return day > TodayDate || day < TodayDate.AddDays(-MaxHistoryDays)
            || (day != TodayDate && !_availableDates.Contains(day));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _persistingSubscription?.Dispose();
        _lifetimeCts.Cancel();
        _lifetimeCts.Dispose();
        _loadCts = null;
    }

    private sealed record InitialNewsState(DateOnly Date, List<NewsArticleModel> Articles,
        List<DateOnly> AvailableDates, string? ErrorMessage);
}
