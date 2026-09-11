using PaymentGateway.Api.Models.Requests;
using PaymentGateway.Api.Services;

namespace PaymentGateway.Api.Tests;

public class RequestValidationTests
{
    private readonly PaymentRequestValidator _validator = new(new TestClock());

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("1234567890123", false)]
    [InlineData("00000000000007", true)]
    [InlineData("0000000000000000007", true)]
    [InlineData("00000000000000000007", false)]
    [InlineData("1234567890123a", false)]
    [InlineData("123456789012 7", false)]
    [InlineData("１２３４５６７８９０１２３４", false)]
    public void ValidatesCardNumber(string? cardNumber, bool valid)
    {
        Assert.Equal(valid, !_validator.Validate(TestData.Payment(cardNumber: cardNumber), "key").ContainsKey("cardNumber"));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("01", false)]
    [InlineData("012", true)]
    [InlineData("0987", true)]
    [InlineData("01234", false)]
    [InlineData("1 2", false)]
    [InlineData("12x", false)]
    [InlineData("１２３", false)]
    public void ValidatesCvv(string? cvv, bool valid)
    {
        Assert.Equal(valid, !_validator.Validate(TestData.Payment(cvv: cvv), "key").ContainsKey("cvv"));
    }

    [Theory]
    [InlineData(null, 2030, false)]
    [InlineData(12, null, false)]
    [InlineData(0, 2031, false)]
    [InlineData(13, 2031, false)]
    [InlineData(12, 0, false)]
    [InlineData(12, 10000, false)]
    [InlineData(12, 2029, false)]
    [InlineData(11, 2030, false)]
    [InlineData(12, 2030, true)]
    [InlineData(1, 2031, true)]
    [InlineData(12, 9999, true)]
    public void ValidatesCombinedExpiryAcrossYearBoundary(int? month, int? year, bool valid)
    {
        Assert.Equal(valid, _validator.Validate(TestData.Payment(expiryMonth: month, expiryYear: year), "key").Count == 0);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("GBP", true)]
    [InlineData("USD", true)]
    [InlineData("EUR", true)]
    [InlineData("JPY", false)]
    [InlineData("gbp", false)]
    [InlineData("GB", false)]
    [InlineData("GBPP", false)]
    public void ValidatesSupportedCurrencies(string? currency, bool valid)
    {
        Assert.Equal(valid, !_validator.Validate(TestData.Payment(currency: currency), "key").ContainsKey("currency"));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData(0, false)]
    [InlineData(-1, false)]
    [InlineData(1, true)]
    [InlineData(int.MaxValue, true)]
    public void ValidatesAmount(int? amount, bool valid)
    {
        Assert.Equal(valid, !_validator.Validate(TestData.Payment(amount: amount), "key").ContainsKey("amount"));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("a", true)]
    [InlineData("ABC-xyz_123", true)]
    [InlineData("has space", false)]
    [InlineData("key,another", false)]
    [InlineData("é", false)]
    public void ValidatesIdempotencyKey(string? key, bool valid)
    {
        Assert.Equal(valid, !_validator.Validate(TestData.Payment(), key).ContainsKey("Idempotency-Key"));
    }

    [Theory]
    [InlineData(128, true)]
    [InlineData(129, false)]
    public void ValidatesIdempotencyKeyLength(int length, bool valid)
    {
        Assert.Equal(valid, _validator.Validate(TestData.Payment(), new string('a', length)).Count == 0);
    }

    [Fact]
    public void RejectsAllMissingFieldsTogether()
    {
        Dictionary<string, string[]> errors = _validator.Validate(new PostPaymentRequest(), null);
        Assert.Equal(7, errors.Count);
    }
}