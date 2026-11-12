using VirtuousGateway.Core.Transactions;
using VirtuousGateway.Core.Virtuous;
using VirtuousGateway.Infrastructure.Virtuous;
using System.Net;
using VirtuousGateway.Core.Aplos;

namespace VirtuousGateway.Tests.Virtuous;

public sealed class VirtuousGiftServiceTests
{
    [Fact]
    public async Task ReconciliationService_ConfirmPosted_CompletesGift()
    {
        var fixture =
            await CreateReconciliationFixtureAsync();

        var completed =
            await fixture.Service.ConfirmPostedAsync(
                fixture.GiftId,
                fixture.AttemptId,
                70064235,
                "operator-1",
                "Matched Aplos transaction 70064235.");

        Assert.Equal(
            VirtuousGiftProcessingStatus.Completed,
            completed.Status);
        Assert.Equal(
            70064235,
            completed.AplosTransactionId);
    }

    [Theory]
    [InlineData("", "evidence")]
    [InlineData("  ", "evidence")]
    [InlineData("operator-1", "")]
    [InlineData("operator-1", "  ")]
    public async Task ReconciliationService_BlankOperatorOrEvidence_IsRejected(
        string verifiedBy,
        string evidence)
    {
        var fixture =
            await CreateReconciliationFixtureAsync();

        await Assert.ThrowsAsync<ArgumentException>(
            () => fixture.Service.ConfirmPostedAsync(
                fixture.GiftId,
                fixture.AttemptId,
                70064235,
                verifiedBy,
                evidence));

        Assert.Equal(
            VirtuousGiftProcessingStatus.RequiresReconciliation,
            fixture.Ledger.GetRecord(fixture.GiftId).Status);

        await Assert.ThrowsAsync<VirtuousGiftProcessingStateException>(
            () => fixture.Ledger.RetryFailedAsync(
                fixture.GiftId,
                $"test-fingerprint-{fixture.GiftId}"));
    }

    [Fact]
    public async Task ReconciliationService_CallerEvidenceCannotMakeAmbiguousGiftRetryable()
    {
        var fixture =
            await CreateReconciliationFixtureAsync();

        const string callerSuppliedEvidence =
            "I could not find a matching Aplos transaction.";

        await Assert.ThrowsAsync<VirtuousGiftProcessingStateException>(
            () => fixture.Ledger.RetryFailedAsync(
                fixture.GiftId,
                callerSuppliedEvidence));

        Assert.Equal(
            VirtuousGiftProcessingStatus.RequiresReconciliation,
            fixture.Ledger.GetRecord(fixture.GiftId).Status);
    }

    [Fact]
    public async Task ReconciliationService_WrongAttempt_IsRejected()
    {
        var fixture =
            await CreateReconciliationFixtureAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.Service.ConfirmPostedAsync(
                fixture.GiftId,
                Guid.NewGuid(),
                70064235,
                "operator-1",
                "Matched Aplos transaction."));

