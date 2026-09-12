# Instructions

This is the .NET version of the Payment Gateway challenge. Please read the general instructions for this challenge on [README.md](https://github.com/cko-recruitment/). 

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

## About this solution

The [assessment requirements](https://github.com/cko-recruitment/.github/blob/main/profile/README.md)
cover the payment fields, validation rules and bank simulator behavior. The notes
below describe how to run this implementation and the choices beyond those requirements.

## Run locally

Install the .NET 8 SDK and Docker with Compose, then start the Docker engine.
For VS Code debugging, also install C# Dev Kit.

From the repository root, start the bank simulator first:

```bash
docker-compose up
```

With Docker Compose v2, the equivalent command is `docker compose up`. Wait for
the simulator to start on `http://localhost:8080`, and leave it running while using
the API. Compose runs only the provided bank simulator.

Then press **F5** in VS Code and select the `PaymentGateway.Api` project/profile
if prompted. The original launch profile enables automatic browser opening at
[Swagger](https://localhost:7092/swagger). If the development certificate is not
trusted, run `dotnet dev-certs https --trust` once.

To run the API from another terminal instead:

```bash
dotnet run --project src/PaymentGateway.Api
```

This starts the API without opening a browser; open the Swagger link yourself.
For automatic browser opening from the terminal, use
`dotnet watch --project src/PaymentGateway.Api run`.

The API uses HTTPS on port 7092 and redirects HTTP on port 5067 to HTTPS.
Its bank URL defaults to `http://localhost:8080/` and can be overridden using
`Bank__BaseUrl`. The bank timeout is fixed at five seconds in `Program.cs`.

API logs appear in the terminal or VS Code debug output used to start it. Stop the
API with Ctrl+C or VS Code's Stop button, and stop the bank with Ctrl+C in its
terminal. Run `docker-compose down` to remove the simulator container and network.

## Try the API

Submit a payment:

```bash
curl -i https://localhost:7092/api/payments \
  -H 'Content-Type: application/json' \
  -H 'Idempotency-Key: example-payment-1' \
  -d '{"cardNumber":"2222405343248877","expiryMonth":12,"expiryYear":2099,"currency":"GBP","amount":1050,"cvv":"012"}'
```

`POST /api/payments` returns 200 for an authorized or declined payment. Example:

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

Use the returned ID with `GET /api/payments/{id}` to retrieve the same fields.
The gateway generates the payment GUID; the bank authorization code is not exposed.
Swagger has no explicit response annotations, so use this example and the table
below for the response contract.

| HTTP status | Gateway behavior |
|---|---|
| 400 | Validation/key errors or bank 400: `invalid_request` |
| 404 | Unknown payment GUID: `Payment not found`; invalid GUID or unmatched route: `Not Found` |
| 409 | Reused key: `idempotency_conflict` |
| 415 | Unsupported request content type |
| 503 | Bank 503: `bank_unavailable` |
| 500 | Other bank/processing failures, including connection failure, timeout or invalid bank response: `any_other_error` |

Service errors use `application/problem+json` with `status`, `title`, `code` and
`traceId`. Validation errors also contain field `errors`. The current
`invalid_request` response includes `paymentStatus: "Rejected"` for both local
validation and bank 400; only local validation guarantees no bank call occurred.
Bank failures do not create retrievable payments.

Malformed JSON/types, unsupported content types, GET failures and exceptions
outside payment processing use framework Problem Details bodies without guaranteed
payment-specific fields. An authorized bank response without a non-empty
`authorization_code` is treated as an invalid bank response.

### Validation choices

Beyond the linked rules, this implementation accepts only GBP, USD and EUR;
amounts from 1 to 2147483647; ASCII digits; and expiry years from 1 to 9999.
A card remains valid through its expiry month in UTC. No normalization or Luhn
check is added. Nullable request properties let the validator identify missing
input; they do not make required fields optional.

### Duplicate-submission protection

This is additional assessment scope. The caller generates and retains a mandatory
`Idempotency-Key` for each submission. It need not be a GUID: keys are case-sensitive,
1–128 ASCII letters, digits, hyphens or underscores.

The service validates first, then atomically checks and registers the key before
calling the bank. Invalid input returns 400 without consuming a new key. Reuse
with valid input returns 409, even if the details changed or the first bank call
is still running or failed. Keys stay used; there is no fingerprint check or replay.

A retry must retain its key. If the original response is lost, a retry cannot
recover it with this design. Generating a new key makes a separate submission and
can duplicate a payment whose bank outcome was uncertain.

## Tests

Tests use a fake bank and do not require the API or simulator to be running.
Run the suite with the .NET SDK:

```bash
dotnet test PaymentGateway.sln --logger "console;verbosity=normal"
```

Individual results appear in the terminal; failures produce a non-zero exit code.
For a browser report:

```bash
dotnet test PaymentGateway.sln \
  --logger "html;LogFileName=test-results.html" \
  --results-directory ./TestResults
```

Open `TestResults/test-results.html` in your browser. C# Dev Kit also provides
individual results in VS Code's Testing view.

| Category | Coverage and regression prevented |
|---|---|
| Request validation | Missing fields, boundaries, currency, expiry and key format prevent invalid input reaching the bank |
| Service behavior | Concurrent submissions and key reuse after success/failure prevent duplicate bank calls; invalid input leaves a new key available |
| Bank client | Wire format, status/body handling and timeout checks prevent contract drift and invented decisions |
| Persistence/retrieval | Authorized/declined records, distinct IDs and missing records protect correct lookup |
| API integration | HTTP bodies/statuses, logs, sensitive-data exclusion and caller disconnects protect observable API behavior |

Time is controlled through `TimeProvider`; concurrency tests coordinate requests
without sleeps. Tests exercise behavior rather than dictionary or lock internals.

## Design and trade-offs

```text
PaymentsController
  -> PaymentService
       -> PaymentRequestValidator -> TimeProvider
       -> PaymentsRepository
       -> BankClient -> HttpClient -> bank simulator
```

The controller handles HTTP and `ApiErrors` maps service failures. The service
coordinates validation, duplicate protection, bank dispatch and storage. The bank
client owns bank serialization and response handling. Concrete classes keep this
small solution straightforward; interfaces for every class would add little benefit.

The repository holds a payment dictionary and a used-key set under one lock.
Checking and adding a key together prevents two simultaneous requests from both
reaching the bank; a separate validator existence check would race. Bank I/O runs
outside the lock. Separate POST/GET response classes preserve independent endpoint
models at the cost of repeated fields. Stored response objects are mutable; current
handlers do not modify them after storage.

The bank client has no retries or redirects and finishes dispatched work even if
the caller disconnects, bounded by its timeout. This avoids implicit resubmission,
but does not resolve an uncertain bank outcome. Built-in Problem Details avoids
custom middleware at the cost of different framework and service error fields.

Hosting follows the assessment setup: Docker runs the provided bank simulator,
and the .NET SDK runs the API locally. The original Compose configuration,
simulator files and local Swagger launch settings are retained.

Application logs include outcomes, elapsed bank-attempt time, validation reasons,
duplicate rejection and GET found/not-found results with request traces. They omit
full card numbers, CVVs, raw keys and bank bodies. Framework error-body trace IDs
may differ from service log trace IDs. Expected bank exceptions currently collapse
to one error category, limiting diagnostics.

## Limits and future work

Payments and used keys live in memory: restart loses both, instances do not share
state, and memory grows with submissions. Duplicate protection applies only per key
within one running instance.

Production extensions include durable storage and reconciliation, merchant-scoped
keys and authentication, retention/rate limits, TLS hosting and distributed
observability. Bank diagnostics could add safe exception categories and upstream
status without exposing raw messages or changing the public error contract.
