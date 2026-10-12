namespace FreedomBlaze.Data.Entities;

/// <summary>Durable attempt and cooldown information for one Bitcoin news edition.</summary>
public class NewsGenerationAttempt
{
    public DateOnly Date { get; set; }
    public int AttemptCount { get; set; }
    public DateTimeOffset? LastAttemptUtc { get; set; }
    public DateTimeOffset? LastCompletedUtc { get; set; }
    public bool IsRunning { get; set; }
    public string? LastFailure { get; set; }
}
