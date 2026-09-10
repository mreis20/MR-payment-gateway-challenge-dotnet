using PaymentGateway.Api.Models.Requests;

namespace PaymentGateway.Api.Services;

public class PaymentRequestValidator(TimeProvider timeProvider)
{
    public Dictionary<string, string[]> Validate(PostPaymentRequest request, string? idempotencyKey)
    {
        Dictionary<string, string[]> errors = [];
        if (!Digits(request.CardNumber, 14, 19))
        {
            errors["cardNumber"] = ["Must contain 14–19 ASCII digits."];
        }

        if (!Digits(request.Cvv, 3, 4))
        {
            errors["cvv"] = ["Must contain 3–4 ASCII digits."];
        }

        if (request.ExpiryMonth is not (>= 1 and <= 12))
        {
            errors["expiryMonth"] = ["Must be an integer between 1 and 12."];
        }

        if (request.ExpiryYear is not (>= 1 and <= 9999))
        {
            errors["expiryYear"] = ["Must be an integer between 1 and 9999."];
        }
        else if (!errors.ContainsKey("expiryMonth"))
        {
            DateTimeOffset now = timeProvider.GetUtcNow();
            if (request.ExpiryYear < now.Year ||
                (request.ExpiryYear == now.Year && request.ExpiryMonth < now.Month))
            {
                errors["expiryYear"] = ["The combined expiry month and year must not be in the past."];
            }
        }

        if (request.Currency is not ("GBP" or "USD" or "EUR"))
        {
            errors["currency"] = ["Must be GBP, USD, or EUR."];
        }

        if (request.Amount is not > 0)
        {
            errors["amount"] = ["Must be an integer between 1 and 2147483647 in minor units."];
        }

        if (idempotencyKey is not { Length: >= 1 and <= 128 } ||
            !idempotencyKey.All(c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-' or '_'))
        {
            errors["Idempotency-Key"] = ["Must contain 1–128 letters, digits, hyphens, or underscores."];
        }

        return errors;
    }

    private static bool Digits(string? value, int minimum, int maximum)
    {
        return value != null && value.Length >= minimum && value.Length <= maximum &&
            value.All(c => c is >= '0' and <= '9');
    }
}