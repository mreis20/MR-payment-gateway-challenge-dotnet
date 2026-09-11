using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

using PaymentGateway.Api.Enums;
using PaymentGateway.Api.Models.Requests;

namespace PaymentGateway.Api.Services;

public class BankClient(HttpClient httpClient)
{
    public async Task<BankResult> SubmitAsync(PostPaymentRequest request)
    {
        try
        {
            // Finish an already submitted payment even if the caller disconnects.
            // HttpClient.Timeout bounds the bank operation independently.
            using HttpResponseMessage response = await httpClient.PostAsJsonAsync("payments", new
            {
                card_number = request.CardNumber,
                expiry_date = string.Create(CultureInfo.InvariantCulture, $"{request.ExpiryMonth:00}/{request.ExpiryYear:0000}"),
                currency = request.Currency,
                amount = request.Amount,
                cvv = request.Cvv
            }, CancellationToken.None);

            if (response.StatusCode == HttpStatusCode.BadRequest)
            {
                return BankResult.BadRequest;
            }

            if (response.StatusCode == HttpStatusCode.ServiceUnavailable)
            {
                return BankResult.Unavailable;
            }

            if (response.StatusCode != HttpStatusCode.OK)
            {
                return BankResult.OtherError;
            }

            BankResponse? body = await response.Content.ReadFromJsonAsync<BankResponse>();
            return body?.Authorized switch
            {
                true when !string.IsNullOrWhiteSpace(body.AuthorizationCode) => BankResult.Authorized,
                true => BankResult.OtherError,
                false => BankResult.Unauthorized,
                null => BankResult.OtherError
            };
        }
        catch (Exception exception) when (exception is OperationCanceledException or HttpRequestException or JsonException)
        {
            return BankResult.OtherError;
        }
    }

    private class BankResponse
    {
        [JsonPropertyName("authorized")]
        public bool? Authorized { get; set; }

        [JsonPropertyName("authorization_code")]
        public string? AuthorizationCode { get; set; }
    }
}