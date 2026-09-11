using System.Globalization;
using System.Net;
using System.Text.Json;

using PaymentGateway.Api.Enums;
using PaymentGateway.Api.Services;

namespace PaymentGateway.Api.Tests;

public class BankClientTests
{
    [Fact]
    public async Task SendsExactBankContractPreservingLeadingZerosAndMinorUnits()
    {
        using BankHandler handler = new();
        handler.Respond = async (request, _) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("http://bank.test/payments", request.RequestUri!.ToString());
            Assert.Equal("application/json", request.Content!.Headers.ContentType!.MediaType);
            using JsonDocument json = JsonDocument.Parse(await request.Content.ReadAsStringAsync());
            JsonElement body = json.RootElement;
            Assert.Equal(5, body.EnumerateObject().Count());
            Assert.Equal("0000000000000007", body.GetProperty("card_number").GetString());
            Assert.Equal("01/2031", body.GetProperty("expiry_date").GetString());
            Assert.Equal("GBP", body.GetProperty("currency").GetString());
            Assert.Equal(1050, body.GetProperty("amount").GetInt32());
            Assert.Equal("0987", body.GetProperty("cvv").GetString());
            return BankHandler.JsonResponse();
        };
        using HttpClient http = new(handler) { BaseAddress = new Uri("http://bank.test/") };
        CultureInfo original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("ar-SA");
            Assert.Equal(BankResult.Authorized, await new BankClient(http).SubmitAsync(TestData.Payment(expiryMonth: 1, expiryYear: 2031)));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Theory]
    [InlineData("{\"authorized\":true,\"authorization_code\":\"bank-secret\"}", 200, BankResult.Authorized)]
    [InlineData("{\"authorized\":false}", 200, BankResult.Unauthorized)]
    [InlineData("{\"authorized\":true}", 200, BankResult.OtherError)]
    [InlineData("{\"authorized\":true,\"authorization_code\":\"\"}", 200, BankResult.OtherError)]
    [InlineData("{\"authorized\":true,\"authorization_code\":null}", 200, BankResult.OtherError)]
    [InlineData("{\"authorized\":false,\"authorization_code\":\"\"}", 200, BankResult.Unauthorized)]
    [InlineData("{}", 200, BankResult.OtherError)]
    [InlineData("{\"authorized\":null}", 200, BankResult.OtherError)]
    [InlineData("{\"authorized\":\"false\"}", 200, BankResult.OtherError)]
    [InlineData("{\"authorized\":1}", 200, BankResult.OtherError)]
    [InlineData("null", 200, BankResult.OtherError)]
    [InlineData("", 200, BankResult.OtherError)]
    [InlineData("not-json", 200, BankResult.OtherError)]
    [InlineData("{}", 400, BankResult.BadRequest)]
    [InlineData("{\"error_message\":\"Not all required properties were sent in the request\"}", 400, BankResult.BadRequest)]
    [InlineData("{}", 302, BankResult.OtherError)]
    [InlineData("{}", 500, BankResult.OtherError)]
    [InlineData("{}", 502, BankResult.OtherError)]
    [InlineData("{}", 504, BankResult.OtherError)]
    [InlineData("{}", 503, BankResult.Unavailable)]
    public async Task InterpretsBankResponsesWithoutRetrying(string body, int status, BankResult expected)
    {
        using BankHandler handler = new() { Respond = (_, _) => Task.FromResult(BankHandler.JsonResponse(body, (HttpStatusCode)status)) };
        using HttpClient http = new(handler) { BaseAddress = new Uri("http://bank.test/") };
        Assert.Equal(expected, await new BankClient(http).SubmitAsync(TestData.Payment()));
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task ConnectionFailureIsAnyOtherErrorWithoutRetry()
    {
        using BankHandler handler = new() { Respond = (_, _) => throw new HttpRequestException("unreachable") };
        using HttpClient http = new(handler) { BaseAddress = new Uri("http://bank.test/") };
        Assert.Equal(BankResult.OtherError, await new BankClient(http).SubmitAsync(TestData.Payment()));
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task TimeoutBoundsAnUnresponsiveBankWithoutRetry()
    {
        using BankHandler handler = new()
        {
            Respond = async (_, token) =>
            {
                await Task.Delay(Timeout.Infinite, token);
                return BankHandler.JsonResponse();
            }
        };
        using HttpClient http = new(handler) { BaseAddress = new Uri("http://bank.test/"), Timeout = TimeSpan.FromMilliseconds(100) };
        Assert.Equal(BankResult.OtherError, await new BankClient(http).SubmitAsync(TestData.Payment()).WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, handler.Calls);
    }
}