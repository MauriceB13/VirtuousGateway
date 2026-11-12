using System.Runtime.ExceptionServices;
using Npgsql;
using VirtuousGateway.Core.Virtuous;

namespace VirtuousGateway.Infrastructure.Virtuous;

internal static class PostgresAplosTransactionIdConflictTranslator
{
    internal static async Task RollbackAndTranslateAsync(
        PostgresException exception,
        Func<Task> rollback)
    {
        try
        {
            await rollback();
        }
        catch (Exception)
        {
            throw new InvalidOperationException(
                "The ledger transaction could not be rolled back; operator investigation is required.");
        }

        if (PostgresAplosTransactionIdConflictClassifier.IsAplosTransactionIdConflict(
                exception.SqlState,
                exception.ConstraintName))
        {
            throw new AplosTransactionIdConflictException();
        }

        ExceptionDispatchInfo.Capture(exception).Throw();
    }
}