        Assert.Equal(
            VirtuousGiftProcessingStatus.RequiresReconciliation,
            fixture.Ledger.GetRecord(fixture.GiftId).Status);
    }

    [Fact]
    public async Task ReconciliationService_WrongCurrentStatus_IsRejected()
    {
        var ledger =
            new InMemoryVirtuousGiftProcessingLedger();

        var claim =
            await ledger.BeginProcessingAsync(
                12345,
                "test-fingerprint");

        var service =
            new VirtuousGiftReconciliationService(ledger);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.ConfirmPostedAsync(
                12345,
                claim.Record.AttemptId,
                70064235,
                "operator-1",
                "Matched Aplos transaction."));

        Assert.Equal(
            VirtuousGiftProcessingStatus.Processing,
            ledger.GetRecord(12345).Status);
    }

    [Fact]
    public async Task ReconciliationService_DuplicateDecision_IsRejected()
    {
        var fixture =
            await CreateReconciliationFixtureAsync();

        await fixture.Service.ConfirmPostedAsync(
            fixture.GiftId,
            fixture.AttemptId,
            70064235,
            "operator-1",
            "Matched Aplos transaction.");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.Service.ConfirmPostedAsync(
                fixture.GiftId,
                fixture.AttemptId,
                70064235,
                "operator-2",
                "Repeated decision."));

        Assert.Equal(
            1,
            fixture.Ledger.ReconciliationAuditCount);
    }

    [Fact]
    public async Task ReconciliationService_WrongGift_IsRejected()
    {
        var fixture =
            await CreateReconciliationFixtureAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.Service.ConfirmPostedAsync(
                fixture.GiftId + 1,
                fixture.AttemptId,
                70064235,
                "operator-1",
                "Matched Aplos transaction."));

        Assert.Equal(
            VirtuousGiftProcessingStatus.RequiresReconciliation,
            fixture.Ledger.GetRecord(fixture.GiftId).Status);
    }

    [Fact]
    public async Task ReconciliationService_NonPositiveTransactionId_IsRejected()
    {
        var fixture =
            await CreateReconciliationFixtureAsync();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => fixture.Service.ConfirmPostedAsync(
                fixture.GiftId,
                fixture.AttemptId,
                0,
                "operator-1",
                "Matched Aplos transaction."));

        Assert.Equal(
            VirtuousGiftProcessingStatus.RequiresReconciliation,
            fixture.Ledger.GetRecord(fixture.GiftId).Status);
    }

    [Fact]
    public async Task ReconciliationService_ConflictingTransactionId_IsRejected()
    {
        var ledger =
            new InMemoryVirtuousGiftProcessingLedger();

        var posted =
            await ledger.BeginProcessingAsync(
                10001,
                "posted-gift");

        await ledger.CompleteAsync(
            10001,
            posted.Record.AttemptId,
            70064235,
            """{"id":70064235}""");

        var fixture =
            await CreateReconciliationFixtureAsync(
                ledger,
                giftId: 10002);

        await Assert.ThrowsAsync<AplosTransactionIdConflictException>(
            () => fixture.Service.ConfirmPostedAsync(
                fixture.GiftId,
                fixture.AttemptId,
                70064235,
                "operator-1",
                "Matched transaction."));

        Assert.Equal(
            VirtuousGiftProcessingStatus.RequiresReconciliation,
            ledger.GetRecord(fixture.GiftId).Status);
        Assert.Equal(
            0,
            ledger.ReconciliationAuditCount);
    }

    [Fact]
    public async Task ReconciliationService_ConcurrentConfirmations_OnlyOneCompletesAndAudits()
    {
        var fixture =
            await CreateReconciliationFixtureAsync();
        var checkBarrier =
            new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        var conflictChecks = 0;

        fixture.Ledger.ConflictCheckOverride =
            async (_, _, cancellationToken) =>
            {
                if (Interlocked.Increment(ref conflictChecks) == 2)
                {
                    checkBarrier.TrySetResult(true);
                }

                await checkBarrier.Task.WaitAsync(cancellationToken);
                return false;
            };

        var results =
            await Task.WhenAll(
                Enumerable.Range(0, 2).Select(
                    async index =>
                    {
                        try
                        {
                            await fixture.Service.ConfirmPostedAsync(
                                fixture.GiftId,
                                fixture.AttemptId,
                                70064235,
                                $"operator-{index}",
                                "Matched Aplos transaction.");
                            return true;
                        }
                        catch (InvalidOperationException)
                        {
                            return false;
                        }
                    }));

        Assert.Single(results.Where(result => result));
        Assert.Single(results.Where(result => !result));
        Assert.Equal(
            VirtuousGiftProcessingStatus.Completed,
            fixture.Ledger.GetRecord(fixture.GiftId).Status);
        Assert.Equal(
            1,
            fixture.Ledger.ReconciliationAuditCount);
    }

    [Fact]
    public async Task ReconciliationService_LedgerFailure_DoesNotReturnSuccessfulDecision()
    {
        var fixture =
            await CreateReconciliationFixtureAsync();

        fixture.Ledger.FailNextReconciliation =
            true;

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.Service.ConfirmPostedAsync(
                fixture.GiftId,
                fixture.AttemptId,
                70064235,
                "operator-1",
                "Matched Aplos transaction."));

        Assert.Equal(
            VirtuousGiftProcessingStatus.RequiresReconciliation,
            fixture.Ledger.GetRecord(fixture.GiftId).Status);
        Assert.Equal(
            0,
            fixture.Ledger.ReconciliationAuditCount);
    }

    [Fact]
    public async Task ProcessGiftAsync_CompletionConflictRequiresReconciliationAndDoesNotRetryPost()
    {
        var ledger =
            new InMemoryVirtuousGiftProcessingLedger
            {
                CompleteException =
                    new AplosTransactionIdConflictException()
            };
        var transactionService =
            new StubTransactionService(
                """
                {"data":{"transaction":{"id":70064235}}}
                """);
        var service =
            new VirtuousGiftService(
                new StubMapper(new AplosTransactionRequest()),
                transactionService,
                ledger,
                new AplosTransactionResponseParser());
        var gift =
            new VirtuousGift
            {
                Id = 12345,
                ContactName = "Test",
                GiftDateUtc = DateTime.UtcNow,
                Amount = 1.00m
            };

        await Assert.ThrowsAsync<AplosTransactionIdConflictException>(
            () => service.ProcessGiftAsync(gift));

        var record = ledger.GetRecord(gift.Id);
        Assert.Equal(
            VirtuousGiftProcessingStatus.RequiresReconciliation,
            record.Status);
        Assert.Null(record.AplosTransactionId);
        Assert.Null(record.AplosResponse);
        Assert.DoesNotContain("70064235", record.FailureMessage);
        Assert.Equal(0, ledger.FailCallCount);
        Assert.Equal(1, ledger.RequireReconciliationCallCount);
        Assert.Equal(1, transactionService.CallCount);

        await Assert.ThrowsAsync<VirtuousGiftProcessingStateException>(
            () => service.ProcessGiftAsync(gift));
        Assert.Equal(1, transactionService.CallCount);

        await Assert.ThrowsAsync<VirtuousGiftProcessingStateException>(
            () => service.RetryFailedGiftAsync(gift));
        Assert.Equal(1, transactionService.CallCount);
    }

    [Fact]
    public async Task ProcessGiftAsync_CompletionConflictAndFailedStateWriteRequiresOperatorInvestigation()
    {
        var ledger =
            new InMemoryVirtuousGiftProcessingLedger
            {
                CompleteException =
                    new AplosTransactionIdConflictException(),
                FailNextReconciliationStateWrite = true
            };
        var transactionService =
            new StubTransactionService(
                """{"data":{"transaction":{"id":70064235}}}""");
        var service =
            new VirtuousGiftService(
                new StubMapper(new AplosTransactionRequest()),
                transactionService,
                ledger,
                new AplosTransactionResponseParser());
        var gift =
            new VirtuousGift
            {
                Id = 12345,
                ContactName = "Test",
                GiftDateUtc = DateTime.UtcNow,
                Amount = 1.00m
            };

        var exception =
            await Assert.ThrowsAsync<AplosTransactionConflictReconciliationException>(
                () => service.ProcessGiftAsync(gift));

        Assert.Null(exception.InnerException);
        Assert.Equal(
            VirtuousGiftProcessingStatus.Processing,
            ledger.GetRecord(gift.Id).Status);
        Assert.Equal(0, ledger.FailCallCount);
        Assert.Equal(1, ledger.RequireReconciliationCallCount);
        Assert.Equal(1, transactionService.CallCount);

        await Assert.ThrowsAsync<VirtuousGiftProcessingStateException>(
            () => service.RetryFailedGiftAsync(gift));
        Assert.Equal(1, transactionService.CallCount);
    }

    [Fact]
    public async Task ProcessGiftAsync_UnrelatedCompletionFailureIsNotClassifiedAndCannotBeRetried()
    {
        var ledger =
            new InMemoryVirtuousGiftProcessingLedger
            {
                CompleteException =
                    new InvalidOperationException("Unrelated database failure.")
            };
        var transactionService =
            new StubTransactionService(
                """{"data":{"transaction":{"id":70064235}}}""");
        var service =
            new VirtuousGiftService(
                new StubMapper(new AplosTransactionRequest()),
                transactionService,
                ledger,
                new AplosTransactionResponseParser());
        var gift =
            new VirtuousGift
            {
                Id = 12345,
                ContactName = "Test",
                GiftDateUtc = DateTime.UtcNow,
                Amount = 1.00m
            };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.ProcessGiftAsync(gift));

        Assert.Equal(
            VirtuousGiftProcessingStatus.Processing,
            ledger.GetRecord(gift.Id).Status);
        Assert.Equal(0, ledger.RequireReconciliationCallCount);
        Assert.Equal(0, ledger.FailCallCount);
        Assert.Equal(1, transactionService.CallCount);

        await Assert.ThrowsAsync<VirtuousGiftProcessingStateException>(
            () => service.RetryFailedGiftAsync(gift));
        Assert.Equal(1, transactionService.CallCount);
    }

    [Fact]
    public async Task ProcessGiftAsync_UnexpectedTransactionFailure_RequiresReconciliationAndDoesNotRetryDuplicate()
    {
        var transaction =
            new AplosTransactionRequest
            {
                Note = "Test transaction",
                Date = "2026-08-28"
            };

        var mapper =
            new StubMapper(
                transaction);

        var transactionService =
            new UnexpectedFailureTransactionService();

        var processingLedger =
            new InMemoryVirtuousGiftProcessingLedger();

        var responseParser =
            new AplosTransactionResponseParser();

        var service =
            new VirtuousGiftService(
                mapper,
                transactionService,
                processingLedger,
                responseParser);

        var gift =
            new VirtuousGift
            {
                Id = 12345,
                ContactName = "Ray Test",
                GiftDateUtc =
                    new DateTime(
                        2026,
                        8,
                        28,
                        0,
                        0,
                        0,
                        DateTimeKind.Utc),
                Amount = 1.00m
            };

        var exception =
            await Assert.ThrowsAsync<AplosPostOutcomeUnknownException>(
                () =>
                    service.ProcessGiftAsync(
                        gift));

        Assert.Contains(
            "could not be confirmed",
            exception.Message,
            StringComparison.OrdinalIgnoreCase);

        Assert.Equal(
            1,
            transactionService.CallCount);

        var duplicateException =
            await Assert.ThrowsAsync<VirtuousGiftProcessingStateException>(
                () =>
                    service.ProcessGiftAsync(
                        gift));

        Assert.Equal(
            VirtuousGiftProcessingStatus.RequiresReconciliation,
            duplicateException.Status);

        Assert.Equal(
            gift.Id,
            duplicateException.GiftId);

        Assert.Equal(
            1,
            transactionService.CallCount);
    }

   [Fact]
    public async Task ProcessGiftAsync_NotDispatched_MarksFailedAndDoesNotRetryDuplicate()
    {
        var transaction =
        new AplosTransactionRequest
        {
            Note = "Test transaction",
            Date = "2026-08-28"
        };

    var mapper =
        new StubMapper(
            transaction);

        var transactionService =
            new NotDispatchedTransactionService();

        var processingLedger =
            new InMemoryVirtuousGiftProcessingLedger();

        var responseParser =
            new AplosTransactionResponseParser();

        var service =
            new VirtuousGiftService(
                mapper,
                transactionService,
                processingLedger,
                responseParser);

        var gift =
            new VirtuousGift
            {
                Id = 12345,
                ContactName = "Ray Test",
                GiftDateUtc =
                    new DateTime(
                        2026,
                        8,
                        28,
                        0,
                        0,
                        0,
                        DateTimeKind.Utc),
                Amount = 1.00m
            };

        await Assert.ThrowsAsync<AplosPostNotDispatchedException>(
            () =>
                service.ProcessGiftAsync(
                    gift));

        Assert.Equal(
            1,
            transactionService.CallCount);

        var duplicateException =
            await Assert.ThrowsAsync<VirtuousGiftProcessingStateException>(
                () =>
                    service.ProcessGiftAsync(
                        gift));

        Assert.Equal(
            VirtuousGiftProcessingStatus.Failed,
            duplicateException.Status);

        Assert.Equal(
            gift.Id,
            duplicateException.GiftId);

        Assert.Equal(
            1,
            transactionService.CallCount);
    }

   [Fact]
