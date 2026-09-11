using Microsoft.AspNetCore.Mvc;

using PaymentGateway.Api.Models;
using PaymentGateway.Api.Models.Requests;
using PaymentGateway.Api.Models.Responses;
using PaymentGateway.Api.Services;

namespace PaymentGateway.Api.Controllers;

[Route("api/payments")]
[ApiController]
public class PaymentsController(PaymentService payments) : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> PostAsync(
        [FromBody] PostPaymentRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey)
    {
        PaymentResult result = await payments.ProcessAsync(request, idempotencyKey, HttpContext.TraceIdentifier);
        return result switch
        {
            PaymentResult.Completed completed => Ok(completed.Payment),
            PaymentResult.Failed failed => ApiErrors.FromFailure(failed),
            _ => throw new InvalidOperationException("Unknown payment result.")
        };
    }

    [HttpGet("{id:guid}")]
    public IActionResult Get(Guid id)
    {
        PostPaymentResponse? payment = payments.Get(id, HttpContext.TraceIdentifier);

        return payment == null
            ? Problem(statusCode: 404, title: "Payment not found",
                detail: "No payment exists with the supplied ID.")
            : Ok(new GetPaymentResponse
            {
                Id = payment.Id,
                Status = payment.Status,
                CardNumberLastFour = payment.CardNumberLastFour,
                ExpiryMonth = payment.ExpiryMonth,
                ExpiryYear = payment.ExpiryYear,
                Currency = payment.Currency,
                Amount = payment.Amount
            });
    }
}