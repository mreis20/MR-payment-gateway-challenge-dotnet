using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PaymentGateway.Api.Tests;

public class PaymentsControllerTests
{
    [Theory]
    [InlineData(true, "Authorized")]
    [InlineData(false, "Declined")]
    public async Task PostThenGetPreserveThePublicContractAndReusedKeysReturn409(bool authorized, string expectedStatus)
    {
        using ApiFixture fixture = new();
        fixture.Bank.Respond = (_, _) => Task.FromResult(BankHandler.JsonResponse(
            authorized ? "{\"authorized\":true,\"authorization_code\":\"bank-secret\"}" : "{\"authorized\":false}"));
        using HttpClient client = fixture.Client();
        using HttpResponseMessage post = await ApiFixture.Post(client);
        Assert.Equal(HttpStatusCode.OK, post.StatusCode);
        Assert.Equal("application/json", post.Content.Headers.ContentType!.MediaType);
        string originalBody = await post.Content.ReadAsStringAsync();
        using JsonDocument json = JsonDocument.Parse(originalBody);
        JsonElement payment = json.RootElement;
        Assert.Equal(7, payment.EnumerateObject().Count());
        Assert.NotEqual(Guid.Empty, payment.GetProperty("id").GetGuid());
        Assert.Equal(expectedStatus, payment.GetProperty("status").GetString());
        Assert.Equal("0007", payment.GetProperty("cardNumberLastFour").GetString());
        Assert.Equal(12, payment.GetProperty("expiryMonth").GetInt32());
        Assert.Equal(2030, payment.GetProperty("expiryYear").GetInt32());
        Assert.Equal("GBP", payment.GetProperty("currency").GetString());
        Assert.Equal(1050, payment.GetProperty("amount").GetInt32());
        Assert.DoesNotContain(TestData.Payment().CardNumber!, originalBody);
        Assert.DoesNotContain("cvv", originalBody);
        Assert.DoesNotContain("bank-secret", originalBody);

        using HttpResponseMessage get = await client.GetAsync($"/api/payments/{payment.GetProperty("id").GetString()}");
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        Assert.Equal(originalBody, await get.Content.ReadAsStringAsync());
        Dictionary<string, object?> lookupLog = Assert.Single(fixture.Logs.Properties.Where(fields => fields.ContainsKey("traceId")));
        Assert.Equal(payment.GetProperty("id").GetGuid(), Assert.IsType<Guid>(lookupLog["PaymentId"]));
        string lookupTrace = Assert.IsType<string>(lookupLog["traceId"]);
        Assert.False(string.IsNullOrWhiteSpace(lookupTrace));
        Assert.Contains(fixture.Logs.Properties, fields =>
            fields.TryGetValue("PaymentId", out object? id) && Equals(id, payment.GetProperty("id").GetGuid()) &&
            fields.TryGetValue("TraceId", out object? trace) && Equals(trace, lookupTrace));
        Assert.DoesNotContain(TestData.Payment().CardNumber!, string.Join("\n", fixture.Logs.Messages));
        Assert.DoesNotContain("bank-secret", string.Join("\n", fixture.Logs.Messages));

        // Reusing a key is rejected even when only JSON formatting changes.
        const string reordered = "{ \"cvv\": \"0987\", \"amount\": 1050, \"currency\": \"GBP\", \"expiryYear\": 2030, \"expiryMonth\": 12, \"cardNumber\": \"0000000000000007\" }";
        using HttpResponseMessage retry = await ApiFixture.PostJson(client, reordered);
        JsonElement duplicate = await AssertProblem(retry, HttpStatusCode.Conflict);
        Assert.Equal("idempotency_conflict", duplicate.GetProperty("code").GetString());
        Assert.Equal("Idempotency key has already been used", duplicate.GetProperty("title").GetString());
        Assert.Equal(1, fixture.Bank.Calls);
    }

    [Fact]
    public async Task Returns404IfPaymentNotFound()
    {
        using ApiFixture fixture = new();
        using HttpClient client = fixture.Client();
        Guid paymentId = Guid.NewGuid();
        using HttpResponseMessage response = await client.GetAsync($"/api/payments/{paymentId}");
        JsonElement problem = await AssertProblem(response, HttpStatusCode.NotFound);
        Assert.Equal("Payment not found", problem.GetProperty("title").GetString());
        Assert.Equal("No payment exists with the supplied ID.", problem.GetProperty("detail").GetString());
        Dictionary<string, object?> lookupLog = Assert.Single(fixture.Logs.Properties.Where(fields => fields.ContainsKey("traceId")));
        Assert.Equal(paymentId, Assert.IsType<Guid>(lookupLog["PaymentId"]));
        string lookupTrace = Assert.IsType<string>(lookupLog["traceId"]);
        Assert.False(string.IsNullOrWhiteSpace(lookupTrace));
        Assert.Contains(fixture.Logs.Properties, fields =>
            fields.TryGetValue("PaymentId", out object? id) && Equals(id, paymentId) &&
            fields.TryGetValue("TraceId", out object? trace) && Equals(trace, lookupTrace));
        Assert.Equal(0, fixture.Bank.Calls);
    }

