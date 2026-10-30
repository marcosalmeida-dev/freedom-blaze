namespace FreedomBlaze.Services.Lightning;

/// <summary>
/// When the background sweep asks a node about a pending invoice: often while a payment is likely, right after
/// expiry so the result is final quickly, then ever less often while the node cannot say (it is down, or forgot
/// the invoice), giving up a week after expiry.
/// </summary>
public static class LightningInvoiceSchedule
{
    public static readonly TimeSpan FirstCheckDelay = TimeSpan.FromSeconds(5);

    private static readonly TimeSpan AfterExpiry = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan GiveUpAfter = TimeSpan.FromDays(7);

    public static DateTimeOffset FirstCheck(DateTimeOffset createdAt) => createdAt + FirstCheckDelay;

    /// <summary>The next check after one made at <paramref name="now"/>; null to stop checking.</summary>
    public static DateTimeOffset? NextCheck(DateTimeOffset createdAt, DateTimeOffset expiresAt, DateTimeOffset now)
    {
        TimeSpan interval;
        if (now < createdAt + TimeSpan.FromMinutes(2))
            interval = TimeSpan.FromSeconds(5);
        else if (now < expiresAt)
            interval = TimeSpan.FromSeconds(15);
        else if (now < expiresAt + TimeSpan.FromHours(1))
            interval = TimeSpan.FromMinutes(1);
        else if (now < expiresAt + TimeSpan.FromDays(1))
            interval = TimeSpan.FromMinutes(10);
        else if (now < expiresAt + GiveUpAfter)
            interval = TimeSpan.FromHours(1);
        else
            return null;

        var next = now + interval;
        var justAfterExpiry = expiresAt + AfterExpiry;
        return now < justAfterExpiry && next > justAfterExpiry ? justAfterExpiry : next;
    }
}
