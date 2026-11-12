namespace VirtuousGateway.Core.Virtuous;

public sealed class AplosTransactionConflictReconciliationException
    : InvalidOperationException
{
    public AplosTransactionConflictReconciliationException()
        : base(
            "The Aplos transaction may have been posted, but reconciliation state could not be saved. Operator investigation is required.")
    {
    }
}
