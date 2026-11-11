using System.Data;
using StreetBiz.Application.Common.Interfaces;

namespace StreetBiz.Application.Tests;

/// <summary>Runs the work as is. <see cref="Commits"/> counts how many times the work committed.</summary>
internal sealed class FakeUnitOfWork : IUnitOfWork
{
    public int Commits { get; private set; }

    public int Saves { get; private set; }

    public Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        Saves++;
        return Task.CompletedTask;
    }

    public async Task<T> ExecuteInTransactionAsync<T>(
        Func<IUnitOfWorkTransaction, Task<T>> work,
        IsolationLevel isolationLevel,
        CancellationToken cancellationToken,
        bool clearTracker = true) =>
        await work(new Transaction(this));

    public Task ExecuteInTransactionAsync(
        Func<IUnitOfWorkTransaction, Task> work,
        IsolationLevel isolationLevel,
        CancellationToken cancellationToken,
        bool clearTracker = true) =>
        work(new Transaction(this));

    private sealed class Transaction(FakeUnitOfWork owner) : IUnitOfWorkTransaction
    {
        public Task CommitAsync(CancellationToken cancellationToken)
        {
            owner.Commits++;
            return Task.CompletedTask;
        }
    }
}
