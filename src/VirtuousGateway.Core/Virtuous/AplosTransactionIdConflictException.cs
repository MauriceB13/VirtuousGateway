namespace VirtuousGateway.Core.Virtuous;

public sealed class AplosTransactionIdConflictException : InvalidOperationException
{
    public AplosTransactionIdConflictException(long aplosTransactionId)
        : base(
            $"Aplos transaction ID {aplosTransactionId} is already assigned to another gift.")
    {
        AplosTransactionId =
            aplosTransactionId;
    }

    public AplosTransactionIdConflictException()
        : base(
            "An Aplos transaction ID conflict requires reconciliation.")
    {
    }

    public long AplosTransactionId { get; }
}