public async Task ProcessGiftAsync_MappingFailure_MarksFailedAndDoesNotRetryDuplicate()
{
    var mapper =
        new FailingMapper();

    var transactionService =
        new StubTransactionService(
            """{"data":{"transaction":{"id":12345}}}""");

    var processingLedger =
        new InMemoryVirtuousGiftProcessingLedger();

    var responseParser =
        new AplosTransactionResponseParser();

    var service =
        new VirtuousGiftService(
            mapper,
            transactionService,
            processingLedger,
            responseParser);

    var gift =
        new VirtuousGift
        {
            Id = 12345,
            ContactName = "Ray Test",
            GiftDateUtc =
                new DateTime(
                    2026,
                    8,
                    28,
                    0,
                    0,
                    0,
                    DateTimeKind.Utc),
            Amount = 1.00m
        };

    await Assert.ThrowsAsync<InvalidOperationException>(
        () =>
            service.ProcessGiftAsync(
                gift));

    Assert.Equal(
        1,
        mapper.CallCount);

    Assert.Equal(
        0,
        transactionService.CallCount);

    var duplicateException =
        await Assert.ThrowsAsync<VirtuousGiftProcessingStateException>(
            () =>
                service.ProcessGiftAsync(
                    gift));

    Assert.Equal(
        VirtuousGiftProcessingStatus.Failed,
        duplicateException.Status);

    Assert.Equal(
        gift.Id,
        duplicateException.GiftId);

    Assert.Equal(
        1,
        mapper.CallCount);

    Assert.Equal(
        0,
        transactionService.CallCount);
}

   [Theory]
   [InlineData("{\"status\":200,\"data\":{\"transaction\":{}}}")]
   [InlineData("not-json")]
public async Task ProcessGiftAsync_UnconfirmedSuccessfulResponse_RequiresReconciliationAndBlocksRetries(
    string response)
{
    var transaction =
        new AplosTransactionRequest
        {
            Note = "Mapped transaction"
        };

    var mapper =
        new StubMapper(transaction);

    var transactionService =
        new StubTransactionService(
            response);

    var processingLedger =
        new InMemoryVirtuousGiftProcessingLedger();

    var responseParser =
        new AplosTransactionResponseParser();

    var service =
        new VirtuousGiftService(
            mapper,
            transactionService,
            processingLedger,
            responseParser);

    var gift =
        new VirtuousGift
        {
            Id = 12345,
            ContactName = "Ray Test",
            GiftDateUtc =
                new DateTime(
                    2026,
                    8,
                    28,
                    0,
                    0,
                    0,
                    DateTimeKind.Utc),
            Amount = 1.00m
        };

    var firstException =
        await Assert.ThrowsAsync<AplosPostOutcomeUnknownException>(
            () =>
                service.ProcessGiftAsync(
                    gift));

    Assert.Contains(
        "transaction ID could not be confirmed",
        firstException.Message);

    Assert.Equal(
        1,
        transactionService.CallCount);

    var duplicateException =
        await Assert.ThrowsAsync<VirtuousGiftProcessingStateException>(
            () =>
                service.ProcessGiftAsync(
                    gift));

    Assert.Equal(
        VirtuousGiftProcessingStatus.RequiresReconciliation,
        duplicateException.Status);

    Assert.Equal(
        gift.Id,
        duplicateException.GiftId);

    Assert.Equal(
        1,
        transactionService.CallCount);

    var retryException =
        await Assert.ThrowsAsync<VirtuousGiftProcessingStateException>(
            () =>
                service.RetryFailedGiftAsync(
                    gift));

    Assert.Equal(
        VirtuousGiftProcessingStatus.RequiresReconciliation,
        retryException.Status);

    Assert.Equal(
        1,
        transactionService.CallCount);

    Assert.Equal(
        1,
        mapper.CallCount);
}

   [Fact]
