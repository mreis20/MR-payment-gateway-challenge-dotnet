# Design and contract decisions

The original README content and `REQUIREMENTS.md` are the assessment sources of
truth. Added README sections explain the implementation. The agreed additional
scope is duplicate-submission protection using a mandatory, single-use
`Idempotency-Key`. The bank's defined 200, 400 and 503 statuses are preserved.

## Responsibilities and dependency flow

```text
PaymentsController
  -> PaymentService
       -> PaymentRequestValidator -> TimeProvider
       -> PaymentsRepository
       -> BankClient -> HttpClient -> bank simulator
```

The controller handles HTTP input/output. The validator checks payment fields and
key format. The service validates, registers the key, calls the bank and saves
successful bank decisions. `ApiErrors` maps service failures to JSON error responses.
GET uses the existing GUID route and retrieves the payment through the service.

`PaymentsRepository` has three operations: `TryAddIdempotencyKey`, `Add` and `Get`.
It owns a dictionary of payment records and a set of used keys, protected by one
lock. It does not depend on the request model or service result/error types.

## Single-use key behavior

1. Validate the payment and key format. Invalid input returns 400 without calling
   the bank or consuming a new key.
2. Under the repository lock, check and add the key in one operation. If it is
   already present, return 409 with `idempotency_conflict` and the message
   `Idempotency key has already been used`.
3. Call the bank outside the lock.
4. Save an authorized or declined payment for GET retrieval. Bank errors create
   no payment record. The key remains used in either case.

The same rule applies to identical and changed requests, and to calls made during
or after the first bank operation. There is no replay, request fingerprint,
pending/completed attempt record, or stored error result. Changed invalid requests
return validation 400 before the key check; they do not alter the existing payment.
Expired requests are validated normally, even if their key was previously used.

A validator-only existence check followed by a later insertion would be unsafe:
two requests could both see an unused key and call the bank. The service invokes
the repository's atomic check-and-add operation after input validation. Keeping
this operation out of the validator also keeps validation free of state changes.

## Decisions, reasons, alternatives and trade-offs

| Decision | Why | Alternative considered | Trade-off |
|---|---|---|---|
| Reject every reused key; store only used keys | Meet the revised single-use rule and simplify the repository | Fingerprint requests and replay stored results | Retries return 409, not the original response; a lost response cannot be recovered through another POST |
| Check and add a key under one lock before bank dispatch | Prevent concurrent duplicate bank calls | Check existence in the validator and add later | A short state-changing repository operation remains necessary; validation itself stays stateless |
| Retain keys after bank errors or caller cancellation | An uncertain outcome must not cause another submission with the same key | Remove keys after errors | A failed attempt consumes its key for the process lifetime; no automatic retry is offered |
| Keep one repository with a payment dictionary and a key set | Support ID lookup and duplicate detection without extra services | Separate stores, database or concurrency framework | State is process-local; one lock protects all short operations and is never held during bank I/O |
| Keep separate mutable POST and GET response classes | Preserve original names and independent endpoint models | Shared response type, immutable DTOs or separate storage models | Seven fields are repeated. Repository callers receive shared mutable POST objects; current handlers do not mutate them, but there is no immutability guarantee |
| Keep concrete services and framework boundaries | Separate HTTP, validation, orchestration and storage responsibilities | Interfaces for every class, mediator or generic repository | A few concrete dependencies; tests replace HTTP transport and time using existing framework support |
| Use TimeProvider and nullable request fields | Test expiry deterministically and distinguish missing fields from zero | DateTime directly or validation packages | Explicit validation is needed; nullable properties represent untrusted input, not optional fields |
| Use built-in JSON error and exception support | Provide error bodies without custom middleware | A custom error envelope for every framework failure | Framework errors differ from service errors and do not promise custom payment fields |
| Keep Get(Guid id) and its GUID route constraint | Preserve the existing API signature | Parse string IDs explicitly | Invalid IDs produce a routing 404 with Not Found; unknown valid IDs produce Payment not found with explanatory detail |
| Keep bank URL and five-second timeout in Program.cs | Keep startup small and avoid options plumbing | Options classes and custom startup validation | URL remains configurable; timeout changes require code changes and invalid URLs fail when used |
| Keep redirects disabled and finish dispatched work independently of the caller | Avoid implicit resubmission and abandonment on disconnect | Follow redirects or cancel with the caller | The bank operation may continue until its timeout even when the caller stops waiting |
| Keep controller free of Swagger response annotations | Preserve the preferred minimal controller | Add response annotations or Swagger filters | Generated response metadata is limited; the added README documents the actual contract |

## Validation and public responses

- All six payment fields and the key are required. Card number is 14–19 ASCII
  digits; CVV is 3–4 ASCII digits. No normalization or Luhn check is added.
