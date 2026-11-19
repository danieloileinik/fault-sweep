# FaultSweep

[![CI](https://github.com/danieloileinik/fault-sweep/actions/workflows/ci.yml/badge.svg)](https://github.com/danieloileinik/fault-sweep/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/v/FaultSweep.Postgres.svg)](https://www.nuget.org/packages/FaultSweep.Postgres)
[![License: MIT](https://img.shields.io/github/license/danieloileinik/fault-sweep.svg)](LICENSE)

FaultSweep tests idempotency in .NET message handlers. It simulates failures at recorded database and broker operations, redelivers and duplicates the message, and checks the final state against a clean delivery.

## Try a lost Kafka event

An order handler saves the order and its inbox entry in PostgreSQL, then publishes `OrderPlaced`. FaultSweep simulates a failure just after `COMMIT`. The next delivery sees the inbox entry and returns successfully. The order is saved, and the event is missing.

```text
FaultSweep: 5 effects, 10 scenarios, 2 violations.
StateMismatch
  produced $[0]: Missing, expected …, actual <missing>
  #4 postgres:Commit CrashAfter [minimal] — 1/2 attempts succeeded (SimulatedCrashException)
```

From the repository root, run this with the **.NET 10 SDK** and **Docker running**. Testcontainers starts PostgreSQL, and the demo captures Kafka publications in memory.

```sh
dotnet test tests/FaultSweep.Postgres.Tests/FaultSweep.Postgres.Tests.csproj --filter "FullyQualifiedName~MessageFlowTests" --logger "console;verbosity=detailed"
```

The demo prints the missing-event report and checks that the transactional inbox/outbox case passes. [Demo tests](https://github.com/danieloileinik/fault-sweep/blob/main/tests/FaultSweep.Postgres.Tests/MessageFlowTests.cs) · [Example handler](https://github.com/danieloileinik/fault-sweep/blob/main/tests/FaultSweep.Postgres.Tests/PostgresKafkaHandler.cs)

## Check idempotency in integration tests

Install the PostgreSQL adapter in your test project.

```sh
dotnet add package FaultSweep.Postgres
```

Use the application host and an isolated PostgreSQL database with migrations and seed data applied.

```csharp
[Fact]
public async Task Create_order_is_idempotent()
{
    var message = new CreateOrderCommand(OrderId: Guid.NewGuid(), CustomerId: 42);

    await FaultSweep.For<CreateOrderHandler>(factory.Services)
        .WithPostgres(
            postgres.GetConnectionString(),
            ignore: ["outbox.created_at"])
        .AssertIdempotentAsync(message);
}
```

`factory.Services` is the test service provider, `postgres` is e.g. a Testcontainers container, `message` is the request to deliver repeatedly. Add `using FaultSweep.Postgres;`. Because the class and namespace share a name, add `using FaultSweep = global::FaultSweep.FaultSweep;` in the test namespace or call `global::FaultSweep.FaultSweep.For`.

The short API compares the recovered state with a successful single delivery. Add business invariants in the same chain, so a wrong handler cannot pass by matching its own baseline:

```csharp
    .CheckInvariant("exactly one order", context =>
        context.State["postgres"].Value.GetProperty("orders").GetArrayLength() == 1)
    .CheckInvariantAsync("one logical event", async (context, token) =>
    {
        var queries = context.Services.GetRequiredService<IOrderQueries>();
        return await queries.CountEventsAsync(message.OrderId, token) == 1;
    })
```

Invariants run after the baseline and after every scenario, including exhausted retries. `context.State` holds snapshots; `context.Services` is a separate scope for read-only queries. The short API has no typed message in the context, so capture it from the test; the detailed builder exposes `context.Message`.

## How scenarios work

1. Restore initial data and run the request once with no faults. Record the ordered operations (**effects**) and the resulting state.
2. Number effects from 1. Each occurrence counts separately, even for identical SQL. PostgreSQL commands (including reads) are effects, an explicit commit is another effect, an Npgsql batch is one effect. Operations intercepted during delivery scope disposal also count. Setup, restore, snapshot and invariant queries are excluded.
3. For every pair **effect × fault kind**, restore initial data and inject exactly one fault at that effect during the first delivery.
4. Retry the same request in a fresh DI scope with no further faults, keeping the partially modified state. Stop on success or at the redelivery limit, then compare state and check invariants.
5. Each enabled duplicate mode runs as its own scenario from initial data, with no faults: two deliveries of the same request, sequential or parallel. Failed deliveries can be retried.

| Fault | Behavior at the selected effect |
|---|---|
| `CrashBefore` | Throws `SimulatedCrashException` before the underlying operation. |
| `CrashAfter` | Runs the operation, then throws `SimulatedCrashException` before returning to the caller. |
| `AppliedButFailed` | Runs the operation, then throws a recoverable `AppliedButFailedException` (derives from `TimeoutException`). |

Faults interrupt operations at their boundaries and do not terminate the process: `finally` blocks and scope disposal still run. A recorded crash fails the delivery even if the application catches the exception, and later operations in that delivery are blocked. An uncommitted `UPDATE` can still roll back on disposal; a completed `COMMIT` has already persisted.

```
fault scenarios = N × K          N = effects in the baseline, K = enabled fault kinds (default 3)
all scenarios   = N × K + D      D = enabled duplicate modes (0, 1 or 2)
```

The short API enables both duplicate modes (8 effects → 8 × 3 + 2 = 26 scenarios). The detailed builder enables none by default (24 scenarios) until `WithDuplicateDeliveries()` is called. Choose kinds and modes with `FaultSweepOptions.FaultKinds` and `DuplicateDeliveries`. `WithOptions` replaces all options, duplicate modes included, so call `WithDuplicateDeliveries()` after it. `MaxEffects` (default 20 per delivery) is a guard: exceeding it fails the sweep instead of truncating or sampling the trace.

FaultSweep explores effect boundaries seen in the baseline, one fault per scenario. It does not enumerate fault combinations or all concurrent schedules.

## MediatR

`IRequestHandler<TRequest, TResponse>` and `IRequestHandler<TRequest>` are detected automatically. Every delivery (baseline, fault retries, duplicates) goes through `ISender.Send`, resolved from the delivery's scope, so your registered pipeline behaviors (validation, transactions, `SaveChanges`) run. Do not call `handler.Handle` manually.

- The test host must contain the application's normal MediatR registrations; FaultSweep registers nothing itself and imposes no MediatR version (tested with 12.5.0 and 14.1.0).
- A missing `ISender` fails the baseline, with no fallback to a direct handler call.
- Notifications and streaming handlers are not request sweeps.

## Detailed builder

For other stores, custom state or finer control:

```csharp
var builder = Host.CreateApplicationBuilder();
builder.Services.AddScoped<PaymentHandler>();
builder.Services.AddFaultSweepPostgres(testConnectionString);
using var host = builder.Build();

var sweep = FaultSweep.FaultSweep.For<PaymentHandler>(host)
    .WithMessage(new Payment(PaymentId: 42, Amount: 100))
    .ResetWith(ResetAndSeedAsync)
    .TrackPostgres(tables: ["payments", "balances"], ignore: ["created_at"])
    .WithDuplicateDeliveries()
    .CheckInvariantAsync("one debit for this payment", async (context, token) =>
    {
        var payments = context.Services.GetRequiredService<IPaymentQueries>();
        return await payments.CountDebitsAsync(context.Message.PaymentId, token) == 1;
    });

var result = await sweep.RunAsync();
result.ShouldHaveNoViolations();
```

- **Handlers:** implement `IMessageHandler<TMessage>`, use a MediatR handler, or adapt anything with `HandleWith`. An explicit `HandleWith` overrides automatic MediatR dispatch, so keep `ISender.Send` in it.
- **Reset:** `ResetAndSeedAsync(IServiceProvider, CancellationToken)` returns `ValueTask` and must reset every relevant store and seed identical data before each scenario. Deliveries use fresh DI scopes disposed before snapshots and retries.
- **Success criteria:** `ExpectState(name, snapshot)` supplies an expected snapshot (message-based factories supported); `WithOptions(new FaultSweepOptions { CompareWithBaseline = false })` disables baseline comparison; `TrackState` and `TrackSnapshot<TProvider>` capture custom state; `SnapshotComparisonOptions` controls ignored fields, array order and custom comparators.
- **Events:** an outbox row and a published event are separate facts. Use `TrackProduced(ArrayComparison.Set)` when identical duplicate events are acceptable.

## Adapters

| Package | Register | Track state | Hooked version |
|---|---|---|---|
| `FaultSweep.Postgres` | `AddFaultSweepPostgres(...)` (not needed for `WithPostgres`) | `TrackPostgres(...)` | Npgsql 10.0.3 |
| `FaultSweep.Redis` | `AddFaultSweepRedis(connection)` | `TrackRedis(prefix: ...)` | StackExchange.Redis 3.4.20 |
| `FaultSweep.Kafka` | `AddFaultSweepKafka<TKey, TValue>()` | `TrackProduced()` | DI producer |
| `FaultSweep.Mongo` | `AddFaultSweepMongo(database)` | `TrackMongo(collections: [...])` | decorated `IMongoDatabase` |

- **PostgreSQL:** hooks cover EF Core, Dapper and direct connections. Unsupported: `TransactionScope`, COPY/import, stored-procedure internals, batch error barriers. Tracked tables need primary keys. Automatic restoration rejects tables that use `INHERITS` before resetting data. Snapshots share a repeatable-read transaction. Sequence changes from handler operations survive transaction rollback.
- **Redis:** snapshots cover strings, hashes, lists, sets, sorted sets and streams (binary-safe values, TTL ignored). Rejected: transactions, batches, `FireAndForget`, pub/sub, stream-group mutations, unsupported raw commands. Administrative server writes bypass interception.
- **Kafka:** captures the DI producer's messages in memory with no network sends. Snapshots include keys, payloads, topics, requested partitions and headers; comparison is a multiset (nested arrays stay ordered). Captures and per-partition offsets reset between scenarios and persist across retries. Transactions are rejected; directly constructed producers bypass capture.
- **MongoDB:** resolve the decorated `IMongoDatabase` or wrap collections with `InstrumentMongo`. The decorated database keeps interception when you obtain another database through its `Client` property. Separately constructed raw clients bypass interception. Acknowledged single-document writes are supported; transactions, bulk and multi-document writes, aggregation and administrative writes are rejected. Snapshots preserve BSON types, sort by `_id` and assume quiescence across collections.

## Reading failures and reproducing

The assertion message groups scenarios that report the same violation and show the same details. In this run, both fault scenarios and both duplicate-delivery modes left the counter at `2` instead of `1`.

```text
FaultSweep: 1 effects, 5 scenarios, 4 violations.
StateMismatch
  state $.Counter: ValueMismatch, expected 1, actual 2
  #1 postgres:Command CrashAfter [minimal] — 1/2 attempts succeeded (SimulatedCrashException)
  #1 postgres:Command AppliedButFailed — 1/2 attempts succeeded (AppliedButFailedException)
  Duplicate Sequential [minimal], Duplicate Parallel — 2/2 attempts succeeded
Full report: ToDetailedString()
```

Each group shows up to three state differences and three failed invariants, with values and messages shortened to 100 characters. The `[minimal]` marker identifies the scenario selected for reproduction.

Call `ToDetailedString()` on the result or on the caught `FaultSweepAssertionException` to inspect individual deliveries without rerunning the sweep. This excerpt shows the clean run and the first failing scenario.

```text
FaultSweep: 1 effects, 5 scenarios, 4 violations.
Clean baseline trace:
    - #1 postgres:Command (UPDATE accounts SET balance = balance - 1): Succeeded
Minimal scenario: effect #1, CrashAfter, 2 deliveries.
  Message: {"Amount":10}
  Candidates: 0; budget exhausted: False

Effect #1 postgres:Command (UPDATE accounts SET balance = balance - 1): CrashAfter
  Violations: StateMismatch
  Attempts: 2; succeeded: 1; failed: 1
  Delivery 1: SimulatedCrashException: Simulated CrashAfter at effect #1: postgres:Command (UPDATE accounts SET balance = balance - 1).
    ! #1 postgres:Command (UPDATE accounts SET balance = balance - 1): CrashAfter
  Delivery 2: success
    - #1 postgres:Command (UPDATE accounts SET balance = balance - 1): Succeeded
  state $.Counter: ValueMismatch
    expected: 1
    actual:   2
```

The full report also includes any generated regression tests. For a `RedeliveryLimitExceeded` violation, look at the final failed attempt to see why recovery stopped.

- **`MinimalScenario` / `MinimalDuplicateScenario`** identify the earliest failing fault and a failing duplicate mode.
- **`ReplayScenario(new ReplayScenario(index, kind))`** reruns one fault; pass `ExpectedEffect` to detect a changed trace. **`ReplayDuplicates(mode)`** reruns one duplicate mode; keep any test barriers. Both still run a baseline and skip all other scenarios.
- **`MinimizeMessageWith(shrink, complexity)`** searches for a smaller failing message within `MaxMinimizationRuns`, skipping invalid baselines.
- **xUnit:** `result.GenerateXunitRegressionTest(options)` from `FaultSweep.Xunit` generates a regression test. Set `FixtureTypeName` and `CollectionName` in `XunitRegressionOptions`; the fixture must expose `CreateSweep(message)` with the same reset, adapters and checks. `AddFaultSweepXunit(options)` includes the source in reports.

## Test environment and limits

- Use isolated stores, deterministic seeds and stable traces; control random IDs, clocks and background work. Sweeps sharing a service provider are serialized; separate providers need separate environments.
- FaultSweep simulates interruption at supported atomic operations. It does not kill processes, inject network faults or model broker rebalances.
- A passing sweep proves the configured state and checks only for the scenarios exercised.

## Upgrading from 0.1.x

- In invariant callbacks, use `VerificationContext.IsBaseline` and `VerificationContext.Duplicates` to identify the run type. Fault details are available in `FaultScenarioResult.Scenario` after the sweep.
- Regenerate Redis replay tests whose `ExpectedEffect` describes a raw command such as `Execute` or `ExecuteResp`. The effect now identifies the Redis command and ordered keys.

## Development

```
dotnet restore FaultSweep.slnx
dotnet build FaultSweep.slnx -c Release --no-restore -m:1
dotnet test FaultSweep.slnx -c Release --no-build --no-restore -m:1
dotnet pack FaultSweep.slnx -c Release --no-build --no-restore -o artifacts/packages
```

Check the MediatR pipeline tests against another installed version:

```
dotnet test tests/FaultSweep.Postgres.Tests/FaultSweep.Postgres.Tests.csproj -p:MediatRTestVersion=14.1.0 --filter FullyQualifiedName~MediatRSweepTests
```


## License

MIT
