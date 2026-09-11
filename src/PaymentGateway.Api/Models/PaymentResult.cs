using PaymentGateway.Api.Enums;
using PaymentGateway.Api.Models.Responses;

namespace PaymentGateway.Api.Models;

public abstract record PaymentResult
{
    public sealed record Completed(PostPaymentResponse Payment) : PaymentResult;

    public sealed record Failed(
        PaymentError Error,
        string TraceId,
        Dictionary<string, string[]>? Errors = null) : PaymentResult;
}