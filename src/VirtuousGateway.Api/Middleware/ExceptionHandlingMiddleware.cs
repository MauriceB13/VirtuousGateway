using System.Net;
using System.Text.Json;
using VirtuousGateway.Core.Virtuous;

namespace VirtuousGateway.Api.Middleware;

public sealed class ExceptionHandlingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<ExceptionHandlingMiddleware> _logger;

    public ExceptionHandlingMiddleware(
        RequestDelegate next,
        ILogger<ExceptionHandlingMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (Exception exception)
        {
            if (exception is AplosTransactionConflictReconciliationException)
            {
                _logger.LogError(
                    "Gift requires operator investigation after an Aplos transaction conflict. Classification={Classification} TraceId={TraceId}",
                    "APLOS_TRANSACTION_CONFLICT_RECONCILIATION_REQUIRED",
                    context.TraceIdentifier);
            }
            else if (exception is AplosTransactionIdConflictException)
            {
                _logger.LogWarning(
                    "Aplos transaction ID conflict requires reconciliation. Classification={Classification} TraceId={TraceId}",
                    "APLOS_TRANSACTION_ID_UNIQUE_CONFLICT",
                    context.TraceIdentifier);
            }
            else
            {
                _logger.LogError(
                    exception,
                    "Unhandled exception processing {Method} {Path}",
                    context.Request.Method,
                    context.Request.Path);
            }

            await HandleExceptionAsync(
            context,
            exception);
        }
    }

    private static async Task HandleExceptionAsync(
    HttpContext context,
    Exception exception)
{
    if (context.Response.HasStarted)
    {
        return;
    }

    context.Response.Clear();
    context.Response.ContentType = "application/json";

    object response;

    if (exception is VirtuousGiftProcessingStateException processingException)
    {
        context.Response.StatusCode =
            (int)HttpStatusCode.Conflict;

        response = new
        {
            error = processingException.Message,
            giftId = processingException.GiftId,
            processingStatus =
            processingException.Status.ToString(),
            traceId = context.TraceIdentifier
        };
    }
    else if (exception is
        VirtuousGiftFingerprintMismatchException fingerprintException)
    {
        context.Response.StatusCode =
            (int)HttpStatusCode.Conflict;

        response = new
        {
            error = fingerprintException.Message,
            giftId = fingerprintException.GiftId,
            traceId = context.TraceIdentifier
        };
    }
    else if (exception is AplosTransactionIdConflictException)
    {
        context.Response.StatusCode =
            (int)HttpStatusCode.Conflict;

        response = new
        {
            error = "An Aplos transaction ID conflict requires reconciliation.",
            traceId = context.TraceIdentifier
        };
    }
    else if (exception is AplosTransactionConflictReconciliationException)
    {
        context.Response.StatusCode =
            (int)HttpStatusCode.InternalServerError;

        response = new
        {
            error = "Gift processing requires operator investigation.",
            traceId = context.TraceIdentifier
        };
    }
    else
    {
        context.Response.StatusCode =
            (int)HttpStatusCode.InternalServerError;

        response = new
        {
            error = "An unexpected error occurred.",
            traceId = context.TraceIdentifier
        };
    }

    await context.Response.WriteAsync(
        JsonSerializer.Serialize(response));
}

}
