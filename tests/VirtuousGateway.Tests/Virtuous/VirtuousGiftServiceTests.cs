using VirtuousGateway.Core.Transactions;
using VirtuousGateway.Core.Virtuous;
using VirtuousGateway.Infrastructure.Virtuous;
using System.Net;
using VirtuousGateway.Core.Aplos;

namespace VirtuousGateway.Tests.Virtuous;

public sealed class VirtuousGiftServiceTests
{
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
    private readonly Dictionary<
        long,
        VirtuousGiftProcessingRecord> _records = new();

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
