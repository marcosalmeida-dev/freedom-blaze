using System.Data;
using System.Data.Common;
using System.Globalization;
using FreedomBlaze.Data;
using FreedomBlaze.Data.Entities;
using FreedomBlaze.Interfaces;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace FreedomBlaze.Services;

/// <summary>Uses a SQL Server session lock and durable rows to coordinate news generation.</summary>
public sealed class SqlNewsGenerationStore(
    IDbContextFactory<FreedomBlazeDbContext> contextFactory,
    TimeProvider timeProvider) : INewsGenerationStore
{
    public async Task<IAsyncDisposable?> TryAcquireLeaseAsync(DateOnly date, CancellationToken cancellationToken = default)
    {
        var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var connection = db.Database.GetDbConnection();
        var resource = "BitcoinNews:" + date.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        try
        {
            // The context and its connection belong exclusively to this lease. Keeping the SQL
            // session open keeps the application lock alive while other contexts save progress.
            await db.Database.OpenConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandTimeout = 30;
            command.CommandText = """
                DECLARE @result int;
                EXEC @result = sys.sp_getapplock
                    @Resource = @resource,
                    @LockMode = 'Exclusive',
                    @LockOwner = 'Session',
                    @LockTimeout = 0;
                SELECT @result;
                """;
            AddResourceParameter(command, resource);
            var result = await command.ExecuteScalarAsync(cancellationToken);
            if (result is not int status)
                throw new InvalidOperationException("SQL Server returned no news generation lease status.");
            if (status >= 0)
                return new SqlSessionLease(db, connection, resource);
            if (status == -1)
            {
                await db.DisposeAsync();
                return null;
            }

            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException($"SQL Server could not acquire the news generation lease (status {status}).");
        }
        catch
        {
            // Cancellation or a broken response can leave it unclear whether the server granted
            // the lock. Never return that session to a pool with a possibly retained lock.
            DiscardPooledSession(connection);
            await db.DisposeAsync();
            throw;
        }
    }

    public async Task<NewsGenerationState> GetStateAsync(DateOnly date, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.NewsGenerationAttempts.AsNoTracking()
            .Where(attempt => attempt.Date == date)
            .Select(attempt => new NewsGenerationState(
                attempt.AttemptCount, attempt.LastAttemptUtc, attempt.IsRunning, attempt.LastFailure, attempt.LastCompletedUtc))
            .SingleOrDefaultAsync(cancellationToken)
            ?? new NewsGenerationState(0, null, false, null);
    }

    public async Task RecordAttemptAsync(DateOnly date, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var attempt = await db.NewsGenerationAttempts.SingleOrDefaultAsync(item => item.Date == date, cancellationToken);
        if (attempt is null)
        {
            attempt = new NewsGenerationAttempt { Date = date };
            db.NewsGenerationAttempts.Add(attempt);
        }

        attempt.AttemptCount = checked(attempt.AttemptCount + 1);
        attempt.LastAttemptUtc = timeProvider.GetUtcNow();
        attempt.IsRunning = true;
        attempt.LastFailure = null;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task RecordOutcomeAsync(DateOnly date, string? failure, CancellationToken cancellationToken = default)
    {
        var message = string.IsNullOrWhiteSpace(failure) ? null : failure.Trim();
        if (message?.Length > 512) message = message[..512];

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var completedAt = timeProvider.GetUtcNow();
        await db.NewsGenerationAttempts.Where(attempt => attempt.Date == date)
            .ExecuteUpdateAsync(update => update
                .SetProperty(attempt => attempt.IsRunning, false)
                .SetProperty(attempt => attempt.LastCompletedUtc, completedAt)
                .SetProperty(attempt => attempt.LastFailure, message), cancellationToken);
    }

    private static void AddResourceParameter(DbCommand command, string resource)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@resource";
        parameter.DbType = DbType.String;
        parameter.Size = 255;
        parameter.Value = resource;
        command.Parameters.Add(parameter);
    }

    private static void DiscardPooledSession(DbConnection connection)
    {
        if (connection is SqlConnection sqlConnection) SqlConnection.ClearPool(sqlConnection);
    }

    private sealed class SqlSessionLease(
        FreedomBlazeDbContext context, DbConnection connection, string resource) : IAsyncDisposable
    {
        private FreedomBlazeDbContext? _context = context;

        public async ValueTask DisposeAsync()
        {
            var db = Interlocked.Exchange(ref _context, null);
            if (db is null) return;

            try
            {
                if (connection.State == ConnectionState.Open)
                {
                    await using var command = connection.CreateCommand();
                    command.CommandTimeout = 5;
                    command.CommandText = """
                        DECLARE @result int;
                        EXEC @result = sys.sp_releaseapplock
                            @Resource = @resource,
                            @LockOwner = 'Session';
                        SELECT @result;
                        """;
                    AddResourceParameter(command, resource);
                    // Shutdown cancellation must not prevent release. The command timeout bounds
                    // cleanup, and disposing the session below releases its lock even on failure.
                    var result = await command.ExecuteScalarAsync(CancellationToken.None);
                    if (result is not int status || status < 0)
                        throw new InvalidOperationException("SQL Server could not release the news generation lease.");
                }
            }
            catch
            {
                DiscardPooledSession(connection);
                throw;
            }
            finally
            {
                await db.DisposeAsync();
            }
        }
    }
}
