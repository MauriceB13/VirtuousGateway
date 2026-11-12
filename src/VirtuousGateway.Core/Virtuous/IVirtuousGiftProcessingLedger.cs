namespace VirtuousGateway.Core.Virtuous;

public interface IVirtuousGiftProcessingLedger
{
    Task<VirtuousGiftProcessingClaim> BeginProcessingAsync(
        long giftId,
        string giftFingerprint,
        CancellationToken cancellationToken = default);

    Task<VirtuousGiftProcessingClaim> RetryFailedAsync(
        long giftId,
        string giftFingerprint,
        CancellationToken cancellationToken = default);

    Task<VirtuousGiftProcessingRecord> CompleteAsync(
        long giftId,
        Guid attemptId,
        long aplosTransactionId,
        string aplosResponse,
        CancellationToken cancellationToken = default);

    Task<VirtuousGiftProcessingRecord> FailAsync(
        long giftId,
        Guid attemptId,
        string failureMessage,
        CancellationToken cancellationToken = default);

    Task<VirtuousGiftProcessingRecord> RequireReconciliationAsync(
        long giftId,
        Guid attemptId,
        string failureMessage,
        CancellationToken cancellationToken = default);

    Task<bool> HasAplosTransactionIdConflictAsync(
        long giftId,
        long aplosTransactionId,
        CancellationToken cancellationToken = default);

    Task<VirtuousGiftProcessingRecord> ConfirmReconciledCompletedAsync(
        long giftId,
        Guid attemptId,
        long aplosTransactionId,
        string verifiedBy,
        string verificationEvidence,
        CancellationToken cancellationToken = default);

}