public async Task ProcessGiftAsync_UnknownPostOutcome_RequiresReconciliationAndDoesNotRetryDuplicate()
{
    var transaction =
        new AplosTransactionRequest
        {
            Note = "Mapped transaction"
        };

    var mapper =
        new StubMapper(transaction);

    var transactionService =
        new UnknownOutcomeTransactionService();

    var processingLedger =
        new InMemoryVirtuousGiftProcessingLedger();

    var responseParser =
        new AplosTransactionResponseParser();

    var service =
        new VirtuousGiftService(
            mapper,
            transactionService,
            processingLedger,
            responseParser);

    var gift =
        new VirtuousGift
        {
            Id = 12345,
            ContactName = "Ray Test",
            GiftDateUtc =
                new DateTime(
                    2026,
                    8,
                    28,
                    0,
                    0,
                    0,
                    DateTimeKind.Utc),
            Amount = 1.00m
        };

    await Assert.ThrowsAsync<AplosPostOutcomeUnknownException>(
        () =>
            service.ProcessGiftAsync(
                gift));

    Assert.Equal(
        1,
        transactionService.CallCount);

    var duplicateException =
        await Assert.ThrowsAsync<VirtuousGiftProcessingStateException>(
            () =>
                service.ProcessGiftAsync(
                    gift));

    Assert.Equal(
        VirtuousGiftProcessingStatus.RequiresReconciliation,
        duplicateException.Status);

    Assert.Equal(
        gift.Id,
        duplicateException.GiftId);

    Assert.Equal(
        1,
        transactionService.CallCount);

    var retryException =
        await Assert.ThrowsAsync<VirtuousGiftProcessingStateException>(
            () =>
                service.RetryFailedGiftAsync(
                    gift));

    Assert.Equal(
        VirtuousGiftProcessingStatus.RequiresReconciliation,
        retryException.Status);

    Assert.Equal(
        1,
        transactionService.CallCount);

    Assert.Equal(
        1,
        mapper.CallCount);
}

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.MovedPermanently)]
    [InlineData(HttpStatusCode.Redirect)]
    [InlineData(HttpStatusCode.SeeOther)]
    [InlineData(HttpStatusCode.TemporaryRedirect)]
    [InlineData(HttpStatusCode.PermanentRedirect)]
    [InlineData(HttpStatusCode.RequestTimeout)]
    [InlineData(HttpStatusCode.TooManyRequests)]
public async Task ProcessGiftAsync_RejectedPost_RequiresReconciliationAndBlocksRetries(
    HttpStatusCode statusCode)
{
    var transaction =
        new AplosTransactionRequest
        {
            Note = "Mapped transaction"
        };

    var mapper =
        new StubMapper(transaction);

    var transactionService =
        new RejectingTransactionService(statusCode);

    var processingLedger =
        new InMemoryVirtuousGiftProcessingLedger();

    var responseParser =
        new AplosTransactionResponseParser();

    var service =
        new VirtuousGiftService(
            mapper,
            transactionService,
            processingLedger,
            responseParser);

    var gift =
        new VirtuousGift
        {
            Id = 12345,
            ContactName = "Ray Test",
            GiftDateUtc =
                new DateTime(
                    2026,
                    8,
                    28,
                    0,
                    0,
                    0,
                    DateTimeKind.Utc),
            Amount = 1.00m
        };

    var firstException =
        await Assert.ThrowsAsync<AplosPostRejectedException>(
            () =>
                service.ProcessGiftAsync(
                    gift));

    Assert.Equal(
        statusCode,
        firstException.StatusCode);

    Assert.Equal(
        1,
        transactionService.CallCount);

    var duplicateException =
        await Assert.ThrowsAsync<VirtuousGiftProcessingStateException>(
            () =>
                service.ProcessGiftAsync(
                    gift));

    Assert.Equal(
        VirtuousGiftProcessingStatus.RequiresReconciliation,
        duplicateException.Status);

    Assert.Equal(
        gift.Id,
        duplicateException.GiftId);

    Assert.Equal(
        1,
        transactionService.CallCount);

    var retryException =
        await Assert.ThrowsAsync<VirtuousGiftProcessingStateException>(
            () =>
                service.RetryFailedGiftAsync(
                    gift));

    Assert.Equal(
        VirtuousGiftProcessingStatus.RequiresReconciliation,
        retryException.Status);

    Assert.Equal(
        1,
        transactionService.CallCount);

    Assert.Equal(
        1,
        mapper.CallCount);
}


    [Fact]
    public async Task ProcessGiftAsync_MapsGift_AndCreatesTransaction()
    {
        var expectedTransaction =
            new AplosTransactionRequest
            {
                Note = "Mapped transaction"
            };

        var mapper =
            new StubMapper(expectedTransaction);

        var transactionService =
            new StubTransactionService(
                """
                {
                  "status": 200,
                  "data": {
                    "transaction": {
                      "id": 70064235
                    }
                  }
                }
                """);

        var processingLedger =
            new InMemoryVirtuousGiftProcessingLedger();

        var responseParser =
            new AplosTransactionResponseParser();

        var service =
        new VirtuousGiftService(
            mapper,
            transactionService,
            processingLedger,
            responseParser);

        var gift =
            new VirtuousGift
            {
                Id = 12345,
                ContactName = "Ray Test",
                GiftDateUtc =
                    new DateTime(
                        2026,
                        8,
                        28,
                        0,
                        0,
                        0,
                        DateTimeKind.Utc),
                Amount = 1.00m
            };

        var result =
            await service.ProcessGiftAsync(gift);

        Assert.Equal(
            "processed",
            result.Status);

        Assert.Equal(
            12345,
            result.GiftId);

        Assert.Equal(
            70064235,
            result.AplosTransactionId);

        Assert.Same(
            gift,
            mapper.LastGift);

        Assert.Same(
            expectedTransaction,
            transactionService.LastRequest);

        Assert.Equal(
            1,
            transactionService.CallCount);
    }

    [Fact]
    public async Task ProcessGiftAsync_SameGiftIdTwice_CreatesTransactionOnlyOnce()
    {
        var expectedTransaction =
            new AplosTransactionRequest
            {
                Note = "Mapped transaction"
            };

        var mapper =
            new StubMapper(expectedTransaction);

        var transactionService =
            new StubTransactionService(
                """
                {
                  "status": 200,
                  "data": {
                    "transaction": {
                      "id": 70064235
                    }
                  }
                }
                """);

        var processingLedger =
    new InMemoryVirtuousGiftProcessingLedger();

        var responseParser =
            new AplosTransactionResponseParser();

        var service =
            new VirtuousGiftService(
                mapper,
                transactionService,
                processingLedger,
                responseParser);

        var firstGift =
            new VirtuousGift
            {
                Id = 12345,
                ContactName = "Ray Test",
                GiftDateUtc =
                    new DateTime(
                        2026,
                        8,
                        28,
                        0,
                        0,
                        0,
                        DateTimeKind.Utc),
                Amount = 1.00m
            };

        var duplicateGift =
            new VirtuousGift
            {
                Id = 12345,
                ContactName = "Ray Test",
                GiftDateUtc =
                    new DateTime(
                        2026,
                        8,
                        28,
                        0,
                        0,
                        0,
                        DateTimeKind.Utc),
                Amount = 1.00m
            };

        var firstResult =
            await service.ProcessGiftAsync(firstGift);

        var secondResult =
            await service.ProcessGiftAsync(duplicateGift);

        Assert.Equal(
            firstResult.Status,
            secondResult.Status);

        Assert.Equal(
            firstResult.GiftId,
            secondResult.GiftId);

        Assert.Equal(
            firstResult.AplosTransactionId,
            secondResult.AplosTransactionId);

        Assert.Equal(
            1,
            transactionService.CallCount);

        Assert.Equal(
            1,
            mapper.CallCount);
    }

    [Fact]
    public async Task ProcessGiftAsync_NullGift_ThrowsArgumentNullException()
    {
        var mapper =
            new StubMapper(
                new AplosTransactionRequest());

        var transactionService =
            new StubTransactionService(
                """
                {
                  "status": 200,
                  "data": {
                    "transaction": {
                      "id": 70064235
                    }
                  }
                }
                """);

        var processingLedger =
    new InMemoryVirtuousGiftProcessingLedger();

        var responseParser =
            new AplosTransactionResponseParser();

        var service =
            new VirtuousGiftService(
                mapper,
                transactionService,
                processingLedger,
                responseParser);

        await Assert.ThrowsAsync<ArgumentNullException>(
            () =>
                service.ProcessGiftAsync(
                    null!));
    }

        [Fact]
