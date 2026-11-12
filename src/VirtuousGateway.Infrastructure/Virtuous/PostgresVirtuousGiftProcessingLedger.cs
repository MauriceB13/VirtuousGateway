using VirtuousGateway.Core.Configuration;
using VirtuousGateway.Core.Virtuous;
using Microsoft.Extensions.Options;
using Npgsql;

namespace VirtuousGateway.Infrastructure.Virtuous;

public sealed class PostgresVirtuousGiftProcessingLedger
    : IVirtuousGiftProcessingLedger
{
    private readonly string _connectionString;

    public PostgresVirtuousGiftProcessingLedger(
        IOptions<ProcessingLedgerOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _connectionString =
            options.Value.ConnectionString;
    }

    public async Task<VirtuousGiftProcessingClaim> BeginProcessingAsync(
    long giftId,
    string giftFingerprint,
    CancellationToken cancellationToken = default)
    {
        if (giftId <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(giftId),
                "Gift ID must be greater than zero.");
        }

        if (string.IsNullOrWhiteSpace(giftFingerprint))
        {
            throw new ArgumentException(
                "Gift fingerprint cannot be empty.",
                nameof(giftFingerprint));
        }

        if (string.IsNullOrWhiteSpace(giftFingerprint))
        {
            throw new ArgumentException(
                "Gift fingerprint cannot be empty.",
                nameof(giftFingerprint));
        }

        await EnsureDatabaseAsync(
            cancellationToken);

        await using var connection =
            new NpgsqlConnection(
                _connectionString);

        await connection.OpenAsync(
            cancellationToken);

        await using var transaction =
            await connection.BeginTransactionAsync(
                cancellationToken);

        try
        {
            await AcquireGiftLockAsync(
                connection,
                transaction,
                giftId,
                cancellationToken);

            var existingRecord =
                await GetRecordAsync(
                    connection,
                    transaction,
                    giftId,
                    cancellationToken);

            VirtuousGiftProcessingClaim claim;

            if (existingRecord is not null)
            {
                if (string.IsNullOrWhiteSpace(
                        existingRecord.GiftFingerprint)
                    || !string.Equals(
                        existingRecord.GiftFingerprint,
                        giftFingerprint,
                        StringComparison.Ordinal))
                {
                    throw new VirtuousGiftFingerprintMismatchException(
                        giftId);
                }

                claim =
                    new VirtuousGiftProcessingClaim
                    {
                        Record = existingRecord,
                        ShouldProcess = false
                    };
            }
            else
           {
                claim =
                    await CreateProcessingRecordAsync(
                        connection,
                        transaction,
                        giftId,
                        giftFingerprint,
                        cancellationToken);
            }

            await transaction.CommitAsync(
                cancellationToken);

            return claim;
        }
        catch
        {
            await transaction.RollbackAsync(
                CancellationToken.None);

            throw;
        }
    }

    public async Task<VirtuousGiftProcessingClaim> RetryFailedAsync(
        long giftId,
        string giftFingerprint,
        CancellationToken cancellationToken = default)
        {
            if (giftId <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(giftId),
                    "Gift ID must be greater than zero.");
            }

            await EnsureDatabaseAsync(
                cancellationToken);

            await using var connection =
                new NpgsqlConnection(
                    _connectionString);

            await connection.OpenAsync(
                cancellationToken);

            await using var transaction =
                await connection.BeginTransactionAsync(
                    cancellationToken);

            try
            {
                await AcquireGiftLockAsync(
                    connection,
                    transaction,
                    giftId,
                    cancellationToken);

                var existingRecord =
                    await GetRecordAsync(
                        connection,
                        transaction,
                        giftId,
                        cancellationToken);

                if (existingRecord is null)
                {
                    throw new InvalidOperationException(
                        $"Virtuous gift {giftId} does not have an existing processing record.");
                }

                if (existingRecord.Status !=
                    VirtuousGiftProcessingStatus.Failed)
                {
                    throw new VirtuousGiftProcessingStateException(
                        giftId,
                        existingRecord.Status);
                }

                if (string.IsNullOrWhiteSpace(
                        existingRecord.GiftFingerprint)
                    || !string.Equals(
                        existingRecord.GiftFingerprint,
                        giftFingerprint,
                        StringComparison.Ordinal))
                {
                    throw new VirtuousGiftFingerprintMismatchException(
                        giftId);
                }

                var attemptId =
                    Guid.NewGuid();

                var now =
                    DateTime.UtcNow;

                await using var command =
                    connection.CreateCommand();

                command.Transaction =
                    transaction;

                command.CommandText =
                    """
                    UPDATE virtuous_gift_processing
                    SET
                        attempt_id = @attemptId,
                        status = 'Processing',
                        aplos_transaction_id = NULL,
                        aplos_response = NULL,
                        attempt_count = attempt_count + 1,
                        last_attempt_utc = @lastAttemptUtc,
                        completed_utc = NULL,
                        failure_message = NULL
                    WHERE
                        gift_id = @giftId
                        AND status = 'Failed'
                    RETURNING
                        gift_id,
                        gift_fingerprint,
                        attempt_id,
                        status,
                        aplos_transaction_id,
                        aplos_response,
                        attempt_count,
                        created_utc,
                        last_attempt_utc,
                        completed_utc,
                        failure_message;
                    """;

                command.Parameters.AddWithValue(
                    "giftId",
                    giftId);

                command.Parameters.AddWithValue(
                    "attemptId",
                    attemptId);

                command.Parameters.AddWithValue(
                    "lastAttemptUtc",
                    now);

                await using var reader =
                    await command.ExecuteReaderAsync(
                        cancellationToken);

                if (!await reader.ReadAsync(
                        cancellationToken))
                {
                    throw new InvalidOperationException(
                        "The failed gift could not be claimed for retry.");
                }

                var record =
                    ReadRecord(reader);

                await reader.DisposeAsync();

                await transaction.CommitAsync(
                    cancellationToken);

                return new VirtuousGiftProcessingClaim
                {
                    Record = record,
                    ShouldProcess = true
                };
            }
            catch
            {
                await transaction.RollbackAsync(
                    CancellationToken.None);

                throw;
            }
        }

    public async Task<VirtuousGiftProcessingRecord> CompleteAsync(
        long giftId,
        Guid attemptId,
        long aplosTransactionId,
        string aplosResponse,
        CancellationToken cancellationToken = default)
    {
        if (giftId <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(giftId),
                "Gift ID must be greater than zero.");
        }

        if (attemptId == Guid.Empty)
        {
            throw new ArgumentException(
                "Attempt ID cannot be empty.",
                nameof(attemptId));
        }

        if (aplosTransactionId <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(aplosTransactionId),
                "Aplos transaction ID must be greater than zero.");
        }

        if (string.IsNullOrWhiteSpace(aplosResponse))
        {
            throw new ArgumentException(
                "Aplos response cannot be empty.",
                nameof(aplosResponse));
        }

        await EnsureDatabaseAsync(
            cancellationToken);

        await using var connection =
            new NpgsqlConnection(
                _connectionString);

        await connection.OpenAsync(
            cancellationToken);

        await using var transaction =
            await connection.BeginTransactionAsync(
                cancellationToken);

        try
        {
            await EnsureAplosTransactionIdAvailableAsync(
                connection,
                transaction,
                giftId,
                aplosTransactionId,
                cancellationToken);

            var completedUtc =
                DateTime.UtcNow;

            await using var command =
                connection.CreateCommand();

            command.Transaction =
                transaction;

            command.CommandText =
                """
                UPDATE virtuous_gift_processing
                SET
                    status = 'Completed',
                    aplos_transaction_id = @aplosTransactionId,
                    aplos_response = @aplosResponse,
                    completed_utc = @completedUtc,
                    failure_message = NULL
                WHERE
                    gift_id = @giftId
                    AND attempt_id = @attemptId
                    AND status = 'Processing'
                RETURNING
                    gift_id,
                    gift_fingerprint,
                    attempt_id,
                    status,
                    aplos_transaction_id,
                    aplos_response,
                    attempt_count,
                    created_utc,
                    last_attempt_utc,
                    completed_utc,
                    failure_message;
                """;

            command.Parameters.AddWithValue(
                "giftId",
                giftId);
            command.Parameters.AddWithValue(
                "attemptId",
                attemptId);
            command.Parameters.AddWithValue(
                "aplosTransactionId",
                aplosTransactionId);
            command.Parameters.AddWithValue(
                "aplosResponse",
                aplosResponse);
            command.Parameters.AddWithValue(
                "completedUtc",
                completedUtc);

            VirtuousGiftProcessingRecord record;

            await using (var reader =
                await command.ExecuteReaderAsync(
                    cancellationToken))
            {
                if (!await reader.ReadAsync(
                        cancellationToken))
                {
                    throw new InvalidOperationException(
                        "The gift processing attempt is no longer active.");
                }

                record =
                    ReadRecord(reader);
            }

            await transaction.CommitAsync(
                cancellationToken);

            return record;
        }
        catch (PostgresException exception)
        {
            await PostgresAplosTransactionIdConflictTranslator
                .RollbackAndTranslateAsync(
                    exception,
                    () => transaction.RollbackAsync(
                        CancellationToken.None));
            throw;
        }
        catch
        {
            await transaction.RollbackAsync(
                CancellationToken.None);

            throw;
        }
    }

    public async Task<VirtuousGiftProcessingRecord> FailAsync(
        long giftId,
        Guid attemptId,
        string failureMessage,
        CancellationToken cancellationToken = default)
    {
        if (giftId <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(giftId),
                "Gift ID must be greater than zero.");
        }

        if (attemptId == Guid.Empty)
        {
            throw new ArgumentException(
                "Attempt ID cannot be empty.",
                nameof(attemptId));
        }

        if (string.IsNullOrWhiteSpace(failureMessage))
        {
            throw new ArgumentException(
                "Failure message cannot be empty.",
                nameof(failureMessage));
        }

        await EnsureDatabaseAsync(
            cancellationToken);

        await using var connection =
            new NpgsqlConnection(
                _connectionString);

        await connection.OpenAsync(
            cancellationToken);

        await using var command =
            connection.CreateCommand();

        command.CommandText =
            """
            UPDATE virtuous_gift_processing
            SET
                status = 'Failed',
                aplos_transaction_id = NULL,
                aplos_response = NULL,
                completed_utc = NULL,
                failure_message = @failureMessage
            WHERE
                gift_id = @giftId
                AND attempt_id = @attemptId
                AND status = 'Processing'
            RETURNING
                gift_id,
                gift_fingerprint,
                attempt_id,
                status,
                aplos_transaction_id,
                aplos_response,
                attempt_count,
                created_utc,
                last_attempt_utc,
                completed_utc,
                failure_message;
            """;

        command.Parameters.AddWithValue(
            "giftId",
            giftId);

        command.Parameters.AddWithValue(
            "attemptId",
            attemptId);

        command.Parameters.AddWithValue(
            "failureMessage",
            failureMessage);

        await using var reader =
            await command.ExecuteReaderAsync(
                cancellationToken);

        if (!await reader.ReadAsync(
                cancellationToken))
        {
            throw new InvalidOperationException(
                "The gift processing attempt is no longer active.");
        }

        return ReadRecord(reader);
    }

    public async Task<VirtuousGiftProcessingRecord> RequireReconciliationAsync(
    long giftId,
    Guid attemptId,
    string failureMessage,
    CancellationToken cancellationToken = default)
    {
        if (giftId <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(giftId),
                "Gift ID must be greater than zero.");
        }

        if (attemptId == Guid.Empty)
        {
            throw new ArgumentException(
                "Attempt ID cannot be empty.",
                nameof(attemptId));
        }

        if (string.IsNullOrWhiteSpace(failureMessage))
        {
            throw new ArgumentException(
                "Failure message cannot be empty.",
                nameof(failureMessage));
        }

        await EnsureDatabaseAsync(
            cancellationToken);

        await using var connection =
            new NpgsqlConnection(
                _connectionString);

        await connection.OpenAsync(
            cancellationToken);

        await using var command =
            connection.CreateCommand();

        command.CommandText =
            """
            UPDATE virtuous_gift_processing
            SET
                status = 'RequiresReconciliation',
                aplos_transaction_id = NULL,
                aplos_response = NULL,
                completed_utc = NULL,
                failure_message = @failureMessage
            WHERE
                gift_id = @giftId
                AND attempt_id = @attemptId
                AND status = 'Processing'
            RETURNING
                gift_id,
                gift_fingerprint,
                attempt_id,
                status,
                aplos_transaction_id,
                aplos_response,
                attempt_count,
                created_utc,
                last_attempt_utc,
                completed_utc,
                failure_message;
            """;

        command.Parameters.AddWithValue(
            "giftId",
            giftId);

        command.Parameters.AddWithValue(
            "attemptId",
            attemptId);

        command.Parameters.AddWithValue(
            "failureMessage",
            failureMessage);

        await using var reader =
            await command.ExecuteReaderAsync(
                cancellationToken);

        if (!await reader.ReadAsync(
                cancellationToken))
        {
            throw new InvalidOperationException(
                "The gift processing attempt is no longer active.");
        }

        return ReadRecord(reader);
    }

    public async Task<bool> HasAplosTransactionIdConflictAsync(
        long giftId,
        long aplosTransactionId,
        CancellationToken cancellationToken = default)
    {
        ValidateGiftAndTransactionIds(
            giftId,
            aplosTransactionId);

        await EnsureDatabaseAsync(
            cancellationToken);

        await using var connection =
            new NpgsqlConnection(
                _connectionString);

        await connection.OpenAsync(
            cancellationToken);

        await using var command =
            connection.CreateCommand();

        command.CommandText =
            """
            SELECT EXISTS (
                SELECT 1
                FROM virtuous_gift_processing
                WHERE aplos_transaction_id = @aplosTransactionId
                    AND gift_id <> @giftId
            );
            """;

        command.Parameters.AddWithValue(
            "giftId",
            giftId);

        command.Parameters.AddWithValue(
            "aplosTransactionId",
            aplosTransactionId);

        return (bool)(await command.ExecuteScalarAsync(
            cancellationToken))!;
    }

    public async Task<VirtuousGiftProcessingRecord> ConfirmReconciledCompletedAsync(
        long giftId,
        Guid attemptId,
        long aplosTransactionId,
        string verifiedBy,
        string verificationEvidence,
        CancellationToken cancellationToken = default)
    {
        ValidateReconciliationInputs(
            giftId,
            attemptId,
            verifiedBy,
            verificationEvidence);

        if (aplosTransactionId <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(aplosTransactionId),
                "Aplos transaction ID must be greater than zero.");
        }

        await EnsureDatabaseAsync(
            cancellationToken);

        await using var connection =
            new NpgsqlConnection(
                _connectionString);

        await connection.OpenAsync(
            cancellationToken);

        await using var transaction =
            await connection.BeginTransactionAsync(
                cancellationToken);

        try
        {
            await EnsureAplosTransactionIdAvailableAsync(
                connection,
                transaction,
                giftId,
                aplosTransactionId,
                cancellationToken);

            var verifiedUtc =
                DateTime.UtcNow;

            await using var updateCommand =
                connection.CreateCommand();

            updateCommand.Transaction =
                transaction;

            updateCommand.CommandText =
                """
                UPDATE virtuous_gift_processing
                SET
                    status = 'Completed',
                    aplos_transaction_id = @aplosTransactionId,
                    aplos_response = NULL,
                    completed_utc = @verifiedUtc,
                    failure_message = NULL
                WHERE
                    gift_id = @giftId
                    AND attempt_id = @attemptId
                    AND status = 'RequiresReconciliation'
                RETURNING
                    gift_id,
                    gift_fingerprint,
                    attempt_id,
                    status,
                    aplos_transaction_id,
                    aplos_response,
                    attempt_count,
                    created_utc,
                    last_attempt_utc,
                    completed_utc,
                    failure_message;
                """;

            updateCommand.Parameters.AddWithValue(
                "giftId",
                giftId);
            updateCommand.Parameters.AddWithValue(
                "attemptId",
                attemptId);
            updateCommand.Parameters.AddWithValue(
                "aplosTransactionId",
                aplosTransactionId);
            updateCommand.Parameters.AddWithValue(
                "verifiedUtc",
                verifiedUtc);

            var record =
                await ReadUpdatedRecordAsync(
                    updateCommand,
                    "The gift is not awaiting reconciliation for this attempt.",
                    cancellationToken);

            await InsertReconciliationAuditAsync(
                connection,
                transaction,
                giftId,
                attemptId,
                "Completed",
                aplosTransactionId,
                verifiedBy,
                verificationEvidence,
                verifiedUtc,
                cancellationToken);

            await transaction.CommitAsync(
                cancellationToken);

            return record;
        }
        catch (PostgresException exception)
        {
            await PostgresAplosTransactionIdConflictTranslator
                .RollbackAndTranslateAsync(
                    exception,
                    () => transaction.RollbackAsync(
                        CancellationToken.None));
            throw;
        }
        catch
        {
            await transaction.RollbackAsync(
                CancellationToken.None);

            throw;
        }
    }

    private async Task EnsureDatabaseAsync(
        CancellationToken cancellationToken)
    {
        await using var connection =
            new NpgsqlConnection(
                _connectionString);

        await connection.OpenAsync(
            cancellationToken);

        await using var command =
            connection.CreateCommand();

        command.CommandText =
            """
            CREATE TABLE IF NOT EXISTS virtuous_gift_processing
            (
                gift_id BIGINT NOT NULL PRIMARY KEY,
                gift_fingerprint TEXT NULL,
                attempt_id UUID NOT NULL,
                status TEXT NOT NULL,
                aplos_transaction_id BIGINT NULL,
                aplos_response TEXT NULL,
                attempt_count INTEGER NOT NULL,
                created_utc TIMESTAMPTZ NOT NULL,
                last_attempt_utc TIMESTAMPTZ NULL,
                completed_utc TIMESTAMPTZ NULL,
                failure_message TEXT NULL,

                CONSTRAINT ck_virtuous_gift_processing_status
                    CHECK (status IN (
                        'Processing',
                        'Completed',
                        'Failed',
                        'RequiresReconciliation'
                    )),

                CONSTRAINT ck_virtuous_gift_processing_attempt_count
                    CHECK (attempt_count >= 1),

                CONSTRAINT ck_virtuous_gift_processing_aplos_transaction_id
                    CHECK (
                        aplos_transaction_id IS NULL
                        OR aplos_transaction_id > 0
                    )
            );

            ALTER TABLE virtuous_gift_processing
                ADD COLUMN IF NOT EXISTS gift_fingerprint TEXT NULL;

            CREATE TABLE IF NOT EXISTS virtuous_gift_reconciliation_audit
            (
                audit_id BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                gift_id BIGINT NOT NULL
                    REFERENCES virtuous_gift_processing (gift_id),
                attempt_id UUID NOT NULL,
                previous_status TEXT NOT NULL
                    CHECK (previous_status = 'RequiresReconciliation'),
                new_status TEXT NOT NULL
                    CHECK (new_status = 'Completed'),
                aplos_transaction_id BIGINT NULL,
                verified_by TEXT NOT NULL
                    CHECK (length(btrim(verified_by)) > 0),
                verification_evidence TEXT NOT NULL
                    CHECK (length(btrim(verification_evidence)) > 0),
                verified_utc TIMESTAMPTZ NOT NULL,
                CONSTRAINT ck_virtuous_gift_reconciliation_audit_transaction
                    CHECK (
                        (new_status = 'Completed'
                            AND aplos_transaction_id IS NOT NULL
                            AND aplos_transaction_id > 0)
                    )
            );

            CREATE INDEX IF NOT EXISTS ix_virtuous_gift_reconciliation_audit_gift
                ON virtuous_gift_reconciliation_audit (gift_id);

            """;

        await command.ExecuteNonQueryAsync(
            cancellationToken);
    }

    private static async Task AcquireGiftLockAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long giftId,
        CancellationToken cancellationToken)
    {
        await using var command =
            connection.CreateCommand();

        command.Transaction =
            transaction;

        command.CommandText =
            """
            SELECT pg_advisory_xact_lock(@giftId);
            """;

        command.Parameters.AddWithValue(
            "giftId",
            giftId);

        await command.ExecuteNonQueryAsync(
            cancellationToken);
    }

    private static async Task EnsureAplosTransactionIdAvailableAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long giftId,
        long aplosTransactionId,
        CancellationToken cancellationToken)
    {
        await using var lockCommand =
            connection.CreateCommand();

        lockCommand.Transaction =
            transaction;

        lockCommand.CommandText =
            """
            SELECT pg_advisory_xact_lock(@lockId);
            """;

        lockCommand.Parameters.AddWithValue(
            "lockId",
            long.MinValue ^ aplosTransactionId);

        await lockCommand.ExecuteNonQueryAsync(
            cancellationToken);

        await using var conflictCommand =
            connection.CreateCommand();

        conflictCommand.Transaction =
            transaction;

        conflictCommand.CommandText =
            """
            SELECT EXISTS (
                SELECT 1
                FROM virtuous_gift_processing
                WHERE aplos_transaction_id = @aplosTransactionId
                    AND gift_id <> @giftId
            );
            """;

        conflictCommand.Parameters.AddWithValue(
            "giftId",
            giftId);
        conflictCommand.Parameters.AddWithValue(
            "aplosTransactionId",
            aplosTransactionId);

        var hasConflict =
            (bool)(await conflictCommand.ExecuteScalarAsync(
                cancellationToken))!;

        if (hasConflict)
        {
            throw new AplosTransactionIdConflictException();
        }
    }

    private static async Task<VirtuousGiftProcessingRecord> ReadUpdatedRecordAsync(
        NpgsqlCommand command,
        string notUpdatedMessage,
        CancellationToken cancellationToken)
    {
        await using var reader =
            await command.ExecuteReaderAsync(
                cancellationToken);

        if (!await reader.ReadAsync(
                cancellationToken))
        {
            throw new InvalidOperationException(
                notUpdatedMessage);
        }

        return ReadRecord(reader);
    }

    private static async Task InsertReconciliationAuditAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long giftId,
        Guid attemptId,
        string newStatus,
        long? aplosTransactionId,
        string verifiedBy,
        string verificationEvidence,
        DateTime verifiedUtc,
        CancellationToken cancellationToken)
    {
        await using var command =
            connection.CreateCommand();

        command.Transaction =
            transaction;

        command.CommandText =
            """
            INSERT INTO virtuous_gift_reconciliation_audit
            (
                gift_id,
                attempt_id,
                previous_status,
                new_status,
                aplos_transaction_id,
                verified_by,
                verification_evidence,
                verified_utc
            )
            VALUES
            (
                @giftId,
                @attemptId,
                'RequiresReconciliation',
                @newStatus,
                @aplosTransactionId,
                @verifiedBy,
                @verificationEvidence,
                @verifiedUtc
            );
            """;

        command.Parameters.AddWithValue(
            "giftId",
            giftId);
        command.Parameters.AddWithValue(
            "attemptId",
            attemptId);
        command.Parameters.AddWithValue(
            "newStatus",
            newStatus);
        command.Parameters.AddWithValue(
            "aplosTransactionId",
            aplosTransactionId is null
                ? DBNull.Value
                : aplosTransactionId.Value);
        command.Parameters.AddWithValue(
            "verifiedBy",
            verifiedBy);
        command.Parameters.AddWithValue(
            "verificationEvidence",
            verificationEvidence);
        command.Parameters.AddWithValue(
            "verifiedUtc",
            verifiedUtc);

        await command.ExecuteNonQueryAsync(
            cancellationToken);
    }

    private static void ValidateGiftAndTransactionIds(
        long giftId,
        long aplosTransactionId)
    {
        if (giftId <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(giftId),
                "Gift ID must be greater than zero.");
        }

        if (aplosTransactionId <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(aplosTransactionId),
                "Aplos transaction ID must be greater than zero.");
        }
    }

    private static void ValidateReconciliationInputs(
        long giftId,
        Guid attemptId,
        string verifiedBy,
        string verificationEvidence)
    {
        if (giftId <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(giftId),
                "Gift ID must be greater than zero.");
        }

        if (attemptId == Guid.Empty)
        {
            throw new ArgumentException(
                "Attempt ID cannot be empty.",
                nameof(attemptId));
        }

        if (string.IsNullOrWhiteSpace(verifiedBy))
        {
            throw new ArgumentException(
                "Verifier cannot be empty.",
                nameof(verifiedBy));
        }

        if (string.IsNullOrWhiteSpace(verificationEvidence))
        {
            throw new ArgumentException(
                "Verification evidence cannot be empty.",
                nameof(verificationEvidence));
        }
    }

    private static async Task<VirtuousGiftProcessingRecord?> GetRecordAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long giftId,
        CancellationToken cancellationToken)
    {
        await using var command =
            connection.CreateCommand();

        command.Transaction =
            transaction;

        command.CommandText =
            """
            SELECT
                gift_id,
                gift_fingerprint,
                attempt_id,
                status,
                aplos_transaction_id,
                aplos_response,
                attempt_count,
                created_utc,
                last_attempt_utc,
                completed_utc,
                failure_message
            FROM virtuous_gift_processing
            WHERE gift_id = @giftId;
            """;

        command.Parameters.AddWithValue(
            "giftId",
            giftId);

        await using var reader =
            await command.ExecuteReaderAsync(
                cancellationToken);

        if (!await reader.ReadAsync(
                cancellationToken))
        {
            return null;
        }

        return ReadRecord(reader);
    }

        private static async Task<VirtuousGiftProcessingClaim>
            CreateProcessingRecordAsync(
                NpgsqlConnection connection,
                NpgsqlTransaction transaction,
                long giftId,
                string giftFingerprint,
                CancellationToken cancellationToken)

    {
        var attemptId =
            Guid.NewGuid();

        var now =
            DateTime.UtcNow;

        await using var command =
            connection.CreateCommand();

        command.Transaction =
            transaction;

        command.CommandText =
            """
            INSERT INTO virtuous_gift_processing
            (
                gift_id,
                gift_fingerprint,
                attempt_id,
                status,
                attempt_count,
                created_utc,
                last_attempt_utc
            )
            VALUES
            (
                @giftId,
                @giftFingerprint,
                @attemptId,
                'Processing',
                1,
                @createdUtc,
                @lastAttemptUtc
            );
            """;

        command.Parameters.AddWithValue(
            "giftId",
            giftId);

        command.Parameters.AddWithValue(
            "giftFingerprint",
            giftFingerprint);

        command.Parameters.AddWithValue(
            "attemptId",
            attemptId);

        command.Parameters.AddWithValue(
            "createdUtc",
            now);

        command.Parameters.AddWithValue(
            "lastAttemptUtc",
            now);

        await command.ExecuteNonQueryAsync(
            cancellationToken);

        return new VirtuousGiftProcessingClaim
        {
            Record =
                new VirtuousGiftProcessingRecord
                {
                    GiftId = giftId,
                    GiftFingerprint = giftFingerprint,
                    AttemptId = attemptId,
                    Status =
                        VirtuousGiftProcessingStatus.Processing,
                    AttemptCount = 1,
                    CreatedUtc = now,
                    LastAttemptUtc = now
                },
            ShouldProcess = true
        };
    }

       private static VirtuousGiftProcessingRecord ReadRecord(
    NpgsqlDataReader reader)
{
    return new VirtuousGiftProcessingRecord
    {
        GiftId =
            reader.GetInt64(0),

        GiftFingerprint =
            reader.IsDBNull(1)
                ? null
                : reader.GetString(1),

        AttemptId =
            reader.GetGuid(2),

        Status =
            Enum.Parse<VirtuousGiftProcessingStatus>(
                reader.GetString(3)),

        AplosTransactionId =
            reader.IsDBNull(4)
                ? null
                : reader.GetInt64(4),

        AplosResponse =
            reader.IsDBNull(5)
                ? null
                : reader.GetString(5),

        AttemptCount =
            reader.GetInt32(6),

        CreatedUtc =
            reader.GetDateTime(7),

        LastAttemptUtc =
            reader.IsDBNull(8)
                ? null
                : reader.GetDateTime(8),

        CompletedUtc =
            reader.IsDBNull(9)
                ? null
                : reader.GetDateTime(9),

        FailureMessage =
            reader.IsDBNull(10)
                ? null
                : reader.GetString(10)
    };
}
}
