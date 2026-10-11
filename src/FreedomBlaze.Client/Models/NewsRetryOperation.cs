namespace FreedomBlaze.Client.Models;

/// <summary>Status of one explicit background news retry, shared by both render modes.</summary>
public sealed record NewsRetryOperation(Guid Id, bool IsCompleted, NewsResult? Result);
