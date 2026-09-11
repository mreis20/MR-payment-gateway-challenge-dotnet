using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using PaymentGateway.Api.Models.Requests;
using PaymentGateway.Api.Services;

namespace PaymentGateway.Api.Tests;

internal static class TestData
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static PostPaymentRequest Payment(
        string? cardNumber = "0000000000000007", string? cvv = "0987",
        int? expiryMonth = 12, int? expiryYear = 2030, string? currency = "GBP", int? amount = 1050)
    {
        return new PostPaymentRequest
        {
            CardNumber = cardNumber, Cvv = cvv, ExpiryMonth = expiryMonth,
            ExpiryYear = expiryYear, Currency = currency, Amount = amount
        };
    }
}

internal class TestClock : TimeProvider
{
    public DateTimeOffset UtcNow { get; set; } = new(2030, 12, 15, 12, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => UtcNow;
}

internal class BankHandler : HttpMessageHandler
{
    private int _calls;
    public int Calls => Volatile.Read(ref _calls);
    public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Respond { get; set; }
        = (_, _) => Task.FromResult(JsonResponse());

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _calls);
        return Respond(request, cancellationToken);
    }

    public static HttpResponseMessage JsonResponse(string body = "{\"authorized\":true,\"authorization_code\":\"0bb07405-6d44-4b50-a14f-7ae0beff13ad\"}", HttpStatusCode status = HttpStatusCode.OK)
    {
        return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}

internal class ServiceFixture : IDisposable
{
    private readonly HttpClient _httpClient;
    public BankHandler Bank { get; } = new();
    public TestClock Clock { get; } = new();
    public PaymentService Service { get; }

    public ServiceFixture()
    {
        _httpClient = new HttpClient(Bank) { BaseAddress = new Uri("http://bank.test/") };
        Service = new PaymentService(new PaymentRequestValidator(Clock), new PaymentsRepository(),
            new BankClient(_httpClient), NullLogger<PaymentService>.Instance);
    }

    public void Dispose() => _httpClient.Dispose();
}

internal class ApiFixture : WebApplicationFactory<Program>
{
    public BankHandler Bank { get; } = new();
    public CapturedLogs Logs { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureLogging(logging => logging.ClearProviders().AddProvider(Logs));
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(new TestClock());
            services.AddHttpClient<BankClient>().ConfigurePrimaryHttpMessageHandler(() => Bank);
        });
    }

    public HttpClient Client() => CreateClient(new WebApplicationFactoryClientOptions
    {
        BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false
    });

    public static async Task<HttpResponseMessage> Post(HttpClient client, string? key = "payment-1",
        PostPaymentRequest? payment = null, CancellationToken cancellationToken = default)
    {
        return await PostJson(client, JsonSerializer.Serialize(payment ?? TestData.Payment(), TestData.Json), key, cancellationToken);
    }

    public static async Task<HttpResponseMessage> PostJson(HttpClient client, string json, string? key = "payment-1",
        CancellationToken cancellationToken = default)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, "/api/payments")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        if (key != null)
        {
            request.Headers.TryAddWithoutValidation("Idempotency-Key", key);
        }

        return await client.SendAsync(request, cancellationToken);
    }
}

internal class CapturedLogs : ILoggerProvider
{
    public ConcurrentQueue<string> Messages { get; } = new();
    public ConcurrentQueue<Dictionary<string, object?>> Properties { get; } = new();
    public ILogger CreateLogger(string categoryName) => new CapturedLogger(Messages, Properties);
    public void Dispose() { }

    private class CapturedLogger(ConcurrentQueue<string> messages,
        ConcurrentQueue<Dictionary<string, object?>> properties) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            messages.Enqueue(formatter(state, exception) + (exception?.ToString() ?? ""));
            if (state is IEnumerable<KeyValuePair<string, object?>> fields)
            {
                properties.Enqueue(fields.ToDictionary(pair => pair.Key, pair => pair.Value));
            }
        }
    }
}