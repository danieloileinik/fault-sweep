# FaultSweep

[![CI](https://github.com/danieloileinik/fault-sweep/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/danieloileinik/fault-sweep/actions/workflows/ci.yml?query=branch%3Amain)
[![NuGet](https://img.shields.io/nuget/v/FaultSweep.Postgres.svg)](https://www.nuget.org/packages/FaultSweep.Postgres)
[![License](https://img.shields.io/github/license/danieloileinik/fault-sweep.svg)](LICENSE)

FaultSweep checks whether a .NET message handler recovers from interrupted operations and duplicate deliveries. It runs the same message through your application and compares the resulting database state and captured events with one successful delivery. A business invariant can also catch mistakes shared by every run.

## Test your handler

Add `FaultSweep.Postgres` to your .NET 10 integration test project. Point the application and `WithPostgres` at the same isolated database. Apply migrations and seed data first. FaultSweep copies the selected schema (default `public`), resets its tables and sequences before each scenario and restores the seed state at the end.

```csharp
using FaultSweep.Postgres;
using Sweep = global::FaultSweep.FaultSweep;

// Inside your integration test, using the application's service provider:
var message = new CreateOrderCommand(OrderId: Guid.NewGuid(), CustomerId: 42);

await Sweep.For<CreateOrderHandler>(factory.Services)
    .WithPostgres(postgres.GetConnectionString())
    .CheckInvariant("one order for this request", context =>
        context.State["postgres"].Value.GetProperty("orders").EnumerateArray()
            .Count(row => row.GetProperty("id").GetGuid() == message.OrderId) == 1)
    .AssertIdempotentAsync(message);
```

Use `CheckIdempotencyAsync(message)` to inspect the result in code. For MediatR request handlers, keep the application's normal DI registrations. Each delivery goes through `ISender.Send` with its pipeline behaviors in a fresh scope.

## Try a lost Kafka event

The demo handler commits an order and its inbox entry, then publishes an event. If it crashes just after the commit, its retry sees the inbox entry and returns immediately. The order exists, but the publication is missing.

Run the demo from the repository root with the .NET 10 SDK and Docker. Testcontainers starts PostgreSQL and the Kafka adapter captures publications in memory.

```sh
dotnet test tests/FaultSweep.Postgres.Tests/FaultSweep.Postgres.Tests.csproj --filter "FullyQualifiedName~MessageFlowTests" --logger "console;verbosity=detailed"
```

```text
FaultSweep: 2 of 10 scenarios failed (5 operations in clean run).
Failure 1 (2 scenarios)
  Missing Kafka event on "order-placed" (key 1)
    Matching items: 1 in clean run, 0 observed
  Crash after PostgreSQL COMMIT (#4)
    Attempt 1: simulated crash after PostgreSQL COMMIT (#4)
    Attempt 2: handler returned successfully
  Also seen with: Crash before Kafka publish (#5)
Replay: .ReplayScenario(new ReplayScenario(4, FaultKind.CrashAfter))
Full report: ToDetailedString()
```

The assertion groups scenarios with matching evidence. Call `ToDetailedString()` on the result or the caught `FaultSweepAssertionException` to see full values, SQL and the operations each attempt completed. The report uses data from the finished sweep. [Demo tests](https://github.com/danieloileinik/fault-sweep/blob/main/tests/FaultSweep.Postgres.Tests/MessageFlowTests.cs) · [Example handler](https://github.com/danieloileinik/fault-sweep/blob/main/tests/FaultSweep.Postgres.Tests/PostgresKafkaHandler.cs)

When retries run out, inspect the last failed attempt for the application exception. If the target fault was never injected, compare the first attempt with the clean trace. If the clean run records zero operations, check the handler and its adapter registration.

## What gets tested

The clean run records supported operations in order. Each fault scenario injects one failure at one recorded operation during the first delivery. Retries keep the partially modified state and stop when the handler returns successfully or exhausts its allowance (default **3 retries**). FaultSweep then compares snapshots and evaluates invariants. Each enabled duplicate scenario delivers two copies of the same message, sequentially or in parallel, from fresh seed data.

| Fault | What the handler experiences |
|---|---|
| `CrashBefore` | FaultSweep interrupts the delivery before the operation runs. |
| `CrashAfter` | The operation completes, then FaultSweep interrupts the delivery. |
| `AppliedButFailed` | The operation completes, but the caller receives a recoverable timeout. |

A simulated crash stops further effects in that delivery. Scope disposal and `finally` still run, so an open transaction can roll back. Coverage follows the clean trace, the configured checks and the concurrent schedules actually exercised. Keep IDs, clocks, seed data and background work deterministic. `MaxEffects` defaults to 20 operations per delivery and fails the sweep if exceeded.

## Custom state and replay

Use the detailed builder when you need multiple adapters, your own reset, explicit expectations or replay. Register the adapter in the test host first.

```csharp
var sweep = Sweep.For<CreateOrderHandler>(host)
    .WithMessage(message)
    .ResetWith(ResetAndSeedAsync)
    .TrackPostgres(tables: ["orders", "inbox"])
    .TrackProduced()
    .WithDuplicateDeliveries();

var result = await sweep.RunAsync();
result.ShouldHaveNoViolations();
```

- `ResetWith` must restore every relevant store to the same seed before each scenario. Use isolated stores for sweeps running on separate service providers.
- The short API enables both duplicate modes by default. The detailed builder opts in with `WithDuplicateDeliveries()`. `WithOptions` replaces the options, including duplicate modes. On the short API, set `DuplicateDeliveries` explicitly when supplying options.
- `TrackState` captures custom state. `ExpectState` replaces the clean reference for one snapshot. Invariants run after the clean delivery and each scenario, including exhausted retries. Use `CheckInvariantAsync` for read-only queries through `context.Services`.
- To reproduce a failure, apply the reported `ReplayScenario` or `ReplayDuplicates` call before `RunAsync()`. The run repeats the baseline and the selected scenario. An optional `ExpectedEffect` checks that the replay still targets the same operation.
- `MinimizeMessageWith` searches for a smaller failing input. `FaultSweep.Xunit` can generate a regression test with `result.GenerateXunitRegressionTest(options)`. Supply a collection and fixture exposing `CreateSweep(message)`.

## Adapter coverage

| Adapter | Register and track | Coverage and limits |
|---|---|---|
| PostgreSQL | `AddFaultSweepPostgres` + `TrackPostgres`, or automatic `WithPostgres` | Npgsql **10.0.3**. EF Core, Dapper and direct commands. Reads count, each batch is one effect and explicit commits count separately. Tracked tables require primary keys. COPY, ambient transactions and stored-procedure internals are outside coverage. |
| Redis | `AddFaultSweepRedis` + `TrackRedis(prefix: …)` | StackExchange.Redis **3.4.20**. Snapshots preserve binary values and ignore TTL. Transactions, batches, fire-and-forget and unsupported raw commands are rejected. Administrative server writes bypass interception. |
| Kafka | `AddFaultSweepKafka<TKey, TValue>` + `TrackProduced()` | Captures the DI producer in memory. Compares messages including duplicate counts, keys, payloads and headers. Use `ArrayComparison.Set` only when identical duplicates are acceptable. Direct producers bypass capture. |
| MongoDB | `AddFaultSweepMongo` + `TrackMongo(collections: …)` | Resolve the decorated database or wrap collections with `InstrumentMongo`. Supports acknowledged single-document writes. Raw clients bypass interception. Transactions, bulk and multi-document writes are rejected. |

Run the full suite with `dotnet test FaultSweep.slnx` and Docker running. [Build and packaging workflow](https://github.com/danieloileinik/fault-sweep/blob/main/.github/workflows/ci.yml)
