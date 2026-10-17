using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using StreetBiz.Application.Common.Interfaces;

namespace StreetBiz.Infrastructure.Persistence;

/// <summary>The one place that opens a database transaction.</summary>
public static class DbContextTransactions
{
    public static async Task<T> ExecuteInTransactionAsync<T>(
        this DbContext db,
        Func<IUnitOfWorkTransaction, Task<T>> work,
        IsolationLevel isolationLevel,
        CancellationToken cancellationToken,
        bool clearTracker = true)
    {
        if (db.Database.CurrentTransaction is not null)
        {
            // Join: clearing the tracker here would drop what the outer work has staged.
            return await work(JoinedTransaction.Instance);
        }

        // EnableRetryOnFailure requires a user transaction to run inside the execution strategy.
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            if (clearTracker)
            {
                db.ChangeTracker.Clear();
            }

            await using var transaction = await db.Database.BeginTransactionAsync(isolationLevel, cancellationToken);
            return await work(new OwnedTransaction(transaction));
        });
    }

    public static Task ExecuteInTransactionAsync(
        this DbContext db,
        Func<IUnitOfWorkTransaction, Task> work,
        IsolationLevel isolationLevel,
        CancellationToken cancellationToken,
        bool clearTracker = true) =>
        db.ExecuteInTransactionAsync<object?>(
            async transaction =>
            {
                await work(transaction);
                return null;
            },
            isolationLevel,
            cancellationToken,
            clearTracker);

    private sealed class OwnedTransaction(IDbContextTransaction transaction) : IUnitOfWorkTransaction
    {
        public Task CommitAsync(CancellationToken cancellationToken) => transaction.CommitAsync(cancellationToken);
    }

    private sealed class JoinedTransaction : IUnitOfWorkTransaction
    {
        public static readonly JoinedTransaction Instance = new();

        public Task CommitAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