public async Task RetryFailedGiftAsync_FailedGift_RetriesAndCompletes()
{
    var transaction =
        new AplosTransactionRequest
        {
            Note = "Mapped transaction"
        };

    var mapper =
        new StubMapper(
            transaction);

    var transactionService =
        new NotDispatchedThenSuccessfulTransactionService();

    var processingLedger =
        new InMemoryVirtuousGiftProcessingLedger();

    var responseParser =
        new AplosTransactionResponseParser();

    var service =
        new VirtuousGiftService(
            mapper,
            transactionService,
            processingLedger,
            responseParser);

    var gift =
        new VirtuousGift
        {
            Id = 12345,
            ContactName = "Ray Test",
            GiftDateUtc =
                new DateTime(
                    2026,
                    8,
                    28,
                    0,
                    0,
                    0,
                    DateTimeKind.Utc),
            Amount = 1.00m
        };

    await Assert.ThrowsAsync<AplosPostNotDispatchedException>(
        () =>
            service.ProcessGiftAsync(
                gift));

    var result =
        await service.RetryFailedGiftAsync(
            gift);

    Assert.Equal(
        "processed",
        result.Status);

    Assert.Equal(
        gift.Id,
        result.GiftId);

    Assert.Equal(
        70064235,
        result.AplosTransactionId);

    Assert.Equal(
        2,
        transactionService.CallCount);

    Assert.Equal(
        2,
        mapper.CallCount);

    var duplicateResult =
        await service.ProcessGiftAsync(
            gift);

    Assert.Equal(
        result.AplosTransactionId,
        duplicateResult.AplosTransactionId);

    Assert.Equal(
        2,
        transactionService.CallCount);

    Assert.Equal(
        2,
        mapper.CallCount);
}

    [Fact]
    public async Task RetryFailedGiftAsync_ChangedGift_IsRejectedBeforeAplosPost()
{
    var transaction =
        new AplosTransactionRequest
        {
            Note = "Mapped transaction"
        };

    var mapper =
        new StubMapper(
            transaction);

    var transactionService =
        new NotDispatchedThenSuccessfulTransactionService();

    var processingLedger =
        new InMemoryVirtuousGiftProcessingLedger();

    var responseParser =
        new AplosTransactionResponseParser();

    var service =
        new VirtuousGiftService(
            mapper,
            transactionService,
            processingLedger,
            responseParser);

    var originalGift =
        new VirtuousGift
        {
            Id = 12345,
            ContactName = "Ray Test",
            GiftDateUtc =
                new DateTime(
                    2026,
                    8,
                    28,
                    0,
                    0,
                    0,
                    DateTimeKind.Utc),
            Amount = 1.00m
        };

    await Assert.ThrowsAsync<AplosPostNotDispatchedException>(
        () =>
            service.ProcessGiftAsync(
                originalGift));

    Assert.Equal(
        1,
        transactionService.CallCount);

    Assert.Equal(
        1,
        mapper.CallCount);

    var changedGift =
        new VirtuousGift
        {
            Id = originalGift.Id,
            ContactName = originalGift.ContactName,
            GiftDateUtc = originalGift.GiftDateUtc,
            Amount = 2.00m,
            Project = originalGift.Project,
            ProjectCode = originalGift.ProjectCode,
            Segment = originalGift.Segment
        };

    var exception =
        await Assert.ThrowsAsync<VirtuousGiftFingerprintMismatchException>(
            () =>
                service.RetryFailedGiftAsync(
                    changedGift));

    Assert.Equal(
        originalGift.Id,
        exception.GiftId);

    Assert.Equal(
        1,
        transactionService.CallCount);

    Assert.Equal(
        1,
        mapper.CallCount);
}

    [Fact]
    public async Task ProcessGiftAsync_ExistingGiftWithChangedData_IsRejected()
{
    var transaction =
        new AplosTransactionRequest
        {
            Note = "Mapped transaction"
        };

    var mapper =
        new StubMapper(
            transaction);

    var transactionService =
        new StubTransactionService(
            """
            {
              "status": 200,
              "data": {
                "transaction": {
                  "id": 70064235
                }
              }
            }
            """);

    var processingLedger =
        new InMemoryVirtuousGiftProcessingLedger();

    var responseParser =
        new AplosTransactionResponseParser();

    var service =
        new VirtuousGiftService(
            mapper,
            transactionService,
            processingLedger,
            responseParser);

    var originalGift =
        new VirtuousGift
        {
            Id = 12345,
            ContactName = "Ray Test",
            GiftDateUtc =
                new DateTime(
                    2026,
                    8,
                    28,
                    0,
                    0,
                    0,
                    DateTimeKind.Utc),
            Amount = 1.00m
        };

    var result =
        await service.ProcessGiftAsync(
            originalGift);

    Assert.Equal(
        70064235,
        result.AplosTransactionId);

    Assert.Equal(
        1,
        transactionService.CallCount);

    Assert.Equal(
        1,
        mapper.CallCount);

    var changedGift =
        new VirtuousGift
        {
            Id = originalGift.Id,
            ContactName = originalGift.ContactName,
            GiftDateUtc = originalGift.GiftDateUtc,
            Amount = 2.00m,
            Project = originalGift.Project,
            ProjectCode = originalGift.ProjectCode,
            Segment = originalGift.Segment
        };

    var exception =
        await Assert.ThrowsAsync<VirtuousGiftFingerprintMismatchException>(
            () =>
                service.ProcessGiftAsync(
                    changedGift));

    Assert.Equal(
        originalGift.Id,
        exception.GiftId);

    Assert.Equal(
        1,
        transactionService.CallCount);

    Assert.Equal(
        1,
        mapper.CallCount);
}
    
        [Fact]
        public async Task RetryFailedGiftAsync_RetryNotDispatched_RemainsFailed()
        {
            var transaction =
                new AplosTransactionRequest
                {
                    Note = "Mapped transaction"
                };

            var mapper =
                new StubMapper(
                    transaction);

            var transactionService =
                new NotDispatchedTransactionService();

            var processingLedger =
                new InMemoryVirtuousGiftProcessingLedger();

            var responseParser =
                new AplosTransactionResponseParser();

            var service =
                new VirtuousGiftService(
                    mapper,
                    transactionService,
                    processingLedger,
                    responseParser);

            var gift =
                new VirtuousGift
                {
                    Id = 12345,
                    ContactName = "Ray Test",
                    GiftDateUtc =
                        new DateTime(
                            2026,
                            8,
                            28,
                            0,
                            0,
                            0,
                            DateTimeKind.Utc),
                    Amount = 1.00m
                };

            await Assert.ThrowsAsync<AplosPostNotDispatchedException>(
                () =>
                    service.ProcessGiftAsync(
                        gift));

            await Assert.ThrowsAsync<AplosPostNotDispatchedException>(
                () =>
                    service.RetryFailedGiftAsync(
                        gift));

            Assert.Equal(
                2,
                transactionService.CallCount);

            Assert.Equal(
                2,
                mapper.CallCount);

            var stateException =
                await Assert.ThrowsAsync<VirtuousGiftProcessingStateException>(
                    () =>
                        service.ProcessGiftAsync(
                            gift));

            Assert.Equal(
                gift.Id,
                stateException.GiftId);

            Assert.Equal(
                VirtuousGiftProcessingStatus.Failed,
                stateException.Status);

            Assert.Equal(
                2,
                transactionService.CallCount);

            Assert.Equal(
                2,
                mapper.CallCount);
        }

        [Fact]
        public async Task RetryFailedGiftAsync_UnknownRetryOutcome_RequiresReconciliationAndBlocksFurtherRetry()
        {
            var transaction =
                new AplosTransactionRequest
                {
                    Note = "Mapped transaction"
                };

            var mapper =
                new StubMapper(
                    transaction);

            var transactionService =
                new NotDispatchedThenUnknownOutcomeTransactionService();

            var processingLedger =
                new InMemoryVirtuousGiftProcessingLedger();

            var responseParser =
                new AplosTransactionResponseParser();

            var service =
                new VirtuousGiftService(
                    mapper,
                    transactionService,
                    processingLedger,
                    responseParser);

            var gift =
                new VirtuousGift
                {
                    Id = 12345,
                    ContactName = "Ray Test",
                    GiftDateUtc =
                        new DateTime(
                            2026,
                            8,
                            28,
                            0,
                            0,
                            0,
                            DateTimeKind.Utc),
                    Amount = 1.00m
                };

            await Assert.ThrowsAsync<AplosPostNotDispatchedException>(
                () =>
                    service.ProcessGiftAsync(
                        gift));

            await Assert.ThrowsAsync<AplosPostOutcomeUnknownException>(
                () =>
                    service.RetryFailedGiftAsync(
                        gift));

            Assert.Equal(
                2,
                transactionService.CallCount);

            Assert.Equal(
                2,
                mapper.CallCount);

            var retryException =
                await Assert.ThrowsAsync<VirtuousGiftProcessingStateException>(
                    () =>
                        service.RetryFailedGiftAsync(
                            gift));

            Assert.Equal(
                gift.Id,
                retryException.GiftId);

            Assert.Equal(
                VirtuousGiftProcessingStatus.RequiresReconciliation,
                retryException.Status);

            Assert.Equal(
                2,
                transactionService.CallCount);

            Assert.Equal(
                2,
                mapper.CallCount);
        }

        private sealed class NotDispatchedThenUnknownOutcomeTransactionService
            : IAplosTransactionService
        {
            public int CallCount { get; private set; }

            public Task<string> CreateTransactionAsync(
                AplosTransactionRequest request,
                CancellationToken cancellationToken = default)
            {
                CallCount++;

                if (CallCount == 1)
                {
                    return Task.FromException<string>(
                        new AplosPostNotDispatchedException(
                            "The Aplos transaction request was not dispatched."));
                }

                return Task.FromException<string>(
                    new AplosPostOutcomeUnknownException(
                        "The Aplos transaction outcome is unknown."));
            }
        }
    
    private static async Task<(
        InMemoryVirtuousGiftProcessingLedger Ledger,
        VirtuousGiftReconciliationService Service,
        long GiftId,
        Guid AttemptId)> CreateReconciliationFixtureAsync(
            InMemoryVirtuousGiftProcessingLedger? ledger = null,
            long giftId = 12345)
    {
        ledger ??=
            new InMemoryVirtuousGiftProcessingLedger();

        var claim =
            await ledger.BeginProcessingAsync(
                giftId,
                $"test-fingerprint-{giftId}");

        await ledger.RequireReconciliationAsync(
            giftId,
            claim.Record.AttemptId,
            "Aplos transaction outcome is unknown.");

        return (
            ledger,
            new VirtuousGiftReconciliationService(ledger),
            giftId,
            claim.Record.AttemptId);
    }

    private sealed class StubMapper
        : IVirtuousGiftTransactionMapper
    {
        private readonly AplosTransactionRequest _transaction;

        public StubMapper(
            AplosTransactionRequest transaction)
        {
            _transaction = transaction;
        }

        public VirtuousGift? LastGift { get; private set; }

        public int CallCount { get; private set; }

        public AplosTransactionRequest Map(
            VirtuousGift gift)
        {
            LastGift = gift;
            CallCount++;

            return _transaction;
        }
    }

    private sealed class StubTransactionService
        : IAplosTransactionService
    {
        private readonly string _result;

        public StubTransactionService(
            string result)
        {
            _result = result;
        }

        public AplosTransactionRequest? LastRequest { get; private set; }

        public int CallCount { get; private set; }

        public Task<string> CreateTransactionAsync(
            AplosTransactionRequest request,
            CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            CallCount++;

            return Task.FromResult(
                _result);
        }
    }

    private sealed class FailingThenSuccessfulTransactionService
        : IAplosTransactionService
    {
        public int CallCount { get; private set; }

        public Task<string> CreateTransactionAsync(
            AplosTransactionRequest request,
            CancellationToken cancellationToken = default)
        {
            CallCount++;

            if (CallCount == 1)
            {
                throw new InvalidOperationException(
                    "Simulated Aplos failure.");
            }

            return Task.FromResult(
                """
                {
                  "status": 200,
                  "data": {
                    "transaction": {
                      "id": 70064235
                    }
                  }
                }
                """);
        }
    }

