using System.Data;
using Microsoft.EntityFrameworkCore;
using StreetBiz.Application.Common.Interfaces;

namespace StreetBiz.Infrastructure.Persistence;

/// <summary>The request's scoped <see cref="StreetBizDbContext"/> as a unit of work.</summary>
public sealed class EfUnitOfWork(StreetBizDbContext db) : IUnitOfWork
{
    public async Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception)
        {
            var translated = SqlErrorTranslator.TryTranslate(exception);
            if (translated is null)
            {
                throw;
            }

            throw translated;
        }
    }

    public Task<T> ExecuteInTransactionAsync<T>(
        Func<IUnitOfWorkTransaction, Task<T>> work,
        IsolationLevel isolationLevel,
        CancellationToken cancellationToken,
        bool clearTracker = true) =>
        db.ExecuteInTransactionAsync(work, isolationLevel, cancellationToken, clearTracker);

    public Task ExecuteInTransactionAsync(
        Func<IUnitOfWorkTransaction, Task> work,
        IsolationLevel isolationLevel,
        CancellationToken cancellationToken,
        bool clearTracker = true) =>
        db.ExecuteInTransactionAsync(work, isolationLevel, cancellationToken, clearTracker);
}