    [Fact]
    public async Task MalformedPaymentIdReturns404WithProblemDetails()
    {
        using ApiFixture fixture = new();
        using HttpClient client = fixture.Client();
        using HttpResponseMessage response = await client.GetAsync("/api/payments/test");
        JsonElement problem = await AssertProblem(response, HttpStatusCode.NotFound);
        Assert.Equal("Not Found", problem.GetProperty("title").GetString());
        Assert.Equal(0, fixture.Bank.Calls);
    }

    [Fact]
    public async Task UnknownRouteReturns404WithProblemDetails()
    {
        using ApiFixture fixture = new();
        using HttpClient client = fixture.Client();
        using HttpResponseMessage response = await client.GetAsync("/api/unknown");
        await AssertProblem(response, HttpStatusCode.NotFound);
        Assert.Equal(0, fixture.Bank.Calls);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("key with spaces")]
    public async Task InvalidOrMissingIdempotencyKeyIsRejectedBeforeBankCall(string? key)
    {
        using ApiFixture fixture = new();
        using HttpClient client = fixture.Client();
        using HttpResponseMessage response = await ApiFixture.Post(client, key);
        JsonElement problem = await AssertProblem(response, HttpStatusCode.BadRequest);
        Assert.Equal("Rejected", problem.GetProperty("paymentStatus").GetString());
        Assert.True(problem.GetProperty("errors").TryGetProperty("Idempotency-Key", out _));
        Assert.Equal(0, fixture.Bank.Calls);
    }

    [Fact]
    public async Task InvalidFieldsAreRejectedWithoutEchoingSensitiveValuesAndKeyCanBeReused()
    {
        using ApiFixture fixture = new();
        using HttpClient client = fixture.Client();
        using HttpResponseMessage response = await ApiFixture.Post(client, payment: TestData.Payment(amount: 0, currency: "JPY", cvv: "sensitive-cvv"));
        JsonElement problem = await AssertProblem(response, HttpStatusCode.BadRequest);
        Assert.Equal("Rejected", problem.GetProperty("paymentStatus").GetString());
        Assert.True(problem.GetProperty("errors").TryGetProperty("amount", out _));
        Assert.True(problem.GetProperty("errors").TryGetProperty("currency", out _));
        Dictionary<string, object?> rejectionLog = Assert.Single(fixture.Logs.Properties.Where(fields => fields.ContainsKey("ValidationErrors")));
        string loggedErrors = Assert.IsType<string>(rejectionLog["ValidationErrors"]);
        foreach (JsonProperty error in problem.GetProperty("errors").EnumerateObject())
        {
            Assert.Contains(error.Name, loggedErrors);
            foreach (JsonElement message in error.Value.EnumerateArray())
            {
                Assert.Contains(message.GetString()!, loggedErrors);
            }
        }
        Assert.Equal(problem.GetProperty("traceId").GetString(), Assert.IsType<string>(rejectionLog["TraceId"]));
        string exposed = problem.GetRawText() + string.Join("\n", fixture.Logs.Messages);
        Assert.DoesNotContain(TestData.Payment().CardNumber!, exposed);
        Assert.DoesNotContain("sensitive-cvv", exposed);
        Assert.Equal(0, fixture.Bank.Calls);
        using HttpResponseMessage corrected = await ApiFixture.Post(client);
        Assert.Equal(HttpStatusCode.OK, corrected.StatusCode);
        Assert.Equal(1, fixture.Bank.Calls);
    }

    public static IEnumerable<object[]> InvalidJson()
    {
        yield return ["{"];
        yield return ["null"];
        yield return ["{}"];
        yield return ["[]"];
        yield return [""];
        foreach (string field in new[] { "cardNumber", "cvv", "expiryMonth", "expiryYear", "currency", "amount" })
        {
            JsonObject missing = JsonSerializer.SerializeToNode(TestData.Payment(), TestData.Json)!.AsObject();
            missing.Remove(field);
            yield return [missing.ToJsonString()];
            JsonObject nulled = JsonSerializer.SerializeToNode(TestData.Payment(), TestData.Json)!.AsObject();
            nulled[field] = null;
            yield return [nulled.ToJsonString()];
        }
        foreach (string field in new[] { "amount", "expiryMonth", "expiryYear" })
        {
            foreach (string value in new[] { "1.5", "2147483648", "\"1\"", "true" })
            {
                JsonObject json = JsonSerializer.SerializeToNode(TestData.Payment(), TestData.Json)!.AsObject();
                json[field] = JsonNode.Parse(value);
                yield return [json.ToJsonString()];
            }
        }
        foreach (string field in new[] { "cardNumber", "cvv", "currency" })
        {
            JsonObject json = JsonSerializer.SerializeToNode(TestData.Payment(), TestData.Json)!.AsObject();
            json[field] = 123;
            yield return [json.ToJsonString()];
        }
    }

