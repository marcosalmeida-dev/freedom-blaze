using FreedomBlaze.Exceptions;
using FreedomBlaze.Interfaces;
using FreedomBlaze.Logging;
using System.Diagnostics;
using System.Threading.Tasks;

namespace FreedomBlaze.Extensions;

public static class TaskExtensions
{
    public static Task<ResultOrException<T>[]> WhenAllOrException<T>(this IEnumerable<Task<T>> tasks)
    {
        return Task.WhenAll(
            tasks.Select(
                task => task.ContinueWith(
                    t => t.IsFaulted
                        ? new ResultOrException<T>(t, t.Exception)
                        : new ResultOrException<T>(t, t.Result))));
    }
}

public class ResultOrException<T>
{
    public bool IsSuccess { get; }
    public T Result { get; }
    public Exception Exception { get; }

    public ResultOrException(Task task, T result)
    {
        IsSuccess = true;
        Result = result;
    }

    public ResultOrException(Task task, Exception ex)
    {
        IsSuccess = false;
        Exception = ex;
    }
}
