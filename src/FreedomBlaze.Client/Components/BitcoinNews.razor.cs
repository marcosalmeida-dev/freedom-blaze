using FreedomBlaze.Client.Helpers;
using FreedomBlaze.Client.Services;
using FreedomBlaze.Models;
using Microsoft.AspNetCore.Components;
using MudBlazor;

namespace FreedomBlaze.Client.Components;

public partial class BitcoinNews
{
    /// <summary>Number of placeholder cards shown while loading.</summary>
    private const int SkeletonCount = 9;

    /// <summary>How far back the date filter is allowed to go.</summary>
    private static readonly int MaxHistoryDays = 30;

    [Inject] private IBitcoinNewsApi NewsApi { get; set; } = default!;
    [Inject] private ISnackbar Snackbar { get; set; } = default!;

    private readonly DateTime _maxDate = DateTime.Today;
    private readonly DateTime _minDate = DateTime.Today.AddDays(-MaxHistoryDays);

    private DateTime? _selectedDate = DateTime.Today;
    private List<NewsArticleModel> _articles = [];
    private bool _loading;

    private DateOnly SelectedDate => DateOnly.FromDateTime(_selectedDate ?? DateTime.Today);
    private bool IsToday => SelectedDate == DateOnly.FromDateTime(DateTime.Today);

    protected override async Task OnInitializedAsync()
    {
        // Skip the fetch during prerender and load once the component is interactive
        // (server-interactive or WASM). This avoids blocking first paint on a slow
        // web-search generation and prevents a double fetch.
        if (RendererInfo.IsInteractive)
        {
            await LoadAsync();
        }
        else
        {
            _loading = true; // show skeletons during prerender
        }
    }

    private async Task OnDateChangedAsync(DateTime? date)
    {
        if (date is null || date == _selectedDate)
        {
            return;
        }

        _selectedDate = date;
        await LoadAsync();
    }

    private Task LoadAsync() => RunAsync(ct => NewsApi.GetNewsAsync(SelectedDate, ct));

    private Task RefreshAsync() => RunAsync(ct => NewsApi.RefreshNewsAsync(SelectedDate, ct));

    private async Task RunAsync(Func<CancellationToken, Task<List<NewsArticleModel>>> action)
    {
        try
        {
            _loading = true;
            _articles = await action(CancellationToken.None);
        }
        catch
        {
            Snackbar.SnackMessage("Couldn't load Bitcoin news. Please try again.",
                Defaults.Classes.Position.TopCenter, Severity.Error);
        }
        finally
        {
            _loading = false;
        }
    }
}