- Expiry month is 1–12, year is 1–9999, and a card is valid through its expiry
  month in UTC. Current-month acceptance is the chosen interpretation of future
  expiry in the requirements.
- Currency is exactly GBP, USD or EUR. Amount is a JSON integer from 1 through
  2147483647 in minor units. Positive-only amounts are an assessment assumption.
- Keys are case-sensitive, 1–128 ASCII letters, digits, hyphens or underscores.
  Different keys represent separate submissions, even for identical payments.
- POST and GET success return the same seven safe fields through separate models.
  Full card number, CVV and bank authorization code are not returned.
- Bank 200 authorized requires a non-empty bank-generated authorization code and
  becomes gateway 200 Authorized. Bank 200 unauthorized becomes 200 Declined.
- Bank 400 remains 400 using the current `invalid_request` response; this existing
  envelope includes `Rejected`, as do local validation failures. Only local
  validation guarantees no bank call. Bank 503 remains 503 / `bank_unavailable`.
- Other bank/processing failures use 500 / `any_other_error`. Unexpected failures
  outside processing use the framework's generic 500 Problem Details response.
- Local validation errors include field errors and consume no new key. Malformed
  JSON/type errors also return 400 without a bank call. Unsupported content types
  return 415. Unknown IDs/routes return 404 with JSON Problem Details.

## Logging and assessment competencies

| Competency | Evidence and limits |
|---|---|
| Observability | Logs include payment outcome, elapsed time and request trace. Validation rejection logs include static field/rule messages; duplicate rejection logs include the current trace without the raw key. GET service logs include payment ID, request trace and found/not-found messages |
| Packaging/Hosting | Existing .NET solution runs locally; Compose hosts the provided simulator. README covers prerequisites, bank configuration and run/test commands. No gateway container or deployment platform is added |
| Technical Documentation | Original assessment instructions are preserved. Added README sections and this document describe implemented behavior, assumptions and limits |
| API Design | Existing routes, bank statuses and safe separate response models; explanatory JSON error bodies and a single conflict category for reused keys |
| Code Design | Concrete services, one repository and built-in HTTP/time/logging support; no new abstraction or package |
| Testing Mindset | Behavioral tests cover validation, retrieval, bank outcomes, simultaneous submissions, key reuse after success/failure and caller cancellation. Log tests check useful fields and sensitive-data exclusion |

GET passes `HttpContext.TraceIdentifier` to the service. The framework may use an
Activity ID in its default Problem Details body; equality with that body trace is
not promised. No raw card/CVV values, bank response bodies or idempotency keys are
added to application log messages. Detailed bank exception diagnostics are deferred.

## Tests and regressions covered

| Category | Checks | Regression prevented |
|---|---|---|
| Request validation | Missing/null fields, digit/length boundaries, currencies, amount limits, combined expiry and key format | Invalid data reaching the bank; loss of leading zeros; date-dependent tests |
| Service behavior | Invalid input consumes no key; same/changed valid requests with a used key conflict; simultaneous first submissions call the bank once; keys stay used during processing and after success/failure | Duplicate bank submissions and accidental key reuse |
| Bank client | Exact bank body/route, invariant expiry formatting, authorization code, 400/503 mapping, unexpected responses and real timeout | Wire-contract drift, invented declines, retries or unbounded waits |
| Persistence/retrieval | Authorized/declined records retain fields; distinct keys produce distinct IDs; unknown IDs return no record | Missing or overwritten payments and incorrect lookup |
| API integration | POST/GET bodies; 200/400/404/409/415/500/503; clear duplicate errors, rejection logs, GET logs and disconnect behavior | Incorrect HTTP responses, sensitive-data exposure and cancellation of a dispatched bank call |

The default suite needs no running API, bank, Docker or database. Concurrent tests
use coordination signals rather than sleeps. Tests do not assert dictionary/lock
implementation or immutability of the mutable stored response objects.

## Assessment limits and future work

Used keys and payment records live only in this process. Restart/crash loses both;
multiple instances do not share state and memory grows with submissions. The
promise is at most one bank submission per key per running instance, not durable
exactly-once processing. A timeout may occur after the bank processed a payment;
using a new key can submit it again. Authentication, merchant-scoped keys, durable
shared storage, recovery/reconciliation, retention, rate limits, deployment
hardening and distributed observability remain production concerns outside scope.

Future bank diagnostics should log safe exception types or bounded categories,
upstream HTTP status when available and request traces for timeout, connection,
JSON or unexpected-response failures. Do not log raw exception messages, bank
bodies, credentials or keys. Keep the existing generic public 500 response and
single-use key behavior. Today the service logs types only for exceptions escaping
the bank client; expected bank exceptions collapse to OtherError. Future tests
should verify diagnostic fields and sensitive-data exclusion.
