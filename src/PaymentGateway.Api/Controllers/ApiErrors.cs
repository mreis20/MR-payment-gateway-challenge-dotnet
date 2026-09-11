using Microsoft.AspNetCore.Mvc;

using PaymentGateway.Api.Enums;
using PaymentGateway.Api.Models;

namespace PaymentGateway.Api.Controllers;

public static class ApiErrors
{
    public static ObjectResult FromFailure(PaymentResult.Failed failure)
    {
        (int status, string title, string code) = failure.Error switch
        {
            PaymentError.InvalidRequest => (400, "Payment request is invalid", "invalid_request"),
            PaymentError.IdempotencyConflict => (409, "Idempotency key has already been used", "idempotency_conflict"),
            PaymentError.BankUnavailable => (503, "Bank is unavailable", "bank_unavailable"),
            _ => (500, "Any other error", "any_other_error")
        };

        ProblemDetails problem = failure.Errors != null
            ? new ValidationProblemDetails(failure.Errors)
            : new ProblemDetails();
        problem.Type = "about:blank";
        problem.Status = status;
        problem.Title = title;
        problem.Extensions["code"] = code;
        problem.Extensions["traceId"] = failure.TraceId;
        if (failure.Error == PaymentError.InvalidRequest)
        {
            problem.Extensions["paymentStatus"] = "Rejected";
        }

        ObjectResult result = new(problem) { StatusCode = status };
        result.ContentTypes.Add("application/problem+json");
        return result;
    }
}