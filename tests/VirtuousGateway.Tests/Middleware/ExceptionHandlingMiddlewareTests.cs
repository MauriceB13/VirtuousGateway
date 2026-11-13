using System.Net;
using System.Text.Json;
using VirtuousGateway.Api.Middleware;
using VirtuousGateway.Core.Virtuous;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace VirtuousGateway.Tests.Middleware;

public sealed class ExceptionHandlingMiddlewareTests
{
    [Fact]
    public async Task InvokeAsync_ProcessingStateException_ReturnsConflict()
    {
        const long giftId = 38118;

        var context = CreateHttpContext();

        var middleware =
            new ExceptionHandlingMiddleware(
                _ => throw new VirtuousGiftProcessingStateException(
                    giftId,
                    VirtuousGiftProcessingStatus.RequiresReconciliation),
                NullLogger<ExceptionHandlingMiddleware>.Instance);

        await middleware.InvokeAsync(context);

        Assert.Equal(
            (int)HttpStatusCode.Conflict,
            context.Response.StatusCode);

        Assert.Equal(
            "application/json",
            context.Response.ContentType);

        var responseBody =
            await ReadResponseBodyAsync(context);

        using var document =
            JsonDocument.Parse(responseBody);

        var root = document.RootElement;

        Assert.Equal(
            giftId,
            root.GetProperty("giftId").GetInt64());

        Assert.Equal(
            "RequiresReconciliation",
            root.GetProperty("processingStatus").GetString());

        Assert.Contains(
            "RequiresReconciliation",
            root.GetProperty("error").GetString());

        Assert.Equal(
            context.TraceIdentifier,
            root.GetProperty("traceId").GetString());
    }

    [Fact]
    public async Task InvokeAsync_UnexpectedException_ReturnsSanitizedInternalServerError()
    {
        const string sensitiveMessage =
            "Sensitive internal failure details.";

        var context = CreateHttpContext();

        var middleware =
            new ExceptionHandlingMiddleware(
                _ => throw new InvalidOperationException(
                    sensitiveMessage),
                NullLogger<ExceptionHandlingMiddleware>.Instance);

        await middleware.InvokeAsync(context);

        Assert.Equal(
            (int)HttpStatusCode.InternalServerError,
            context.Response.StatusCode);

        Assert.Equal(
            "application/json",
            context.Response.ContentType);

        var responseBody =
            await ReadResponseBodyAsync(context);

        Assert.DoesNotContain(
            sensitiveMessage,
            responseBody);

        using var document =
            JsonDocument.Parse(responseBody);

        var root = document.RootElement;

        Assert.Equal(
            "An unexpected error occurred.",
            root.GetProperty("error").GetString());

        Assert.Equal(
            context.TraceIdentifier,
            root.GetProperty("traceId").GetString());
    }

    [Fact]
    public async Task InvokeAsync_AplosTransactionIdConflict_ReturnsSanitizedConflictAndStableLog()
    {
        var context = CreateHttpContext();
        var logger = new RecordingLogger<ExceptionHandlingMiddleware>();
        var middleware =
            new ExceptionHandlingMiddleware(
                _ => throw new AplosTransactionIdConflictException(70064235),
                logger);

        await middleware.InvokeAsync(context);

        Assert.Equal(
            (int)HttpStatusCode.Conflict,
            context.Response.StatusCode);

        var responseBody = await ReadResponseBodyAsync(context);
        Assert.DoesNotContain("70064235", responseBody);
        Assert.DoesNotContain("ConstraintName", responseBody);
        using var document = JsonDocument.Parse(responseBody);
        Assert.Equal(
            "An Aplos transaction ID conflict requires reconciliation.",
            document.RootElement.GetProperty("error").GetString());
        Assert.Equal(
            context.TraceIdentifier,
            document.RootElement.GetProperty("traceId").GetString());
        Assert.Null(logger.Exception);
        Assert.Contains(
            "APLOS_TRANSACTION_ID_UNIQUE_CONFLICT",
            Assert.Single(logger.Messages));
        Assert.Contains(
            context.TraceIdentifier,
            logger.Messages[0]);
        Assert.DoesNotContain(
            "70064235",
            logger.Messages[0]);
    }

    [Fact]
    public async Task InvokeAsync_ReconciliationStateWriteFailure_LogsClassificationWithoutException()
    {
        var context = CreateHttpContext();
        var logger = new RecordingLogger<ExceptionHandlingMiddleware>();
        var middleware =
            new ExceptionHandlingMiddleware(
                _ => throw new AplosTransactionConflictReconciliationException(),
                logger);

        await middleware.InvokeAsync(context);

        Assert.Equal(
            (int)HttpStatusCode.InternalServerError,
            context.Response.StatusCode);

        var responseBody = await ReadResponseBodyAsync(context);
        Assert.DoesNotContain("70064235", responseBody);
        using var document = JsonDocument.Parse(responseBody);
        Assert.Equal(
            "Gift processing requires operator investigation.",
            document.RootElement.GetProperty("error").GetString());
        Assert.Equal(
            context.TraceIdentifier,
            document.RootElement.GetProperty("traceId").GetString());
        Assert.Null(logger.Exception);
        Assert.Contains(
            "APLOS_TRANSACTION_CONFLICT_RECONCILIATION_REQUIRED",
            Assert.Single(logger.Messages));
        Assert.Contains(
            context.TraceIdentifier,
            logger.Messages[0]);
    }

    private static DefaultHttpContext CreateHttpContext()
    {
        var context =
            new DefaultHttpContext();

        context.Response.Body =
            new MemoryStream();

        return context;
    }

    private static async Task<string> ReadResponseBodyAsync(
        HttpContext context)
    {
        context.Response.Body.Position = 0;

        using var reader =
            new StreamReader(
                context.Response.Body,
                leaveOpen: true);

        return await reader.ReadToEndAsync();
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public Exception? Exception { get; private set; }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull =>
            null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Messages.Add(formatter(state, exception));
            Exception ??= exception;
        }
    }
}