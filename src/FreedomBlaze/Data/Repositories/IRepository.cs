using System.Linq.Expressions;

namespace FreedomBlaze.Data.Repositories;

/// <summary>
/// Generic data-access service for any <see cref="FreedomBlazeDbContext"/> entity. Each call runs
/// against its own short-lived context (created from an <c>IDbContextFactory</c>), which is the
/// safe pattern for Blazor where a scoped context would otherwise live for the whole circuit.
/// </summary>
/// <typeparam name="TEntity">A class entity mapped on the context.</typeparam>
public interface IRepository<TEntity> where TEntity : class
{
    Task<TEntity?> GetByIdAsync(object id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TEntity>> ListAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TEntity>> ListAsync(Expression<Func<TEntity, bool>> predicate, CancellationToken cancellationToken = default);

    Task<TEntity?> FirstOrDefaultAsync(Expression<Func<TEntity, bool>> predicate, CancellationToken cancellationToken = default);

    Task<bool> AnyAsync(Expression<Func<TEntity, bool>> predicate, CancellationToken cancellationToken = default);

    Task<int> CountAsync(Expression<Func<TEntity, bool>>? predicate = null, CancellationToken cancellationToken = default);

    /// <summary>Adds and persists a single entity.</summary>
    Task<TEntity> AddAsync(TEntity entity, CancellationToken cancellationToken = default);

    /// <summary>Updates and persists a single entity.</summary>
    Task UpdateAsync(TEntity entity, CancellationToken cancellationToken = default);

    /// <summary>Removes and persists the deletion of a single entity.</summary>
    Task RemoveAsync(TEntity entity, CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs a custom query (e.g. with Include/Where/Select) against a short-lived context. The
    /// supplied delegate must fully materialize its result before the context is disposed.
    /// </summary>
    Task<TResult> QueryAsync<TResult>(
        Func<IQueryable<TEntity>, CancellationToken, Task<TResult>> query,
        CancellationToken cancellationToken = default);
}
