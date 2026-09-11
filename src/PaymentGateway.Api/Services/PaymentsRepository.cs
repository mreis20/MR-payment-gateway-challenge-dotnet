using PaymentGateway.Api.Models.Responses;

namespace PaymentGateway.Api.Services;

public class PaymentsRepository
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, PostPaymentResponse> _payments = [];
    private readonly HashSet<string> _usedKeys = new(StringComparer.Ordinal);

    public bool TryAddIdempotencyKey(string key)
    {
        lock (_gate)
        {
            // Checking and adding together prevents simultaneous duplicate submissions.
            return _usedKeys.Add(key);
        }
    }

    public void Add(PostPaymentResponse payment)
    {
        lock (_gate)
        {
            _payments.Add(payment.Id, payment);
        }
    }

    public PostPaymentResponse? Get(Guid id)
    {
        lock (_gate)
        {
            return _payments.GetValueOrDefault(id);
        }
    }
}