private sealed class NotDispatchedTransactionService
    : IAplosTransactionService
    {
        public int CallCount { get; private set; }

        public Task<string> CreateTransactionAsync(
            AplosTransactionRequest request,
            CancellationToken cancellationToken = default)
        {
            CallCount++;

            return Task.FromException<string>(
                new AplosPostNotDispatchedException(
                    "The Aplos transaction request was not dispatched."));
        }
    }

private sealed class RejectingTransactionService
    : IAplosTransactionService
{
    private readonly HttpStatusCode _statusCode;

    public RejectingTransactionService(
        HttpStatusCode statusCode)
    {
        _statusCode = statusCode;
    }

    public int CallCount { get; private set; }

    public Task<string> CreateTransactionAsync(
        AplosTransactionRequest request,
        CancellationToken cancellationToken = default)
    {
        CallCount++;

        return Task.FromException<string>(
            new AplosPostRejectedException(
                _statusCode));
    }
}

    private sealed class UnknownOutcomeTransactionService
    : IAplosTransactionService
{
    public int CallCount { get; private set; }

    public Task<string> CreateTransactionAsync(
        AplosTransactionRequest request,
        CancellationToken cancellationToken = default)
    {
        CallCount++;

        return Task.FromException<string>(
            new AplosPostOutcomeUnknownException(
                "The Aplos transaction outcome is unknown."));
    }
}

    private sealed class FailingMapper
    : IVirtuousGiftTransactionMapper
{
    public int CallCount { get; private set; }

    public AplosTransactionRequest Map(
        VirtuousGift gift)
    {
        CallCount++;

        throw new InvalidOperationException(
            "Simulated mapping failure.");
    }
}

    private sealed class InMemoryVirtuousGiftProcessingLedger
        : IVirtuousGiftProcessingLedger

