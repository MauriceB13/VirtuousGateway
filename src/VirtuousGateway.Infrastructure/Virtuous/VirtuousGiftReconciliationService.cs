using VirtuousGateway.Core.Virtuous;

namespace VirtuousGateway.Infrastructure.Virtuous;

public sealed class VirtuousGiftReconciliationService
{
    private readonly IVirtuousGiftProcessingLedger _processingLedger;

    public VirtuousGiftReconciliationService(
        IVirtuousGiftProcessingLedger processingLedger)
    {
        ArgumentNullException.ThrowIfNull(processingLedger);
        _processingLedger = processingLedger;
    }

    public async Task<VirtuousGiftProcessingRecord> ConfirmPostedAsync(
        long giftId,
        Guid attemptId,
        long aplosTransactionId,
        string verifiedBy,
        string verificationEvidence,
        CancellationToken cancellationToken = default)
    {
        ValidateInputs(
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

        if (await _processingLedger.HasAplosTransactionIdConflictAsync(
                giftId,
                aplosTransactionId,
                cancellationToken))
        {
            throw new AplosTransactionIdConflictException();
        }

        return await _processingLedger.ConfirmReconciledCompletedAsync(
            giftId,
            attemptId,
            aplosTransactionId,
            verifiedBy,
            verificationEvidence,
            cancellationToken);
    }

    private static void ValidateInputs(
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
}