    [Theory]
    [MemberData(nameof(InvalidJson))]
    public async Task InvalidJsonReturnsBadRequestWithoutCallingBank(string json)
    {
        using ApiFixture fixture = new();
        using HttpClient client = fixture.Client();
        using HttpResponseMessage response = await ApiFixture.PostJson(client, json);
        JsonElement problem = await AssertProblem(response, HttpStatusCode.BadRequest);
        Assert.True(problem.TryGetProperty("errors", out _));
        Assert.DoesNotContain(TestData.Payment().CardNumber!, problem.GetRawText());
        Assert.Equal(0, fixture.Bank.Calls);
    }

    [Fact]
    public async Task UnsupportedContentTypeReturns415WithoutBankCall()
    {
        using ApiFixture fixture = new();
        using HttpClient client = fixture.Client();
        using HttpResponseMessage response = await client.PostAsync("/api/payments", new StringContent("text", Encoding.UTF8, "text/plain"));
        await AssertProblem(response, HttpStatusCode.UnsupportedMediaType);
        Assert.Equal(0, fixture.Bank.Calls);
    }

    [Fact]
    public async Task ChangedRequestReturns409AndOriginalRemainsRetrievable()
    {
        using ApiFixture fixture = new();
        using HttpClient client = fixture.Client();
        using HttpResponseMessage first = await ApiFixture.Post(client);
        using HttpResponseMessage changed = await ApiFixture.Post(client, payment: TestData.Payment(amount: 2));
        JsonElement problem = await AssertProblem(changed, HttpStatusCode.Conflict);
        Assert.Equal("idempotency_conflict", problem.GetProperty("code").GetString());
        string id = JsonNode.Parse(await first.Content.ReadAsStringAsync())!["id"]!.GetValue<string>();
        using HttpResponseMessage get = await client.GetAsync($"/api/payments/{id}");
        Assert.Equal(await first.Content.ReadAsStringAsync(), await get.Content.ReadAsStringAsync());
        Assert.Equal(1, fixture.Bank.Calls);
    }

    [Theory]
    [InlineData("invalid", HttpStatusCode.InternalServerError)]
    [InlineData("bad-request", HttpStatusCode.BadRequest)]
    [InlineData("unavailable", HttpStatusCode.ServiceUnavailable)]
    [InlineData("connection", HttpStatusCode.InternalServerError)]
    [InlineData("timeout", HttpStatusCode.InternalServerError)]
    [InlineData("unexpected", HttpStatusCode.InternalServerError)]
    [InlineData("unexpected-status", HttpStatusCode.InternalServerError)]
    [InlineData("missing-code", HttpStatusCode.InternalServerError)]
    public async Task BankErrorsPreserveSpecifiedStatusesAndPreventKeyReuse(string scenario, HttpStatusCode expected)
    {
        using ApiFixture fixture = new();
        string sensitive = $"bank-secret card={TestData.Payment().CardNumber} cvv={TestData.Payment().Cvv}";
        fixture.Bank.Respond = (_, _) => scenario switch
        {
            "connection" => throw new HttpRequestException(sensitive),
            "timeout" => throw new TaskCanceledException(sensitive),
            "unexpected" => throw new InvalidOperationException(sensitive),
            "unexpected-status" => Task.FromResult(BankHandler.JsonResponse(sensitive, HttpStatusCode.BadGateway)),
            "missing-code" => Task.FromResult(BankHandler.JsonResponse("{\"authorized\":true}")),
            "bad-request" => Task.FromResult(BankHandler.JsonResponse(sensitive, HttpStatusCode.BadRequest)),
            "unavailable" => Task.FromResult(BankHandler.JsonResponse(sensitive, HttpStatusCode.ServiceUnavailable)),
            _ => Task.FromResult(BankHandler.JsonResponse(sensitive))
        };
        using HttpClient client = fixture.Client();
        using HttpResponseMessage first = await ApiFixture.Post(client);
        JsonElement problem = await AssertProblem(first, expected);
        string expectedCode = expected switch
        {
            HttpStatusCode.BadRequest => "invalid_request",
            HttpStatusCode.ServiceUnavailable => "bank_unavailable",
            _ => "any_other_error"
        };
        Assert.Equal(expectedCode, problem.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(problem.GetProperty("title").GetString()));
        if (expected == HttpStatusCode.InternalServerError)
        {
            Assert.Equal("Any other error", problem.GetProperty("title").GetString());
        }
        Assert.False(problem.TryGetProperty("id", out _));
        if (expected == HttpStatusCode.BadRequest)
        {
            Assert.Equal("Rejected", problem.GetProperty("paymentStatus").GetString());
        }
        else
        {
            Assert.False(problem.TryGetProperty("paymentStatus", out _));
        }
        using HttpResponseMessage retry = await ApiFixture.Post(client);
        JsonElement duplicate = await AssertProblem(retry, HttpStatusCode.Conflict);
        Assert.Equal("idempotency_conflict", duplicate.GetProperty("code").GetString());
        Assert.NotEqual(problem.GetProperty("traceId").GetString(), duplicate.GetProperty("traceId").GetString());
        Assert.Equal(1, fixture.Bank.Calls);
        Assert.Contains(fixture.Logs.Properties, fields =>
            fields.TryGetValue("TraceId", out object? trace) && Equals(trace, duplicate.GetProperty("traceId").GetString()));
        string exposed = problem.GetRawText() + string.Join("\n", fixture.Logs.Messages);
        Assert.DoesNotContain(TestData.Payment().CardNumber!, exposed);
        Assert.DoesNotContain("cvv=0987", exposed);
        Assert.DoesNotContain("bank-secret", exposed);
    }

