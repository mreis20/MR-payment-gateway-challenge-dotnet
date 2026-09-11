namespace PaymentGateway.Api.Enums;

public enum PaymentError
{
    InvalidRequest,
    IdempotencyConflict,
    IdempotencyInProgress,
    BankUnavailable,
    OtherError
}