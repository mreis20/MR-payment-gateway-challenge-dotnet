using System.Net;

using PaymentGateway.Api.Enums;
using PaymentGateway.Api.Models;
using PaymentGateway.Api.Models.Requests;

namespace PaymentGateway.Api.Tests;

public class PaymentServiceTests
{
    [Fact]
    public async Task RejectionDoesNotCallBankOrConsumeKey()
    {
        using ServiceFixture fixture = new();
        PaymentResult.Failed rejected = Assert.IsType<PaymentResult.Failed>(
            await fixture.Service.ProcessAsync(TestData.Payment(amount: 0), "key", "trace-1"));
        Assert.Equal(PaymentError.InvalidRequest, rejected.Error);
        Assert.Equal(0, fixture.Bank.Calls);
        Assert.Null(fixture.Service.Get(Guid.Empty, "get-trace"));

        Assert.IsType<PaymentResult.Completed>(await fixture.Service.ProcessAsync(TestData.Payment(), "key", "trace-2"));
        Assert.Equal(1, fixture.Bank.Calls);
    }

    [Theory]
    [InlineData(true, PaymentStatus.Authorized)]
    [InlineData(false, PaymentStatus.Declined)]
    public async Task CompletedPaymentsAreRetrievableAndTheirKeysCannotBeReused(bool authorized, PaymentStatus status)
    {
        using ServiceFixture fixture = new();
        fixture.Bank.Respond = (_, _) => Task.FromResult(BankHandler.JsonResponse(authorized ? "{\"authorized\":true,\"authorization_code\":\"0bb07405-6d44-4b50-a14f-7ae0beff13ad\"}" : "{\"authorized\":false}"));
        PaymentResult.Completed first = Assert.IsType<PaymentResult.Completed>(
            await fixture.Service.ProcessAsync(TestData.Payment(), "key", "trace-1"));
        PaymentResult retry = await fixture.Service.ProcessAsync(TestData.Payment(), "key", "trace-2");

        Assert.Equal(status, first.Payment.Status);
        Assert.NotEqual(Guid.Empty, first.Payment.Id);
        Assert.Equal("0007", first.Payment.CardNumberLastFour);
        Assert.Equal(PaymentError.IdempotencyConflict, Assert.IsType<PaymentResult.Failed>(retry).Error);
        Assert.Equal(first.Payment, fixture.Service.Get(first.Payment.Id, "get-trace"));
        Assert.Equal(1, fixture.Bank.Calls);
    }

    [Fact]
    public async Task ChangedDetailsConflictWithoutAnotherBankCall()
    {
        using ServiceFixture fixture = new();
        await fixture.Service.ProcessAsync(TestData.Payment(), "key", "trace-1");
        PaymentResult.Failed conflict = Assert.IsType<PaymentResult.Failed>(
            await fixture.Service.ProcessAsync(TestData.Payment(amount: 1051), "key", "trace-2"));
        Assert.Equal(PaymentError.IdempotencyConflict, conflict.Error);
        Assert.Equal(1, fixture.Bank.Calls);
    }

    [Fact]
    public async Task SimultaneousFirstSubmissionsWithTheSameKeyCallBankOnce()
    {
        using ServiceFixture fixture = new();
        TaskCompletionSource start = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<PaymentResult>[] submissions = Enumerable.Range(0, 10).Select(index => Task.Run(async () =>
        {
            await start.Task;
            return await fixture.Service.ProcessAsync(TestData.Payment(), "same-key", $"trace-{index}");
        })).ToArray();
        start.SetResult();
        PaymentResult[] results = await Task.WhenAll(submissions).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Single(results.OfType<PaymentResult.Completed>());
        Assert.Equal(9, results.OfType<PaymentResult.Failed>().Count());
        Assert.All(results.OfType<PaymentResult.Failed>(), failure => Assert.Equal(PaymentError.IdempotencyConflict, failure.Error));
        Assert.Equal(1, fixture.Bank.Calls);
    }

