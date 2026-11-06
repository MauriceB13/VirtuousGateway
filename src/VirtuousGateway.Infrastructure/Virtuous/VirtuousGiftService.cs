using VirtuousGateway.Core.Aplos;
using VirtuousGateway.Core.Transactions;
using VirtuousGateway.Core.Virtuous;

namespace VirtuousGateway.Infrastructure.Virtuous;

public sealed class VirtuousGiftService
    : IVirtuousGiftService
{
    private readonly IVirtuousGiftTransactionMapper _mapper;
    private readonly IAplosTransactionService _transactionService;
    private readonly IVirtuousGiftProcessingLedger _processingLedger;
    private readonly AplosTransactionResponseParser _responseParser;

    public VirtuousGiftService(
        IVirtuousGiftTransactionMapper mapper,
        IAplosTransactionService transactionService,
        IVirtuousGiftProcessingLedger processingLedger,
        AplosTransactionResponseParser responseParser)
    {
        _mapper = mapper;
        _transactionService = transactionService;
        _processingLedger = processingLedger;
        _responseParser = responseParser;
    }

    public async Task<VirtuousGiftProcessingResult> ProcessGiftAsync(
        VirtuousGift gift,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(gift);

        var giftFingerprint =
        VirtuousGiftFingerprint.Create(
            gift);

        var claim =
            await _processingLedger.BeginProcessingAsync(
            gift.Id,
            giftFingerprint,
            cancellationToken);

        if (!claim.ShouldProcess)
        {
            if (claim.Record.Status ==
                    VirtuousGiftProcessingStatus.Completed
                &&
                claim.Record.AplosTransactionId.HasValue)
            {
                return new VirtuousGiftProcessingResult
                {
                    Status = "processed",
                    GiftId = gift.Id,
                    AplosTransactionId =
                        claim.Record.AplosTransactionId.Value
                };
            }

            throw new VirtuousGiftProcessingStateException(
                gift.Id,
                claim.Record.Status);
        }

        return await ProcessClaimedGiftAsync(
            gift,
            claim,
            cancellationToken);
    }

    public async Task<VirtuousGiftProcessingResult> RetryFailedGiftAsync(
        VirtuousGift gift,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(gift);

          var giftFingerprint =
            VirtuousGiftFingerprint.Create(
                gift);

        var claim =
            await _processingLedger.RetryFailedAsync(
                gift.Id,
                giftFingerprint,
                cancellationToken);

        return await ProcessClaimedGiftAsync(
            gift,
            claim,
            cancellationToken);
    }

    private async Task<VirtuousGiftProcessingResult> ProcessClaimedGiftAsync(
        VirtuousGift gift,
        VirtuousGiftProcessingClaim claim,
        CancellationToken cancellationToken)
    {
        AplosTransactionRequest transaction;

        try
        {
            transaction =
                _mapper.Map(gift);
        }
        catch (Exception)
        {
            await _processingLedger.FailAsync(
                gift.Id,
                claim.Record.AttemptId,
                "The Virtuous gift could not be mapped to an Aplos transaction.",
                cancellationToken);

            throw;
        }

        string rawResult;

        try
        {
            rawResult =
                await _transactionService.CreateTransactionAsync(
                    transaction,
                    cancellationToken);
        }
        catch (AplosPostNotDispatchedException exception)
        {
            await _processingLedger.FailAsync(
                gift.Id,
                claim.Record.AttemptId,
                exception.Message,
                cancellationToken);

            throw;
        }
        catch (AplosPostRejectedException exception)
        {
            await _processingLedger.RequireReconciliationAsync(
                gift.Id,
                claim.Record.AttemptId,
                exception.Message,
                cancellationToken);

            throw;
        }
        catch (AplosPostOutcomeUnknownException exception)
        {
            await _processingLedger.RequireReconciliationAsync(
                gift.Id,
                claim.Record.AttemptId,
                exception.Message,
                cancellationToken);

            throw;
        }
        catch (Exception exception)
        {
            await _processingLedger.RequireReconciliationAsync(
                gift.Id,
                claim.Record.AttemptId,
                "An unexpected error occurred while attempting the Aplos transaction.",
                cancellationToken);

            throw new AplosPostOutcomeUnknownException(
                "The Aplos transaction outcome could not be confirmed.",
                exception);
        }

        VirtuousGiftProcessingResult result;

        try
        {
            result =
                _responseParser.Parse(
                    gift.Id,
                    rawResult);
        }
        catch (Exception exception)
        {
            await _processingLedger.RequireReconciliationAsync(
                gift.Id,
                claim.Record.AttemptId,
                "Aplos returned a successful response, but the transaction ID could not be confirmed.",
                cancellationToken);

            throw new AplosPostOutcomeUnknownException(
                "Aplos returned a successful response, but the transaction ID could not be confirmed.",
                exception);
        }

        await _processingLedger.CompleteAsync(
            gift.Id,
            claim.Record.AttemptId,
            result.AplosTransactionId,
            rawResult,
            cancellationToken);

        return result;
    }
}