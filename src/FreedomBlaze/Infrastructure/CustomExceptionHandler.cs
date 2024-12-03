using FreedomBlaze.Exceptions;
using FreedomBlaze.Logging;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using System.ComponentModel.DataAnnotations;

namespace FreedomBlaze.Infrastructure;

public class CustomExceptionHandler : IExceptionHandler
{
    private readonly Dictionary<Type, Func<HttpContext, Exception, Task>> _exceptionHandlers;

    public CustomExceptionHandler()
    {
        // Register known exception types and handlers.
        _exceptionHandlers = new()
            {
                { typeof(ExchangeIntegrationException), HandleExchangeIntegrationException },
            };
    }

    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var exceptionType = exception.GetType();

        if (_exceptionHandlers.ContainsKey(exceptionType))
        {
            await _exceptionHandlers[exceptionType].Invoke(httpContext, exception);
            return true;
        }

        Logger.LogError("Unhandled exception!", exception);

        return false;
    }

    private async Task HandleExchangeIntegrationException(HttpContext httpContext, Exception ex)
    {
        Logger.LogError("Exchange Integration error", ex);
        //TODO: save status in db
    }
}