    [Fact]
    public async Task KeyCannotBeReusedDuringOrAfterBankProcessing()
    {
        using ServiceFixture fixture = new();
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Bank.Respond = async (_, _) =>
        {
            started.TrySetResult();
            await release.Task;
            return BankHandler.JsonResponse();
        };

        Task<PaymentResult> first = fixture.Service.ProcessAsync(TestData.Payment(), "key", "first");
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            PaymentResult[] retries = await Task.WhenAll(Enumerable.Range(0, 10).Select(index => Task.Run(() =>
                fixture.Service.ProcessAsync(TestData.Payment(), "key", $"retry-{index}"))));
            Assert.All(retries, result => Assert.Equal(PaymentError.IdempotencyConflict, Assert.IsType<PaymentResult.Failed>(result).Error));

            PaymentResult.Failed changed = Assert.IsType<PaymentResult.Failed>(
                await fixture.Service.ProcessAsync(TestData.Payment(amount: 1), "key", "changed"));
            Assert.Equal(PaymentError.IdempotencyConflict, changed.Error);
            Assert.Equal(1, fixture.Bank.Calls);
        }
        finally
        {
            release.TrySetResult();
        }

        PaymentResult.Completed completed = Assert.IsType<PaymentResult.Completed>(await first);
        Assert.Equal(completed.Payment, fixture.Service.Get(completed.Payment.Id, "get-trace"));
        PaymentResult.Failed duplicate = Assert.IsType<PaymentResult.Failed>(
            await fixture.Service.ProcessAsync(TestData.Payment(), "key", "later"));
        Assert.Equal(PaymentError.IdempotencyConflict, duplicate.Error);
        Assert.Equal(1, fixture.Bank.Calls);
    }

    [Theory]
    [InlineData("bad-request", PaymentError.InvalidRequest)]
    [InlineData("unavailable", PaymentError.BankUnavailable)]
    [InlineData("connection", PaymentError.OtherError)]
    [InlineData("timeout", PaymentError.OtherError)]
    [InlineData("invalid", PaymentError.OtherError)]
    [InlineData("unexpected", PaymentError.OtherError)]
    public async Task KeysRemainUsedAfterBankErrors(string scenario, PaymentError expected)
    {
        using ServiceFixture fixture = new();
        fixture.Bank.Respond = (_, _) => scenario switch
        {
            "bad-request" => Task.FromResult(BankHandler.JsonResponse("{\"error_message\":\"Missing required fields\"}", HttpStatusCode.BadRequest)),
            "connection" => throw new HttpRequestException("connection failed"),
            "timeout" => throw new TaskCanceledException("timed out"),
            "unexpected" => throw new InvalidOperationException("unexpected"),
            "invalid" => Task.FromResult(BankHandler.JsonResponse("{}")),
            _ => Task.FromResult(BankHandler.JsonResponse("{}", HttpStatusCode.ServiceUnavailable))
        };
        PaymentResult.Failed first = Assert.IsType<PaymentResult.Failed>(
            await fixture.Service.ProcessAsync(TestData.Payment(), "key", "trace-1"));
        Assert.Equal(expected, first.Error);
        PaymentResult.Failed duplicate = Assert.IsType<PaymentResult.Failed>(
            await fixture.Service.ProcessAsync(TestData.Payment(), "key", "trace-2"));
        Assert.Equal(PaymentError.IdempotencyConflict, duplicate.Error);
        Assert.Equal("trace-2", duplicate.TraceId);
        Assert.Equal(1, fixture.Bank.Calls);
        Assert.Null(fixture.Service.Get(Guid.Empty, "get-trace"));
    }

    [Fact]
    public async Task DifferentKeysCreateDistinctPaymentsAndPreserveEachRecord()
    {
        using ServiceFixture fixture = new();
        PaymentResult.Completed first = Assert.IsType<PaymentResult.Completed>(
            await fixture.Service.ProcessAsync(TestData.Payment(), "Key", "trace-1"));
        PaymentResult.Completed second = Assert.IsType<PaymentResult.Completed>(
            await fixture.Service.ProcessAsync(TestData.Payment(), "key", "trace-2"));

        Assert.NotEqual(first.Payment.Id, second.Payment.Id);
        Assert.Equal(first.Payment, fixture.Service.Get(first.Payment.Id, "get-trace"));
        Assert.Equal(second.Payment, fixture.Service.Get(second.Payment.Id, "get-trace"));
        Assert.Equal(2, fixture.Bank.Calls);
        Assert.Null(fixture.Service.Get(Guid.NewGuid(), "get-trace"));
    }
}