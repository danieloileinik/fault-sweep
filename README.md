# FaultSweep

FaultSweep tests whether a .NET 10 message handler recovers safely after a failure and redelivery. It runs the handler once to establish a clean baseline, injects failures at effect boundaries, retries the same message, and checks the resulting state. Business correctness is defined by your expected snapshots and invariants; the engine does not infer business rules.

## Try the demo

```sh
dotnet run --project samples/FaultSweep.Demo
```

No Docker is needed for the demo. It detects repeated debits in a broken payment handler and accepts an atomic inbox implementation. The demo also checks sequential and parallel duplicates and exits unsuccessfully if either expected outcome is wrong.

## Packages

| Package | Purpose |
| --- | --- |
| `FaultSweep` | Runner, fault injection, snapshots, invariants, replay and minimization |
| `FaultSweep.Postgres` | Native Npgsql command and commit interception, table snapshots |
| `FaultSweep.Redis` | Native StackExchange.Redis write interception, prefix snapshots |
| `FaultSweep.Kafka` | In-memory `IProducer<TKey,TValue>` and outgoing event snapshots |
| `FaultSweep.Mongo` | MongoDB database/collection decorators and collection snapshots |
| `FaultSweep.Xunit` | Regression source generation using an existing xUnit collection fixture |

The repository builds version 1.0.0. Local packages are produced by `dotnet pack`; availability on NuGet depends on publishing a release.

## Configure a test host

Register your real application handlers and replace only the test dependencies that need capture or decoration. Supply an isolated test database and reset every relevant store before every scenario.

```csharp
services.AddScoped<PaymentHandler>();
services.AddFaultSweepPostgres(postgresConnectionString);
services.AddFaultSweepRedis(redisConnection);
services.AddFaultSweepKafka<string, PaymentSucceeded>();

var result = await FaultSweep.For<PaymentHandler>(host)
    .WithMessage(new Payment(PaymentId: 42, Amount: 100))
    .ResetWith(ResetAndSeedAsync)
    .TrackPostgres(tables: ["payments", "balances"], ignore: ["created_at"])
    .TrackRedis(prefix: "payment:")
    .TrackProduced()
    .WithDuplicateDeliveries()
    .RunAsync(cancellationToken);

result.ShouldHaveNoViolations();
```

`ResetAndSeedAsync` has signature `ValueTask (IServiceProvider services, CancellationToken token)`. Reset database rows, Redis keys and any in-memory application state, then seed identical input data. Produced messages are reset automatically between scenarios and retained across retries. Handler scopes are disposed before snapshots and redelivery, so abandoned database transactions roll back.

Implement `IMessageHandler<TMessage>` or use `.HandleWith((handler, message, token) => ...)` to adapt an existing handler. `FaultSweep.For` accepts an `IHost` or `IServiceProvider`. `ShouldHaveNoViolations()` throws an ordinary assertion exception usable from xUnit, NUnit or other test runners.

## Choose the oracle

Baseline comparison is enabled by default. A test without `CheckInvariant` or `ExpectState` compares only with the clean run.

Add named business conditions using `CheckInvariant` and `CheckInvariantAsync`. They run after the clean run and after each scenario finishes, including exhausted retries. The context exposes the current message, immutable snapshots, attempts and a separate DI scope for read-only queries.

```csharp
sweep.CheckInvariantAsync("one debit for this payment", async (context, token) =>
{
    var payments = context.Services.GetRequiredService<IPaymentQueries>();
    return await payments.CountDebitsAsync(context.Message.PaymentId, token) == 1;
});
```

Use `.WithOptions(new FaultSweepOptions { CompareWithBaseline = false })` for invariant-only verification. No snapshots are required if all checks query state themselves. Use the default `true` to combine invariants and baseline comparison. Set options before `WithDuplicateDeliveries`, since `WithOptions` replaces the full options value.

`ExpectState("balance", StateSnapshot.From(900))` replaces the baseline reference for that tracked snapshot. A factory, such as `ExpectState("balance", message => StateSnapshot.From(1000 - message.Amount))`, is reevaluated when shrinking the message. Explicit expected states and business rules also validate the clean run: a consistently wrong handler does not become its own valid oracle.

