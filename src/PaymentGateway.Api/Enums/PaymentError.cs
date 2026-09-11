namespace PaymentGateway.Api.Enums;

public enum PaymentError
{
    InvalidRequest,
    IdempotencyConflict,
    BankUnavailable,
    OtherError
}