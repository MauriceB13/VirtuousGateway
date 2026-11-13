using Npgsql;
using VirtuousGateway.Core.Virtuous;
using VirtuousGateway.Infrastructure.Virtuous;

namespace VirtuousGateway.Tests.Virtuous;

public sealed class PostgresAplosTransactionIdConflictTranslatorTests
{
    private const string UniqueIndexName =
        "uq_virtuous_gift_processing_aplos_transaction_id";

    [Theory]
    [InlineData("23505", UniqueIndexName, true)]
    [InlineData("23505", "other_unique_constraint", false)]
    [InlineData("23503", UniqueIndexName, false)]
    [InlineData("23505", null, false)]
    public async Task RollbackAndTranslateAsync_UsesExactPostgresClassification(
        string sqlState,
        string? constraintName,
        bool expectedConflict)
    {
        var exception =
            CreatePostgresException(
                sqlState,
                constraintName);
        var rollbackCompleted = false;

        var actual =
            await Record.ExceptionAsync(
                () => PostgresAplosTransactionIdConflictTranslator
                    .RollbackAndTranslateAsync(
                        exception,
                        () =>
                        {
                            rollbackCompleted = true;
                            return Task.CompletedTask;
                        }));

        Assert.True(rollbackCompleted);

        if (expectedConflict)
        {
            var translated =
                Assert.IsType<AplosTransactionIdConflictException>(
                    actual);
            Assert.DoesNotContain(
                "70064235",
                translated.Message);
            Assert.DoesNotContain(
                "70064235",
                translated.ToString());
        }
        else
        {
            Assert.Same(
                exception,
                actual);
        }
    }

    [Fact]
    public async Task RollbackAndTranslateAsync_RollbackFailureDoesNotTranslateConflict()
    {
        var exception =
            CreatePostgresException(
                "23505",
                UniqueIndexName);

        var rollbackFailure =
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => PostgresAplosTransactionIdConflictTranslator
                    .RollbackAndTranslateAsync(
                        exception,
                        () => Task.FromException(
                            new InvalidOperationException(
                                "Sensitive database failure 70064235."))));

        Assert.Equal(
            "The ledger transaction could not be rolled back; operator investigation is required.",
            rollbackFailure.Message);
        Assert.DoesNotContain(
            "70064235",
            rollbackFailure.ToString());
    }

    [Fact]
    public void AplosTransactionIdConflictException_PreservesLegacyConstructorAndProperty()
    {
        var exception =
            new AplosTransactionIdConflictException(70064235);

        Assert.Equal(
            70064235,
            exception.AplosTransactionId);
        Assert.Equal(
            "Aplos transaction ID 70064235 is already assigned to another gift.",
            exception.Message);
    }

    [Fact]
    public void AplosTransactionIdConflictException_DefaultConstructorIsSanitized()
    {
        var exception =
            new AplosTransactionIdConflictException();

        Assert.DoesNotContain(
            "70064235",
            exception.Message);
    }

    private static PostgresException CreatePostgresException(
        string sqlState,
        string? constraintName) =>
        new(
            "Database error containing sensitive transaction ID 70064235.",
            "ERROR",
            "ERROR",
            sqlState,
            constraintName: constraintName);
}
