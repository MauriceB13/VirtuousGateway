namespace VirtuousGateway.Infrastructure.Virtuous;

internal static class PostgresAplosTransactionIdConflictClassifier
{
    internal const string UniqueIndexName =
        "uq_virtuous_gift_processing_aplos_transaction_id";

    internal static bool IsAplosTransactionIdConflict(
        string? sqlState,
        string? constraintName) =>
        string.Equals(
            sqlState,
            "23505",
            StringComparison.Ordinal)
        &&
        string.Equals(
            constraintName,
            UniqueIndexName,
            StringComparison.Ordinal);
}