{
    private readonly object _confirmationLock = new();
    private readonly Dictionary<
        long,
        VirtuousGiftProcessingRecord> _records = new();

    public bool FailNextReconciliation { get; set; }

    public bool FailNextReconciliationStateWrite { get; set; }

    public Exception? CompleteException { get; set; }

    public int FailCallCount { get; private set; }

    public int RequireReconciliationCallCount { get; private set; }

    public int ReconciliationAuditCount { get; private set; }

    public Func<long, long, CancellationToken, Task<bool>>?
        ConflictCheckOverride { get; set; }

    public VirtuousGiftProcessingRecord GetRecord(long giftId) =>
        _records[giftId];

    public Task<VirtuousGiftProcessingClaim> BeginProcessingAsync(
    long giftId,
    string giftFingerprint,
    CancellationToken cancellationToken = default)
    {
        if (_records.TryGetValue(
        giftId,
        out var existingRecord))
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

            return Task.FromResult(
                new VirtuousGiftProcessingClaim
                {
                    Record = existingRecord,
                    ShouldProcess = false
                });
        }

        var now =
            DateTime.UtcNow;

        var record =
            new VirtuousGiftProcessingRecord
            {
                GiftId = giftId,
                GiftFingerprint = giftFingerprint,
                AttemptId = Guid.NewGuid(),
                Status =
                    VirtuousGiftProcessingStatus.Processing,
                AttemptCount = 1,
                CreatedUtc = now,
                LastAttemptUtc = now
            };

        _records[giftId] =
            record;

        return Task.FromResult(
            new VirtuousGiftProcessingClaim
            {
                Record = record,
                ShouldProcess = true
            });
    }

    public Task<VirtuousGiftProcessingClaim> RetryFailedAsync(
    long giftId,
    string giftFingerprint,
    CancellationToken cancellationToken = default)
    {
        if (!_records.TryGetValue(
                giftId,
                out var current))
        {
            throw new InvalidOperationException(
                $"Virtuous gift {giftId} does not have an existing processing record.");
        }

        if (current.Status !=
            VirtuousGiftProcessingStatus.Failed)
        {
            throw new VirtuousGiftProcessingStateException(
                giftId,
                current.Status);
        }

        if (string.IsNullOrWhiteSpace(
                current.GiftFingerprint)
            || !string.Equals(
                current.GiftFingerprint,
                giftFingerprint,
                StringComparison.Ordinal))
        {
            throw new VirtuousGiftFingerprintMismatchException(
                giftId);
        }

        var now =
            DateTime.UtcNow;

        var retry =
            new VirtuousGiftProcessingRecord
            {
                GiftId =
                    current.GiftId,
                GiftFingerprint =
                    current.GiftFingerprint,
                AttemptId =
                    Guid.NewGuid(),
                Status =
                    VirtuousGiftProcessingStatus.Processing,
                AttemptCount =
                    current.AttemptCount + 1,
                CreatedUtc =
                    current.CreatedUtc,
                LastAttemptUtc =
                    now
            };

        _records[giftId] =
            retry;

        return Task.FromResult(
            new VirtuousGiftProcessingClaim
            {
                Record = retry,
                ShouldProcess = true
            });
    }

    public Task<VirtuousGiftProcessingRecord> CompleteAsync(
        long giftId,
        Guid attemptId,
        long aplosTransactionId,
        string aplosResponse,
        CancellationToken cancellationToken = default)
    {
        if (CompleteException is not null)
        {
            throw CompleteException;
        }

        var current =
            GetActiveAttempt(
                giftId,
                attemptId);

        var completed =
            new VirtuousGiftProcessingRecord
            {
                GiftId = current.GiftId,
                GiftFingerprint = current.GiftFingerprint,
                AttemptId = current.AttemptId,
                Status =
                    VirtuousGiftProcessingStatus.Completed,
                AplosTransactionId =
                    aplosTransactionId,
                AplosResponse =
                    aplosResponse,
                AttemptCount =
                    current.AttemptCount,
                CreatedUtc =
                    current.CreatedUtc,
                LastAttemptUtc =
                    current.LastAttemptUtc,
                CompletedUtc =
                    DateTime.UtcNow
            };

        _records[giftId] =
            completed;

        return Task.FromResult(
            completed);
    }

    public Task<VirtuousGiftProcessingRecord> FailAsync(
        long giftId,
        Guid attemptId,
        string failureMessage,
        CancellationToken cancellationToken = default)
    {
        FailCallCount++;

        var current =
            GetActiveAttempt(
                giftId,
                attemptId);

        var failed =
            new VirtuousGiftProcessingRecord
            {
                GiftId = current.GiftId,
                GiftFingerprint = current.GiftFingerprint,
                AttemptId = current.AttemptId,
                Status =
                    VirtuousGiftProcessingStatus.Failed,
                AttemptCount =
                    current.AttemptCount,
                CreatedUtc =
                    current.CreatedUtc,
                LastAttemptUtc =
                    current.LastAttemptUtc,
                FailureMessage =
                    failureMessage
            };

        _records[giftId] =
            failed;

        return Task.FromResult(
            failed);
    }

    public Task<VirtuousGiftProcessingRecord> RequireReconciliationAsync(
        long giftId,
        Guid attemptId,
        string failureMessage,
        CancellationToken cancellationToken = default)
    {
        RequireReconciliationCallCount++;

        if (FailNextReconciliationStateWrite)
        {
            FailNextReconciliationStateWrite = false;
            throw new InvalidOperationException(
                "Simulated reconciliation-state persistence failure.");
        }

        var current =
            GetActiveAttempt(
                giftId,
                attemptId);

        var reconciliation =
            new VirtuousGiftProcessingRecord
            {
                GiftId = current.GiftId,
                GiftFingerprint = current.GiftFingerprint,
                AttemptId = current.AttemptId,
                Status =
                    VirtuousGiftProcessingStatus.RequiresReconciliation,
                AttemptCount =
                    current.AttemptCount,
                CreatedUtc =
                    current.CreatedUtc,
                LastAttemptUtc =
                    current.LastAttemptUtc,
                FailureMessage =
                    failureMessage
            };

        _records[giftId] =
            reconciliation;

        return Task.FromResult(
            reconciliation);
    }

    public Task<bool> HasAplosTransactionIdConflictAsync(
        long giftId,
        long aplosTransactionId,
        CancellationToken cancellationToken = default)
    {
        if (ConflictCheckOverride is not null)
        {
            return ConflictCheckOverride(
                giftId,
                aplosTransactionId,
                cancellationToken);
        }

        return Task.FromResult(
            _records.Values.Any(
                record =>
                    record.GiftId != giftId
                    && record.AplosTransactionId == aplosTransactionId));
    }

    public Task<VirtuousGiftProcessingRecord> ConfirmReconciledCompletedAsync(
        long giftId,
        Guid attemptId,
        long aplosTransactionId,
        string verifiedBy,
        string verificationEvidence,
        CancellationToken cancellationToken = default)
    {
        lock (_confirmationLock)
        {
            ValidateReconciliation(
                giftId,
                attemptId,
                verifiedBy,
                verificationEvidence);

            if (aplosTransactionId <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(aplosTransactionId));
            }

            if (FailNextReconciliation)
            {
                FailNextReconciliation = false;
                throw new InvalidOperationException(
                    "Simulated audit persistence failure.");
            }

            var current =
                GetReconciliationAttempt(
                    giftId,
                    attemptId);

            var completed =
                new VirtuousGiftProcessingRecord
                {
                    GiftId = current.GiftId,
                    GiftFingerprint = current.GiftFingerprint,
                    AttemptId = current.AttemptId,
                    Status = VirtuousGiftProcessingStatus.Completed,
                    AplosTransactionId = aplosTransactionId,
                    AttemptCount = current.AttemptCount,
                    CreatedUtc = current.CreatedUtc,
                    LastAttemptUtc = current.LastAttemptUtc,
                    CompletedUtc = DateTime.UtcNow
                };

            _records[giftId] =
                completed;
            ReconciliationAuditCount++;

            return Task.FromResult(
                completed);
        }
    }

    private static void ValidateReconciliation(
        long giftId,
        Guid attemptId,
        string verifiedBy,
        string verificationEvidence)
    {
        if (giftId <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(giftId));
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

    private VirtuousGiftProcessingRecord GetReconciliationAttempt(
        long giftId,
        Guid attemptId)
    {
        if (!_records.TryGetValue(
                giftId,
                out var record)
            || record.AttemptId != attemptId
            || record.Status !=
                VirtuousGiftProcessingStatus.RequiresReconciliation)
        {
            throw new InvalidOperationException(
                "The gift is not awaiting reconciliation for this attempt.");
        }

        return record;
    }

    private VirtuousGiftProcessingRecord GetActiveAttempt(
        long giftId,
        Guid attemptId)
    {
        if (!_records.TryGetValue(
                giftId,
                out var record)
            ||
            record.AttemptId != attemptId
            ||
            record.Status !=
                VirtuousGiftProcessingStatus.Processing)
        {
            throw new InvalidOperationException(
                "The gift processing attempt is no longer active.");
        }

        return record;
    }
}

private sealed class UnexpectedFailureTransactionService
    : IAplosTransactionService
{
    public int CallCount { get; private set; }

    public Task<string> CreateTransactionAsync(
        AplosTransactionRequest request,
        CancellationToken cancellationToken = default)
    {
        CallCount++;

        return Task.FromException<string>(
            new InvalidOperationException(
                "Simulated unexpected transaction failure."));
    }
}

private sealed class NotDispatchedThenSuccessfulTransactionService
    : IAplosTransactionService
{
    public int CallCount { get; private set; }

    public Task<string> CreateTransactionAsync(
        AplosTransactionRequest request,
        CancellationToken cancellationToken = default)
    {
        CallCount++;

        if (CallCount == 1)
        {
            return Task.FromException<string>(
                new AplosPostNotDispatchedException(
                    "The Aplos transaction request was not dispatched."));
        }

        return Task.FromResult(
            """
            {
              "status": 200,
              "data": {
                "transaction": {
                  "id": 70064235
                }
              }
            }
            """);
    }
}

}
