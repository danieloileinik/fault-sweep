# FaultSweep
[![CI](https://github.com/danieloileinik/fault-sweep/actions/workflows/ci.yml/badge.svg)](https://github.com/danieloileinik/fault-sweep/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/v/FaultSweep.svg)](https://www.nuget.org/packages/FaultSweep)
[![License: MIT](https://img.shields.io/github/license/danieloileinik/fault-sweep.svg)](LICENSE)

FaultSweep automates idempotency testing for .NET services that interact with other services. It checks that failures, retries and duplicate deliveries do not repeat business effects, such as charging a payment twice.

Instead of writing a separate test for every failure point, run your real handler through FaultSweep. It injects failures around database operations and message publication, redelivers the same message, and checks the final state. Use it in automated tests to catch duplicate writes, lost updates and incomplete recovery.

## Check idempotency in integration tests

Add `FaultSweep.Postgres` to the integration test project's package references. Use the existing application host and an isolated PostgreSQL database with migrations and seed data already applied:

```csharp
await FaultSweep.For<CreateOrderHandler>(factory.Services)
    .WithPostgres(
        postgres.GetConnectionString(),
        ignore: ["outbox.created_at"])
    .AssertIdempotentAsync(message);
```

Here `factory.Services` is the application's test service provider, `postgres` is the existing Testcontainers PostgreSQL container, and `message` is the request to deliver repeatedly. Import `FaultSweep.Postgres`. To use the short `FaultSweep.For` name, add `using FaultSweep = global::FaultSweep.FaultSweep;` inside the test namespace; alternatively use `global::FaultSweep.FaultSweep.For`.


## MediatR handlers

`IRequestHandler<TRequest, TResponse>` and `IRequestHandler<TRequest>` are detected automatically. The same short API sends each delivery through **`ISender.Send`**, including the baseline, fault retries and duplicate deliveries. It also works with the detailed builder and scenario replay.

FaultSweep resolves `ISender` from each delivery's scope and lets MediatR resolve the handler. This executes the application's registered pipeline behaviors: validation, transaction handling, `SaveChanges`, and any other behaviors surrounding the handler. Do not manually call `handler.Handle` in the sweep. The request response is awaited; verification checks the resulting state and invariants.

The test host must contain the application's normal MediatR registrations and pipeline configuration. FaultSweep does not register MediatR or add transaction behaviors itself. A missing `ISender` fails the baseline instead of falling back to a direct handler call. No extra FaultSweep package or configuration is required, and FaultSweep does not impose a MediatR package version. Integration tests cover MediatR 12.5.0 and 14.1.0. Notifications and streaming handlers are not request sweeps.

## Optional invariants

The short API compares recovered state with a successful single delivery. Business invariants are optional and can be added in the same chain:

```csharp
await FaultSweep.For<CreateOrderHandler>(factory.Services)
    .WithPostgres(postgres.GetConnectionString(), ignore: ["outbox.created_at"])
    .CheckInvariant("exactly one order", context =>
        context.State["postgres"].Value.GetProperty("orders").GetArrayLength() == 1)
    .CheckInvariantAsync("one logical event", async (context, token) =>
    {
        var queries = context.Services.GetRequiredService<IOrderQueries>();
        return await queries.CountEventsAsync(message.OrderId, token) == 1;
    })
    .AssertIdempotentAsync(message);
```

Invariants run after the baseline and each scenario. They can read snapshots or query through a separate DI scope. In the short API, capture the request from the test when a rule needs its fields. The detailed builder also provides typed `context.Message`.

## Configure a detailed sweep

Register your application handler and its dependencies in a test host. This example uses an isolated PostgreSQL database:

```csharp
using FaultSweep;
using FaultSweep.Postgres;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder();
builder.Services.AddScoped<PaymentHandler>();
builder.Services.AddFaultSweepPostgres(testConnectionString);
using var host = builder.Build();

var sweep = FaultSweep.FaultSweep.For<PaymentHandler>(host)
    .WithMessage(new Payment(PaymentId: 42, Amount: 100))
    .ResetWith(ResetAndSeedAsync)
    .TrackPostgres(tables: ["payments", "balances"], ignore: ["created_at"])
    .WithDuplicateDeliveries();

var result = await sweep.RunAsync();
result.ShouldHaveNoViolations();
```

Implement `IMessageHandler<TMessage>`, use a MediatR request handler, or adapt another handler with `HandleWith`. An explicit `HandleWith` overrides automatic MediatR dispatch, so preserve `ISender.Send` when customizing a MediatR sweep. The assertion works with xUnit, NUnit and other test runners.

`ResetAndSeedAsync(IServiceProvider services, CancellationToken token)` returns `ValueTask`. It must reset every relevant store and seed identical data before each scenario. Deliveries use fresh DI scopes, disposed before snapshots and retries. Kafka captures are reset between scenarios and retained across retries.

## Define success

By default, FaultSweep compares the recovered state with a successful delivery. Add business checks before `RunAsync` so that an incorrect handler cannot pass by matching its own baseline:

```csharp
sweep.CheckInvariantAsync("one debit for this payment", async (context, token) =>
{
    var payments = context.Services.GetRequiredService<IPaymentQueries>();
    return await payments.CountDebitsAsync(context.Message.PaymentId, token) == 1;
});
```

Invariants run after the baseline and each scenario, including exhausted retries. `context.Services` provides a separate scope for read-only queries; `context.State` contains snapshots.

- `ExpectState(name, snapshot)` supplies an expected snapshot; message-based factories are supported.
- `WithOptions(new FaultSweepOptions { CompareWithBaseline = false })` disables baseline comparison. Query-based invariants need no snapshots.
- `TrackState` and `TrackSnapshot<TProvider>` capture custom state. `SnapshotComparisonOptions` controls ignored fields, array order and custom comparators.

For a payment, verify the balance, debit count and logical event. Use `TrackProduced(ArrayComparison.Set)` when identical duplicate events are acceptable. An outbox row and a published event are separate facts.

## Scenarios

Scenarios are derived deterministically from a clean baseline execution of the supplied request:

1. Restore the initial data and execute the request once without injected faults. Record the ordered sequence of intercepted operations and the resulting state.
2. Number those operations starting at 1. Each occurrence is a separate **effect**, even when the same SQL is executed more than once. PostgreSQL commands, including reads, are effects; an explicit transaction commit is another effect. An Npgsql batch is one effect. Setup, state restoration, snapshot queries and invariant queries are outside the delivery and are not counted.
3. Create one independent scenario for every pair of **effect index × enabled fault kind**. Restore the initial data before each scenario, then inject exactly one fault at the selected effect during the first delivery.
4. Retry the same request after a failed delivery, using a fresh DI scope and no further injected faults. Keep the partially modified state between attempts. Stop on success or when the redelivery limit is reached, then compare the state and check invariants.
5. Run each enabled duplicate-delivery mode as another independent scenario, starting from the initial data again. These scenarios do not inject faults: they test two deliveries of the same request, either sequentially or concurrently. Each failed delivery can also be retried.

The three default fault kinds are:

| Fault | Behavior at the selected effect |
| --- | --- |
| `CrashBefore` | Throw `SimulatedCrashException` before calling the underlying operation. |
| `CrashAfter` | Await the underlying operation, then throw `SimulatedCrashException` before returning to its caller. |
| `AppliedButFailed` | Await the underlying operation, then throw a recoverable `AppliedButFailedException` derived from `TimeoutException`. |

These are interruptions at operation boundaries, not process termination. `finally` blocks and scope disposal still run. A recorded crash makes the delivery fail even if application code catches the exception; subsequent intercepted operations in that delivery are blocked. An `UPDATE` completed inside an uncommitted transaction can still roll back during disposal; a completed `COMMIT` has already persisted the changes.

The scenario count for a normal sweep is:

```text
N = number of effects recorded in the clean baseline
K = number of enabled distinct fault kinds (default: 3)
D = number of enabled distinct duplicate modes (0, 1 or 2)

fault scenarios = N × K
all scenarios   = N × K + D
```

The short API enables both duplicate modes by default, so **8 effects produce 8 × 3 + 2 = 26 scenarios**: 24 fault scenarios, one sequential-duplicate scenario and one parallel-duplicate scenario. The detailed builder defaults to no duplicate modes, so the same trace produces 24 scenarios until `WithDuplicateDeliveries()` is enabled.

`FaultSweepOptions.FaultKinds` selects the fault kinds; `DuplicateDeliveries` selects the duplicate modes. `WithOptions` replaces all options, including duplicate modes. For the detailed builder, call `WithDuplicateDeliveries()` after `WithOptions` if both modes are wanted. `MaxEffects` defaults to 20 per delivery and is a guard: exceeding it fails the sweep instead of truncating the trace or sampling effects.

FaultSweep explores the effect boundaries observed in the baseline, with one injected fault per scenario. It does not enumerate combinations of faults or every possible concurrent schedule. `ReplayScenario` selects one fault scenario; `ReplayDuplicates` selects one duplicate mode. Both still execute a baseline, and neither runs the remaining scenarios.

## Adapters

`WithPostgres` enables the short API without DI changes. For detailed sweeps and other stores, add the adapters your service needs alongside the core `FaultSweep` package:

| Package | Register | Track state |
| --- | --- | --- |
| `FaultSweep.Postgres` | `AddFaultSweepPostgres(...)` | `TrackPostgres(...)` |
| `FaultSweep.Redis` | `AddFaultSweepRedis(connection)` | `TrackRedis(prefix: ...)` |
| `FaultSweep.Kafka` | `AddFaultSweepKafka<TKey, TValue>()` | `TrackProduced()` |
| `FaultSweep.Mongo` | `AddFaultSweepMongo(database)` | `TrackMongo(collections: [...])` |

- **PostgreSQL:** JIT hooks for Npgsql **10.0.3**, including EF Core, Dapper and direct connections. Commands and explicit commits are separate effects; a batch is one effect. `TransactionScope`, COPY/import, stored-procedure internal boundaries and batch error barriers are unsupported. Tracked tables need primary keys; snapshots share a repeatable-read transaction. Sequences do not roll back.
- **Redis:** JIT hooks for StackExchange.Redis **3.4.20**. Snapshots cover strings, hashes, lists, sets, sorted sets and streams; values are binary-safe, TTL is ignored. Transactions, batches, `FireAndForget`, pub/sub, stream-group mutations and unsupported raw commands are rejected. Administrative server writes bypass interception.
- **Kafka:** captures the DI producer's messages in memory without network sends. Snapshots include keys, payloads, topics, requested partitions and headers. Comparison uses a multiset; nested arrays remain ordered. Transactions are rejected; directly constructed producers bypass capture.
- **MongoDB:** resolve the decorated `IMongoDatabase` or wrap collections with `InstrumentMongo`. Separate raw clients bypass interception. Acknowledged single-document writes are supported; transactions, bulk/multi-document writes, aggregation and administrative writes are rejected during delivery. Snapshots preserve BSON types, sort by `_id` and assume quiescence across collections.

## Diagnose and reproduce

The following test uses the existing application's MediatR registrations and PostgreSQL connection. `message` is the prepared request for `CompleteStepHandler`:

```csharp
await FaultSweep
    .For<CompleteStepHandler>(app.Services)
    .WithPostgres(
        infrastructure.IntegrationPostgres,
        ignore:
        [
            "process_instances.UpdatedAt",
            "step_instances.CompletedAt",
            "event_logs.Id",
            "event_logs.CreatedAt",
        ])
    .AssertIdempotentAsync(message);
```

Example failure output for `CompleteStep_IsIdempotent` (runner-specific stack frames are omitted):

```text
FaultSweep.Reporting.FaultSweepAssertionException
FaultSweep: 8 effects, 26 scenarios, 4 violations.
Minimal scenario: effect #8, CrashAfter, 4 deliveries.

Effect #8 postgres:Commit: CrashAfter
  Violations: RedeliveryLimitExceeded
  Attempts: 4; succeeded: 0; failed: 4
  Exceptions: SimulatedCrashException (1), InvalidOperationException (3)

Effect #8 postgres:Commit: AppliedButFailed
  Violations: RedeliveryLimitExceeded
  Attempts: 4; succeeded: 0; failed: 4
  Exceptions: InvalidOperationException (4)

Duplicate deliveries: Sequential; violations: RedeliveryLimitExceeded
  Attempts: 5; succeeded: 1; failed: 4
  Exceptions: InvalidOperationException (4)

Duplicate deliveries: Parallel; violations: RedeliveryLimitExceeded
  Attempts: 5; succeeded: 1; failed: 4
  Exceptions: InvalidOperationException (4)
Minimal duplicate scenario: Sequential

Call ToDetailedString() for SQL, delivery traces and regression tests.

   at FaultSweep.Reporting.FaultSweepResult.ShouldHaveNoViolations()
   at FaultSweep.FaultSweepBuilder`1.AssertIdempotentAsync[TMessage](TMessage message, CancellationToken cancellationToken)
   at WorkflowScheduler.Tests.Integration.CompleteStepFaultSweepTests.CompleteStep_IsIdempotent()
```

Here the eight baseline effects produce 26 scenarios as described above. Only the four violating scenarios are expanded. `RedeliveryLimitExceeded` means the operation failed to recover within the allowed attempts; it does not by itself prove a state mismatch. This example has no reported state differences. The repeated `InvalidOperationException` types are counted in the compact report; call `ToDetailedString()` to inspect their messages and find the underlying cause.

`result.ToString()` and assertion failures use a compact report by default: the effect and fault scenario, violation kinds, total/successful/failed attempt counts, exception types with occurrence counts, state differences, and invariant failures. SQL targets, individual delivery traces, request payloads and generated regression source are omitted.

Call `result.ToDetailedString()` to include the baseline trace, SQL targets, each violating scenario's deliveries and exception messages, minimization details, and regression source. Formatting reads the captured result and does not rerun the handler or reset the database. After `AssertIdempotentAsync` or `ShouldHaveNoViolations` throws, call `ToDetailedString()` on the caught `FaultSweepAssertionException`; its `Result` property also exposes the captured result.

`MinimalScenario` identifies the earliest failing fault; `MinimalDuplicateScenario` identifies a failing duplicate mode.

- `ReplayScenario(new ReplayScenario(index, kind))` reruns one fault. Supply `ExpectedEffect` to guard against a changed effect.
- `ReplayDuplicates(mode)` reruns duplicates; preserve any test barriers.
- `MinimizeMessageWith(shrink, complexity)` searches smaller failing messages within `MaxMinimizationRuns`, excluding invalid baselines.

`FaultSweep.Xunit` generates source through `result.GenerateXunitRegressionTest(options)`. Set `FixtureTypeName` and `CollectionName` in `XunitRegressionOptions`. The fixture must expose `CreateSweep(message)` with the same reset, adapters and checks. Register `AddFaultSweepXunit(options)` to include source in reports.

## Test boundaries

Use isolated stores, deterministic seeds and stable traces. Control random IDs, clocks and background work. Sweeps sharing a service provider are serialized; separate providers need separate environments.

FaultSweep simulates interruption at supported atomic operations. It does not kill processes, inject network faults or model broker rebalances. A passing sweep verifies the configured state and checks in the scenarios exercised.

## Build and test

```sh
dotnet restore FaultSweep.slnx
dotnet build FaultSweep.slnx -c Release --no-restore -m:1
dotnet test FaultSweep.slnx -c Release --no-build --no-restore -m:1
dotnet pack FaultSweep.slnx -c Release --no-build --no-restore -o artifacts/packages
```

To check the MediatR pipeline tests with an alternative installed version:

```sh
dotnet test tests/FaultSweep.Postgres.Tests/FaultSweep.Postgres.Tests.csproj -p:MediatRTestVersion=14.1.0 --filter FullyQualifiedName~MediatRSweepTests
```

Integration tests require Docker and share containers through collection fixtures, with isolated data per test. CI builds and tests the solution and produces packages. Publishing requires `NUGET_API_KEY` and a tag matching the package version; see the [release workflow](.github/workflows/ci.yml).
