using System.Data;

namespace StreetBiz.Application.Common.Interfaces;

/// <summary>Repositories stage changes; the use case commits them together.</summary>
public interface IUnitOfWork
{
    Task SaveChangesAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Runs the work in one transaction. Nothing is committed unless the work calls
    /// <see cref="IUnitOfWorkTransaction.CommitAsync"/>. Inside an open transaction the work joins
    /// it and the outer scope commits. The work may run again after a transient failure, so it
    /// must not call outside services.
    /// </summary>
    Task<T> ExecuteInTransactionAsync<T>(
        Func<IUnitOfWorkTransaction, Task<T>> work,
        IsolationLevel isolationLevel,
        CancellationToken cancellationToken,
        bool clearTracker = true);

    Task ExecuteInTransactionAsync(
        Func<IUnitOfWorkTransaction, Task> work,
        IsolationLevel isolationLevel,
        CancellationToken cancellationToken,
        bool clearTracker = true);
}

public interface IUnitOfWorkTransaction
{
    /// <summary>Commits; does nothing when the work joined an outer transaction.</summary>
    Task CommitAsync(CancellationToken cancellationToken);
}