    [Fact]
    public async Task ConcurrentHttpRetryConflictsAndCallerCancellationDoesNotCancelBankCall()
    {
        using ApiFixture fixture = new();
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken bankToken = default;
        fixture.Bank.Respond = async (_, token) =>
        {
            bankToken = token;
            started.TrySetResult();
            await release.Task;
            return BankHandler.JsonResponse();
        };
        using HttpClient client = fixture.Client();
        using CancellationTokenSource caller = new();
        Task<HttpResponseMessage> first = ApiFixture.Post(client, cancellationToken: caller.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            using HttpResponseMessage concurrent = await ApiFixture.Post(client);
            JsonElement problem = await AssertProblem(concurrent, HttpStatusCode.Conflict);
            Assert.Equal("idempotency_conflict", problem.GetProperty("code").GetString());
            caller.Cancel();
            Assert.False(bankToken.IsCancellationRequested);
        }
        finally
        {
            release.TrySetResult();
        }

        try
        {
            using HttpResponseMessage ignored = await first;
        }
        catch (OperationCanceledException)
        {
            // The caller may stop waiting; the bank operation must still complete.
        }

        using HttpResponseMessage retry = await ApiFixture.Post(client);
        JsonElement duplicate = await AssertProblem(retry, HttpStatusCode.Conflict);
        Assert.Equal("idempotency_conflict", duplicate.GetProperty("code").GetString());
        Assert.False(bankToken.IsCancellationRequested);
        Assert.Equal(1, fixture.Bank.Calls);
    }

    [Fact]
    public async Task InvalidChangedRequestReturns400AndPreservesOriginalPayment()
    {
        using ApiFixture fixture = new();
        using HttpClient client = fixture.Client();
        using HttpResponseMessage first = await ApiFixture.Post(client);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        using HttpResponseMessage changed = await ApiFixture.Post(client, payment: TestData.Payment(amount: 0));
        JsonElement problem = await AssertProblem(changed, HttpStatusCode.BadRequest);
        Assert.Equal("invalid_request", problem.GetProperty("code").GetString());
        Assert.Equal("Rejected", problem.GetProperty("paymentStatus").GetString());

        string originalBody = await first.Content.ReadAsStringAsync();
        string id = JsonNode.Parse(originalBody)!["id"]!.GetValue<string>();
        using HttpResponseMessage get = await client.GetAsync($"/api/payments/{id}");
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        Assert.Equal(originalBody, await get.Content.ReadAsStringAsync());
        using HttpResponseMessage duplicate = await ApiFixture.Post(client);
        await AssertProblem(duplicate, HttpStatusCode.Conflict);
        Assert.Equal(1, fixture.Bank.Calls);
    }

    private static async Task<JsonElement> AssertProblem(HttpResponseMessage response, HttpStatusCode expected)
    {
        Assert.Equal(expected, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement problem = json.RootElement.Clone();
        Assert.Equal((int)expected, problem.GetProperty("status").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(problem.GetProperty("title").GetString()));
        return problem;
    }
}