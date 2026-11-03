# FaultSweep

FaultSweep automates idempotency testing for .NET 10 services that process messages. It checks that failures, retries and duplicate deliveries do not repeat business effects, such as charging a payment twice.

Instead of writing a separate test for every failure point, run your real handler through FaultSweep. It injects failures around database operations and message publication, redelivers the same message, and checks the final state. Use it in automated tests to catch duplicate writes, lost updates and incomplete recovery.

## Check idempotency in an existing test

Add `FaultSweep.Postgres` to the integration test project's package references. Use the existing application host and an isolated PostgreSQL database with migrations and seed data already applied:

```csharp
await FaultSweep.For<CreateOrderHandler>(factory.Services)
    .WithPostgres(
        postgres.GetConnectionString(),
        ignore: ["outbox.created_at"])
    .AssertIdempotentAsync(message);
```

Here `factory.Services` is the application's test service provider, `postgres` is the existing Testcontainers PostgreSQL container, and `message` is the request to deliver repeatedly. Import `FaultSweep.Postgres`. To use the short `FaultSweep.For` name, add `using FaultSweep = global::FaultSweep.FaultSweep;` inside the test namespace; alternatively use `global::FaultSweep.FaultSweep.For`.

No `AddFaultSweep`, `ResetWith`, application changes or library-specific fixtures are required. The short API discovers tables in the `public` schema, saves the initial rows and sequence positions, and restores them before each scenario and after completion, failure or cancellation. It runs the standard fault scenarios plus sequential and parallel duplicate deliveries. Each delivery uses a fresh DI scope.

`WithPostgres` accepts an optional `schema`. All tables in that schema must have primary keys and no row-level security. Foreign keys must remain within the selected schema; cycles require deferrable constraints. Restoration preserves generated and identity columns and suppresses application triggers while restoring seed data. Use a dedicated test database without background writes.

`ignore` accepts a column name or `table.column` and affects comparison only, never the saved seed. Unknown columns are rejected. Explicitly ignore technical sequence-generated identifiers such as `outbox.id` when gaps after a transaction rollback are acceptable; business identifiers should remain checked.

For ordinary handlers, the short API calls `HandleAsync(message[, cancellationToken])` returning `Task` or `ValueTask`, or `IMessageHandler<TMessage>.HandleAsync`. Handlers are resolved from DI or created with their registered dependencies. Use `CheckIdempotencyAsync(message)` instead of the assertion to inspect the full `FaultSweepResult`.

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

Each intercepted operation is an effect. For every recorded effect, FaultSweep tests:

| Fault | Behavior |
| --- | --- |
| `CrashBefore` | Stop before the operation. |
| `CrashAfter` | Apply the operation, then stop. |
| `AppliedButFailed` | Apply the operation, then return a recoverable timeout. |

A simulated crash ends the delivery even if the handler catches the exception. Defaults are 20 effects per delivery and three redelivery attempts, stopping on success. Configure these through `FaultSweepOptions`.

The short API includes two sequential and two concurrent deliveries by default. In the detailed builder, `WithDuplicateDeliveries()` enables them. Concurrency exercises one schedule; use test barriers for reproducible races. `WithOptions` replaces all options, including the duplicate-delivery modes; in the detailed builder set options before `WithDuplicateDeliveries`.

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
