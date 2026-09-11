# Instructions for candidates

This is the .NET version of the Payment Gateway challenge. If you haven't already read this [README.md](https://github.com/cko-recruitment/) on the details of this exercise, please do so now. 

## Template structure
```
src/
    PaymentGateway.Api - a skeleton ASP.NET Core Web API
test/
    PaymentGateway.Api.Tests - an empty xUnit test project
imposters/ - contains the bank simulator configuration. Don't change this

.editorconfig - don't change this. It ensures a consistent set of rules for submissions when reformatting code
docker-compose.yml - configures the bank simulator
PaymentGateway.sln
```

Feel free to change the structure of the solution, use a different test library etc.

## Running the solution

Run the following from the repository root. Prerequisites: a .NET SDK capable of
building `net8.0`, the .NET 8 ASP.NET Core runtime, and Docker for the bank simulator.
The gateway runs locally; Compose hosts only the provided bank simulator.

```bash
docker-compose up -d
dotnet restore PaymentGateway.sln
dotnet build PaymentGateway.sln --no-restore
dotnet dev-certs https --trust
dotnet run --project src/PaymentGateway.Api
```

The launch profile exposes HTTPS at `https://localhost:7092`, HTTP at
`http://localhost:5067` (redirected to HTTPS), and Swagger at
`https://localhost:7092/swagger` in Development. On newer Docker installations,
`docker compose` is equivalent to `docker-compose`.

If an existing container from another checkout already owns the name
`bank_simulator`, run this checkout's simulator with a distinct name (ports
8080 and 2525 must be free):

```bash
docker compose run -d --rm --name payment_gateway_bank_local --service-ports bank_simulator
# When finished:
docker stop payment_gateway_bank_local
```

Bank configuration is in `src/PaymentGateway.Api/appsettings.json`:

| Setting | Default | Environment override |
|---|---|---|
| Base URL | `http://localhost:8080/` | `Bank__BaseUrl` |
| Timeout | 5 seconds | Fixed in `Program.cs` |

There is no custom startup validation; an invalid URL fails when the bank client
is created or used. If hosting the gateway in a container later,
use the bank's service hostname (`http://bank_simulator:8080/`) instead of localhost.

## API examples

Process a payment (amount is in minor units):

```bash
curl -i https://localhost:7092/api/payments \
  -H 'Content-Type: application/json' \
  -H 'Idempotency-Key: example-payment-1' \
  -d '{"cardNumber":"2222405343248877","expiryMonth":12,"expiryYear":2099,"currency":"GBP","amount":1050,"cvv":"012"}'
```

An authorized or declined payment returns `200 OK` with this body (ID varies):

```json
{
  "id": "72f1f6f4-d48f-4c79-a9b2-b8d14f3656a9",
  "status": "Authorized",
  "cardNumberLastFour": "8877",
  "expiryMonth": 12,
  "expiryYear": 2099,
  "currency": "GBP",
  "amount": 1050
}
```

Retrieve it with `GET /api/payments/{id}` using the returned ID. Each
`Idempotency-Key` may be used for only one valid submission. Reusing it returns
`409 idempotency_conflict`, whether the details are identical or different and
whether the first bank call is running, completed or failed. No result is replayed.
Invalid input returns `400` before key registration and does not consume a new
key. A changed invalid request also returns `400`; it cannot modify the original
payment. Use a new key for an intentional new payment. Cards ending in `8`
exercise decline; cards ending in `0` exercise bank unavailability.

| Response | Meaning |
|---|---|
| POST 200 | Authorized or declined payment stored; response status is a string |
| GET 200 | Stored payment, same safe response fields |
| 400 | Invalid input/key or bank 400 with a safe error message |
| 404 | Payment not found or invalid payment ID, with an explanatory JSON body |
| 409 | `idempotency_conflict`: the key has already been used |
| 415 | Unsupported request content type |
| 503 | Bank returned 503 Service Unavailable |
| 500 | Any other error: unexpected bank status/body, connection failure, timeout, or unexpected gateway exception |

Payment-processing errors use `application/problem+json` with HTTP `status`,
`title`, `traceId`, and a stable `code`. Payment-rule validation errors include
`paymentStatus: "Rejected"` and field `errors`. Malformed JSON, invalid JSON types,
and unsupported content types use ASP.NET Core default errors. GET failures use
Problem Details JSON: an invalid payment ID does not match the GUID route and
returns 404 with `Not Found`; an unknown valid ID returns 404 with
`Payment not found` and explains that no payment exists with the supplied ID.
An unmatched route returns 404 with a built-in Problem Details JSON body. Framework errors
do not promise the payment-specific fields or a particular `type` URI.
Local validation failures create no payment and do not consume a new key.
Once the key is registered before bank dispatch, it stays used for the process
lifetime, including after bank errors. The repository stores used keys and
completed payment records; it does not store request fingerprints or error results.

The simulator's documented HTTP outcomes are preserved: bank 200 produces gateway
200 with `Authorized` or `Declined`, bank 400 produces gateway 400 using the
existing `invalid_request` response, and bank 503 produces gateway 503 with
`bank_unavailable`. The current `invalid_request` envelope includes `Rejected`
for both local validation and bank 400; only local validation guarantees no bank
call occurred. No bank failure has a retrievable payment ID.
All other bank/processing failures use HTTP 500 with `code: "any_other_error"`
and `title: "Any other error"`. Unexpected exceptions outside payment processing
use the framework's generic 500 Problem Details response. A timeout can mean the
bank processed the payment. Reusing its key returns 409 without another bank call;
switching to a new key would be a separate submission and could duplicate it.

The bank generates the `authorization_code`. An authorized bank response must
include a non-empty code; otherwise it is treated as any other error. The
merchant-facing response remains the seven fields required by the assessment;
the bank code is not returned or used as the gateway payment ID.

Swagger is available for trying the endpoints, but the controller has no explicit
response annotations, so its generated response documentation is limited. The
response example and status table above describe the implemented contract.
Required fields and cross-field validation rules are enforced by the validator;
nullable request properties in Swagger do not make these fields optional at runtime.

## Tests

The default suite requires no running API, Docker, external bank, or database:

```bash
dotnet test PaymentGateway.sln --logger "console;verbosity=normal"
```

Generate a browser report, including individual results and failure details:

```bash
dotnet test PaymentGateway.sln \
  --logger "console;verbosity=normal" \
  --logger "html;LogFileName=test-results.html" \
  --results-directory ./TestResults
open ./TestResults/test-results.html
```

On platforms other than macOS, open the HTML file using your browser. VS Code
users can also use C# Dev Kit's Testing view after building the solution.

If discovery reports no tests after switching package versions, run
`dotnet clean PaymentGateway.sln` and rebuild to remove stale adapter DLLs.
The existing package versions have been retained. A package-audit `NU1900`
warning caused by an unreachable machine-level NuGet feed does not indicate
a test failure; ensure configured package sources are reachable for restore/audit.

See [DESIGN.md](DESIGN.md) for responsibilities, assumptions, alternatives,
trade-offs, test coverage, and the limits of in-memory idempotency.
