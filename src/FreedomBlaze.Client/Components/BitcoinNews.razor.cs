using FreedomBlaze.Client.Helpers;
using FreedomBlaze.Client.Interfaces;
using FreedomBlaze.Client.Models;
using Microsoft.AspNetCore.Components;
using MudBlazor;

namespace FreedomBlaze.Client.Components;

public partial class BitcoinNews : IDisposable
{
    /// <summary>Number of placeholder cards shown while loading (matches a full day's set).</summary>
    private const int SkeletonCount = 9;

    /// <summary>How far back the date filter is allowed to go.</summary>
    private const int MaxHistoryDays = 30;

    private const string LoadFailedMessage = "Bitcoin news is temporarily unavailable. Please try again in a moment.";

    [Inject] private IBitcoinNewsApiService NewsApi { get; set; } = default!;
    [Inject] private ISnackbar Snackbar { get; set; } = default!;

    /// <summary>Cancels in-flight work when the user navigates away from the page.</summary>
    private readonly CancellationTokenSource _cts = new();

    private DateTime? _selectedDate = DateTime.Today;
    private IReadOnlyList<NewsArticleModel> _articles = [];
    private HashSet<DateOnly> _availableDates = [];
    private string? _errorMessage;
    private bool _loading;

    // Identifies the newest load, so a superseded one can never write its (stale) result to the UI.
    private int _loadGeneration;

    private static DateOnly TodayDate => DateOnly.FromDateTime(DateTime.Today);
    private DateOnly SelectedDate => DateOnly.FromDateTime(_selectedDate ?? DateTime.Today);
    private bool IsToday => SelectedDate == TodayDate;

    // Read at render time rather than captured in a field, so a long-lived circuit that spans
    // midnight still offers the correct range.
    private static DateTime MaxSelectableDate => DateTime.Today;
    private static DateTime MinSelectableDate => DateTime.Today.AddDays(-MaxHistoryDays);

    /// <summary>"today", "yesterday", or the full date — the fastest thing to read in the subtitle.</summary>
    private string DayLabel
    {
        get
        {
            var formatted = SelectedDate.ToString("MMMM d, yyyy");
            var dayDelta = SelectedDate.DayNumber - TodayDate.DayNumber;
            return dayDelta switch
            {
                0 => $"{formatted} · today",
                -1 => $"{formatted} · yesterday",
                _ => formatted,
            };
        }
    }

    /// <summary>Every day the reader may open: those with saved news, plus today.</summary>
    private IEnumerable<DateOnly> SelectableDates => _availableDates.Append(TodayDate).Distinct();

    /// <summary>Nearest selectable day before the current one, or <c>null</c> when there is none.</summary>
    private DateOnly? PreviousDay => SelectableDates
        .Where(d => d < SelectedDate && d >= DateOnly.FromDateTime(MinSelectableDate))
        .OrderByDescending(d => d)
        .Cast<DateOnly?>()
        .FirstOrDefault();

    /// <summary>Nearest selectable day after the current one, or <c>null</c> when already at the newest.</summary>
    private DateOnly? NextDay => SelectableDates
        .Where(d => d > SelectedDate && d <= TodayDate)
        .OrderBy(d => d)
        .Cast<DateOnly?>()
        .FirstOrDefault();

    protected override async Task OnInitializedAsync()
    {
        await LoadAvailableDatesAsync();

        // During prerender, read only what is already stored: a crawler and a first paint both get
        // real content, but neither triggers the slow, paid generation. The component re-runs this
        // once it becomes interactive (server-interactive or WebAssembly), which is where a missing
        // day is actually generated.
        await LoadAsync(allowGeneration: RendererInfo.IsInteractive);
    }

    private async Task OnDateChangedAsync(DateTime? date)
    {
        if (date is null || date.Value.Date == _selectedDate?.Date)
        {
            return;
        }

        _selectedDate = date.Value.Date;
        await LoadAsync();
    }

    private async Task GoToAsync(DateOnly? date)
    {
        if (date is null || date.Value == SelectedDate)
        {
            return;
        }

        _selectedDate = date.Value.ToDateTime(TimeOnly.MinValue);
        await LoadAsync();
    }

    private Task RetryAsync() => LoadAsync();

    private Task OnEmptyStateActionAsync() => IsToday ? LoadAsync() : GoToAsync(TodayDate);

    private async Task LoadAsync(bool allowGeneration = true)
    {
        var generation = ++_loadGeneration;

        _loading = true;
        _errorMessage = null;

        try
        {
            var result = await NewsApi.GetNewsAsync(SelectedDate, allowGeneration, _cts.Token);

            if (generation != _loadGeneration)
            {
                return; // A newer request owns the UI now.
            }

            _articles = result.Articles;

            if (result.Status == NewsStatus.Unavailable)
            {
                // Show the reason in place of the grid rather than beside stale cards, so the page
                // never implies these are the headlines for the day being viewed.
                _errorMessage = result.Message ?? LoadFailedMessage;
                _articles = [];
            }

            // A generation may have produced a new day; keep the selectable dates in sync.
            await LoadAvailableDatesAsync();
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
            // The page was disposed mid-load; there is nothing left to update.
        }
        catch (Exception) when (generation == _loadGeneration)
        {
            _errorMessage = LoadFailedMessage;
            _articles = [];
            Snackbar.SnackMessage(LoadFailedMessage, Defaults.Classes.Position.TopCenter, Severity.Error);
        }
        finally
        {
            if (generation == _loadGeneration)
            {
                _loading = false;
            }
        }
    }

    private async Task LoadAvailableDatesAsync()
    {
        try
        {
            var dates = await NewsApi.GetAvailableDatesAsync(_cts.Token);
            _availableDates = [.. dates];
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
            // Disposed mid-load.
        }
        catch (Exception)
        {
            // Non-fatal: the picker simply falls back to allowing only today.
        }
    }

    // Only today (so fresh news can always be fetched) and days that already have saved news
    // are selectable; everything else is disabled.
    private bool IsDateDisabled(DateTime date)
    {
        var day = DateOnly.FromDateTime(date);
        return day != TodayDate && !_availableDates.Contains(day);
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}