For a successful payment, check one debit, the expected balance, one payment row, agreement between Redis and PostgreSQL, and the presence of its logical event. Repeated physical publication may be valid: compare projected logical IDs as a set, rather than asserting one physical send. An outbox row and a published event are distinct facts; track the one your handler is responsible for producing.

## Faults and duplicates

For each recorded effect, the runner tests:

* `CrashBefore`: terminate before the operation.
* `CrashAfter`: apply the operation and terminate immediately afterward.
* `AppliedButFailed`: apply the operation, then throw a recoverable timeout to the caller.

Crashes remain latched even if the handler catches them. `AppliedButFailed` can be handled by application retries. Redelivery uses fresh DI scopes and stops at the first successful delivery or the configured limit.

`WithDuplicateDeliveries()` adds two independent scenarios: two sequential deliveries and two concurrent deliveries of the same message. The second sequential delivery happens even if the first succeeds. Parallel deliveries run in separate scopes behind a common start gate. Their traces are reported in stable delivery order. Snapshots are captured only after both deliveries finish. `DuplicateScenarios` and `DuplicateViolations` are separate from the fault scenario collections, and all contribute to `HasViolations`.

Parallel mode exercises an actual concurrent run, not every possible instruction interleaving. Thread scheduling and uninstrumented application reads can affect which races are observed. Use explicit test barriers for reproducible race regressions; the inbox race test demonstrates this. Fault sweeps require deterministic seeds and effect traces. Do not use random IDs, background work or wall-clock decisions without normalization or test control.

## Adapter boundaries

### PostgreSQL

`AddFaultSweepPostgres` installs process-wide, inactive-by-default runtime hooks scoped to the active sweep. Commands and explicit transaction commits are separate effects. EF Core, Dapper and ADO.NET are tested with `DbDataSource`, factories, DI, pre-created connections and direct `new NpgsqlConnection(...)`. No application source rewriting, profiler process or environment variables are needed.

The hooks require a JIT-enabled runtime and **Npgsql 10.0.3**. Unsupported versions fail explicitly. Native AOT, ambient `TransactionScope` commits, COPY/import operations, stored-procedure internal boundaries and error-barrier batches are outside the supported boundary model. A normal native batch is a single effect, not one effect per SQL statement. Do not share a connection between concurrent deliveries.

`TrackPostgres` requires primary keys, orders rows by the full PK, captures all selected tables in one repeatable-read transaction and excludes configured columns. The overload accepting a connection string supports applications that construct their own connections. Sequence values do not roll back: ignore generated technical IDs only when their relationships and event identity remain verifiable.

### Redis

`AddFaultSweepRedis(connection)` installs scoped runtime hooks for **StackExchange.Redis 3.4.20**. Clients created inside handlers are intercepted too. Only writes are effects. String, hash, list, set, sorted-set and stream snapshots use binary-safe base64 values; TTL is ignored. Prefixes are literal, including Redis glob characters.

Queued batches, transactions, `FireAndForget`, pub/sub, unsupported command families and stream group metadata are rejected. Do not use administrative server APIs to write handler state. Native AOT is unsupported.

### Kafka

`AddFaultSweepKafka<TKey,TValue>()` replaces the DI `IProducer<TKey,TValue>` with a capture producer. It captures payloads, keys, topic, requested partition and headers, excluding broker-generated timestamps and offsets. It performs no network sends. A directly constructed native producer is not automatically intercepted.

`TrackProduced()` uses multiset comparison by default; use `TrackProduced(ArrayComparison.Set)` to allow identical physical duplicates. Nested payload arrays remain ordered unless explicitly configured. Kafka transactions are rejected. Real-broker integration tests independently verify fault boundaries and duplicate semantics with Apache Kafka.

### MongoDB

```csharp
services.AddFaultSweepMongo(mongoDatabase);
// Handlers resolve IMongoDatabase from DI, then call GetCollection<T>().
sweep.TrackMongo(collections: ["payments", "balances"], ignore: ["created_at"]);
```

