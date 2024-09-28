using MudBlazor;

namespace FreedomBlaze.Client.Helpers;

public static class SnackMessageHelper
{
    public static void SnackMessage(this ISnackbar snackbar, string message, string position, Severity severity)
    {
        snackbar.Clear();
        snackbar.Configuration.SnackbarVariant = Variant.Text;
        snackbar.Configuration.PositionClass = position;
        snackbar.Add(message, severity);
    }
}
