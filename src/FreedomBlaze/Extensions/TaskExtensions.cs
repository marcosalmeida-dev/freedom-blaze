namespace FreedomBlaze.Extensions;

public static class TaskExtensions
{
    /// <summary>
    /// Awaits every task and captures each outcome individually, so a single failure never
    /// short-circuits the rest (unlike <see cref="Task.WhenAll(IEnumerable{Task})"/>).
    /// </summary>
    public static Task<ResultOrException<T>[]> WhenAllOrException<T>(this IEnumerable<Task<T>> tasks) =>
        Task.WhenAll(tasks.Select(task => task.ContinueWith(t =>
            t.IsFaulted
                ? new ResultOrException<T>(t.Exception!)
                : new ResultOrException<T>(t.Result))));
}

public class ResultOrException<T>
{
    public bool IsSuccess { get; }
    public T? Result { get; }
    public Exception? Exception { get; }

    public ResultOrException(T result)
    {
        IsSuccess = true;
        Result = result;
    }

    public ResultOrException(Exception exception)
    {
        IsSuccess = false;
        Exception = exception;
    }
}
