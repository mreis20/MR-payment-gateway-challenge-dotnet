using System.Diagnostics;

using PaymentGateway.Api.Enums;
using PaymentGateway.Api.Models;
using PaymentGateway.Api.Models.Requests;
using PaymentGateway.Api.Models.Responses;

namespace PaymentGateway.Api.Services;

public class PaymentService(
    PaymentRequestValidator validator,
    PaymentsRepository repository,
    BankClient bankClient,
    ILogger<PaymentService> logger)
{
    public async Task<PaymentResult> ProcessAsync(PostPaymentRequest request, string? key, string traceId)
    {
        // Replays must survive time-dependent validation changes, such as expiry.
        PaymentResult? replay = repository.Replay(key, request, traceId);
        if (replay != null)
        {
            LogExistingAttempt(replay, traceId);
            return replay;
        }

        Dictionary<string, string[]> errors = validator.Validate(request, key);
        if (errors.Count > 0)
        {
            string validationErrors = string.Join("; ", errors.Select(error =>
                $"{error.Key}: {string.Join(" ", error.Value)}"));
            logger.LogInformation("Payment rejected - {ValidationErrors}, TraceId: {TraceId}", validationErrors, traceId);
            return new PaymentResult.Failed(PaymentError.InvalidRequest, traceId, errors);
        }

        PaymentResult? existing = repository.TryBegin(key!, request, traceId);
        if (existing != null)
        {
            LogExistingAttempt(existing, traceId);
            return existing;
        }

        long started = Stopwatch.GetTimestamp();
        PaymentResult result;
        try
        {
            BankResult bankResult = await bankClient.SubmitAsync(request);
            result = bankResult switch
            {
                BankResult.Authorized or BankResult.Unauthorized => new PaymentResult.Completed(new PostPaymentResponse
                {
                    Id = Guid.NewGuid(),
                    Status = bankResult == BankResult.Authorized ? PaymentStatus.Authorized : PaymentStatus.Declined,
                    CardNumberLastFour = request.CardNumber![^4..],
                    ExpiryMonth = request.ExpiryMonth!.Value,
                    ExpiryYear = request.ExpiryYear!.Value,
                    Currency = request.Currency!,
                    Amount = request.Amount!.Value
                }),
                BankResult.BadRequest => new PaymentResult.Failed(PaymentError.InvalidRequest, traceId),
                BankResult.Unavailable => new PaymentResult.Failed(PaymentError.BankUnavailable, traceId),
                _ => new PaymentResult.Failed(PaymentError.OtherError, traceId)
            };
        }
        catch (Exception exception)
        {
            // Dependency exception messages can contain sensitive request data.
            logger.LogError("Payment processing failed with {ExceptionType}, TraceId: {TraceId}",
                exception.GetType().Name, traceId);
            result = new PaymentResult.Failed(PaymentError.OtherError, traceId);
        }

        repository.Complete(key!, result);
        if (result is PaymentResult.Completed completed)
        {
            logger.LogInformation("Payment {PaymentId} completed as {Status} in {ElapsedMs} ms, TraceId: {TraceId}",
                completed.Payment.Id, completed.Payment.Status, Stopwatch.GetElapsedTime(started).TotalMilliseconds, traceId);
        }
        else if (result is PaymentResult.Failed failed)
        {
            logger.LogWarning("Payment bank attempt failed as {Error} in {ElapsedMs} ms, TraceId: {TraceId}",
                failed.Error, Stopwatch.GetElapsedTime(started).TotalMilliseconds, traceId);
        }

        return result;
    }

    private void LogExistingAttempt(PaymentResult result, string traceId)
    {
        if (result is PaymentResult.Completed completed)
        {
            logger.LogInformation("An existing payment: {PaymentId} executed, no payments were made, TraceId: {TraceId}", completed.Payment.Id, traceId);
        }
        else if (result is PaymentResult.Failed failed)
        {
            logger.LogInformation("Request returned {Error}, TraceId: {TraceId}; result trace {ResultTraceId}",
                failed.Error, traceId, failed.TraceId);
        }
    }

    public PostPaymentResponse? Get(Guid id, string traceId)
    {
        logger.LogInformation("Payment lookup for {PaymentId}, TraceId: {traceId}", id, traceId);
        PostPaymentResponse? payment = repository.Get(id);
        if (payment is not null)
        {
            logger.LogInformation("Payment found - id: {PaymentId}, TraceId: {TraceId}", id, traceId);
        }
        else
        {
            logger.LogInformation("Payment not found - id: {PaymentId}, TraceId: {TraceId}", id, traceId);
        }
        return payment;
    }
}