Decorators follow collections returned by `GetCollection`, `WithReadPreference`, `WithWriteConcern` and other collection-returning methods. An existing collection can be wrapped explicitly with `collection.InstrumentMongo(interceptor)`. A separate raw `new MongoClient(...)` bypasses this decorator; resolve the decorated database in the handler. This is different from the PostgreSQL/Redis runtime hooks.

Acknowledged single-document insert, update, replace, delete and find-and-modify operations are supported, synchronously and asynchronously. Reads are not effects. Transactions, multi-document/bulk writes, aggregation and administrative writes are rejected during a delivery rather than silently bypassing interception. Index creation and seed operations may run during reset, outside the handler.

Snapshots order documents by `_id`, preserve BSON types through canonical Extended JSON, and ignore configured top-level fields. Cross-collection snapshots assume quiescence after deliveries; they are not a multi-collection transaction.

## Normalization and custom state

Use `TrackState(name, capture)` or `TrackSnapshot<TProvider>(name)` for application-specific snapshots. Comparison options support ignored properties, sequence/set/multiset arrays, per-path array modes and custom JSON comparators. Exact JSON paths include `$`, `$.balance` and `$.events`. For related stores, validate consistency using one invariant over their snapshots or a read-only query.

## Replay, minimization and regression tests

`MinimalScenario` identifies the earliest failing single-fault scenario; `MinimalDuplicateScenario` identifies a failing duplicate mode. `.MinimizeMessageWith(shrink, complexity)` explores strictly smaller messages within `MaxMinimizationRuns`. The result is the smallest failing candidate found by that bounded search, not a proof of the global minimum. Candidates whose clean run violates business rules are excluded.

Replay a fault with `.ReplayScenario(new ReplayScenario(index, kind, ExpectedEffect: descriptor))` or duplicates with `.ReplayDuplicates(DuplicateDeliveryMode.Parallel)`. The latter does not reproduce a particular operating-system schedule; preserve any test barriers in the fixture.

Register `AddFaultSweepXunit(new XunitRegressionOptions { FixtureTypeName = "MyTests.PaymentFixture", CollectionName = "Payments" })` to attach source to reports. The existing collection fixture must expose `CreateSweep(message)` with the same reset, adapters, normalization, invariants and message handling. Use `result.GenerateXunitRegressionTest(options)` to obtain source explicitly. Generated tests reuse the fixture and do not create a container per test. A custom `MessageExpression` can replace JSON deserialization.

`result.ToString()` includes fault traces, snapshot differences, named invariant failures, the minimized message and generated source when configured. Baseline violations appear in `BaselineVerification`; they also make `ShouldHaveNoViolations` fail.

## Build, test and release

```sh
dotnet restore FaultSweep.slnx
dotnet build FaultSweep.slnx -c Release --no-restore -m:1
dotnet test FaultSweep.slnx -c Release --no-build --no-restore -m:1
dotnet pack FaultSweep.slnx -c Release --no-build --no-restore -o artifacts/packages
```

Integration tests need Docker. Each collection shares a Testcontainers instance, while tests isolate databases, schemas, prefixes or topics. Images are pinned by digest after checking the official registry: PostgreSQL 18.6, Redis 8.8.4, MongoDB 9.0.4, Apache Kafka 4.4.0 and Ryuk 0.14.0. The acceptance suite includes more than eight faulty/correct handler patterns, ORM/construction styles, an inbox race, mixed-store consistency, logical events and a 20-effect time-budget check.

GitHub Actions runs build, tests, demo and packing, and uploads packages and test results. To publish, configure the repository secret `NUGET_API_KEY`, then push a tag matching the package version, such as `v1.0.0`. The workflow checks the tag before publishing all six packages. Creating local packages or commits does not publish them.

FaultSweep simulates handler interruption. It does not kill the process, inject network failures, reorder messages or model Kafka rebalances. Individual supported effects are assumed atomic. Concurrent sweeps over the same service provider are serialized; different providers must use independently isolated environments.
