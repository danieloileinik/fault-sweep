# FaultSweep

FaultSweep automates idempotency testing for .NET 10 services that process messages. It checks that failures, retries and duplicate deliveries do not repeat business effects, such as charging a payment twice.

Instead of writing a separate test for every failure point, run your real handler through FaultSweep. It injects failures around database operations and message publication, redelivers the same message, and checks the final state. Use it in automated tests to catch duplicate writes, lost updates and incomplete recovery.

## Test your handler

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

Implement `IMessageHandler<TMessage>` or adapt your handler with `HandleWith`. The assertion works with xUnit, NUnit and other test runners.

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

Each intercepted operation is an effect. For every recorded effect, FaultSweep tests:

| Fault | Behavior |
| --- | --- |
| `CrashBefore` | Stop before the operation. |
| `CrashAfter` | Apply the operation, then stop. |
| `AppliedButFailed` | Apply the operation, then return a recoverable timeout. |

A simulated crash ends the delivery even if the handler catches the exception. Defaults are 20 effects per delivery and three redelivery attempts, stopping on success. Configure these through `FaultSweepOptions`.

`WithDuplicateDeliveries()` adds two sequential and two concurrent deliveries. Concurrency exercises one schedule; use test barriers for reproducible races. Set options before `WithDuplicateDeliveries`, because `WithOptions` replaces all options.

## Adapters

Add the adapters your service needs alongside the core `FaultSweep` package:

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

`result.ToString()` reports traces, differences and invariant failures. `MinimalScenario` identifies the earliest failing fault; `MinimalDuplicateScenario` identifies a failing duplicate mode.

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

Integration tests require Docker and share containers through collection fixtures, with isolated data per test. CI builds and tests the solution and produces packages. Publishing requires `NUGET_API_KEY` and a tag matching the package version; see the [release workflow](.github/workflows/ci.yml).
