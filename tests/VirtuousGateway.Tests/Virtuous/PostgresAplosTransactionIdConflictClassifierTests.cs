using VirtuousGateway.Infrastructure.Virtuous;

namespace VirtuousGateway.Tests.Virtuous;

public sealed class PostgresAplosTransactionIdConflictClassifierTests
{
    [Theory]
    [InlineData(
        "23505",
        "uq_virtuous_gift_processing_aplos_transaction_id",
        true)]
    [InlineData("23505", "other_unique_constraint", false)]
    [InlineData(
        "23503",
        "uq_virtuous_gift_processing_aplos_transaction_id",
        false)]
    [InlineData("23505", null, false)]
    public void IsAplosTransactionIdConflict_RequiresExactSqlStateAndConstraint(
        string? sqlState,
        string? constraintName,
        bool expected)
    {
        Assert.Equal(
            expected,
            PostgresAplosTransactionIdConflictClassifier.IsAplosTransactionIdConflict(
                sqlState,
                constraintName));
    }
}
