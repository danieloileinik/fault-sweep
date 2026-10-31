using System.Data;
using System.Reflection;
using System.Runtime.CompilerServices;
using FaultSweep.Effects;
using MonoMod.RuntimeDetour;
using Npgsql;

namespace FaultSweep.Postgres;

internal static class NpgsqlRuntimeHooks
{
    // Hooks live for the process lifetime. An AsyncLocal scope makes unrelated work pass through.
    private static readonly Lazy<Hook[]> Hooks = new(
        Install,
        LazyThreadSafetyMode.ExecutionAndPublication
    );
    private static readonly PropertyInfo WrappingBatch = typeof(NpgsqlCommand).GetProperty(
        "WrappingBatch",
        BindingFlags.NonPublic | BindingFlags.Instance
    )!;

    public static void EnsureInstalled() => _ = Hooks.Value;

    private static Hook[] Install()
    {
        if (!RuntimeFeature.IsDynamicCodeSupported)
        {
            throw new PlatformNotSupportedException(
                "FaultSweep PostgreSQL instrumentation requires a JIT-enabled runtime."
            );
        }

        string? version = typeof(NpgsqlCommand)
            .Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion.Split('+')[0];
        if (version != "10.0.3")
        {
            throw new NotSupportedException(
                $"FaultSweep PostgreSQL instrumentation supports Npgsql 10.0.3; loaded {version}."
            );
        }

        List<Hook> installed = [];
        try
        {
            MethodInfo reader = Find(
                typeof(NpgsqlCommand),
                "ExecuteReader",
                typeof(ValueTask<NpgsqlDataReader>),
                [typeof(bool), typeof(CommandBehavior), typeof(CancellationToken)]
            );
            MethodInfo commit = Find(
                typeof(NpgsqlTransaction),
                "Commit",
                typeof(Task),
                [typeof(bool), typeof(CancellationToken)]
            );
            installed.Add(
                new Hook(
                    reader,
                    typeof(NpgsqlRuntimeHooks).GetMethod(
                        nameof(ExecuteReader),
                        BindingFlags.Static | BindingFlags.NonPublic
                    )!
                )
            );
            installed.Add(
                new Hook(
                    commit,
                    typeof(NpgsqlRuntimeHooks).GetMethod(
                        nameof(Commit),
                        BindingFlags.Static | BindingFlags.NonPublic
                    )!
                )
            );
            return [.. installed];
        }
        catch (Exception exception)
        {
            foreach (Hook hook in installed)
            {
                hook.Dispose();
            }

            throw new InvalidOperationException(
                "Failed to install Npgsql instrumentation; the sweep cannot run safely.",
                exception
            );
        }
    }

    private static MethodInfo Find(Type type, string name, Type returnType, Type[] arguments)
    {
        MethodInfo? method = type.GetMethod(
            name,
            BindingFlags.NonPublic | BindingFlags.Instance,
            arguments
        );
        return method?.ReturnType == returnType
            ? method
            : throw new MissingMethodException(
                $"Unsupported Npgsql implementation: {type.Name}.{name}."
            );
    }

    private static ValueTask<NpgsqlDataReader> ExecuteReader(
        Func<
            NpgsqlCommand,
            bool,
            CommandBehavior,
            CancellationToken,
            ValueTask<NpgsqlDataReader>
        > original,
        NpgsqlCommand command,
        bool useAsync,
        CommandBehavior behavior,
        CancellationToken token
    )
    {
        IEffectInterceptor? interceptor = PostgresInstrumentation.Current.Value;
        if (interceptor is not { IsActive: true })
        {
            return original(command, useAsync, behavior, token);
        }

        if (System.Transactions.Transaction.Current is not null)
        {
            interceptor.ReportInstrumentationFailure(
                "FaultSweep requires explicit DbTransaction commits; TransactionScope commits are not instrumented."
            );
        }

        return ExecuteReaderWithFaultAsync(
            original,
            command,
            useAsync,
            behavior,
            token,
            interceptor
        );
    }

    private static async ValueTask<NpgsqlDataReader> ExecuteReaderWithFaultAsync(
        Func<
            NpgsqlCommand,
            bool,
            CommandBehavior,
            CancellationToken,
            ValueTask<NpgsqlDataReader>
        > original,
        NpgsqlCommand command,
        bool useAsync,
        CommandBehavior behavior,
        CancellationToken token,
        IEffectInterceptor interceptor
    )
    {
        NpgsqlDataReader? reader = null;
        try
        {
            NpgsqlBatch? batch = (NpgsqlBatch?)WrappingBatch.GetValue(command);
            if (
                batch is { EnableErrorBarriers: true }
                || batch
                    ?.BatchCommands.Cast<NpgsqlBatchCommand>()
                    .Any(item => item is { AppendErrorBarrier: true }) == true
            )
            {
                interceptor.ReportInstrumentationFailure(
                    "Npgsql batches with error barriers are not atomic; disable error barriers for a sweep."
                );
            }

            var descriptor = new EffectDescriptor(
                "postgres",
                batch is null ? "Command" : "Batch",
                batch is null
                    ? command.CommandText
                    : string.Join(
                        ";\n",
                        batch
                            .BatchCommands.Cast<NpgsqlBatchCommand>()
                            .Select(item => item.CommandText)
                    )
            );
            return await interceptor
                .ExecuteAsync(
                    descriptor,
                    async cancellation =>
                        reader = await original(command, useAsync, behavior, cancellation)
                            .ConfigureAwait(false),
                    token
                )
                .ConfigureAwait(false);
        }
        catch
        {
            if (reader is not null)
            {
                if (useAsync)
                {
                    await reader.DisposeAsync().ConfigureAwait(false);
                }
                else
                {
                    reader.Dispose();
                }
            }

            throw;
        }
    }

    private static Task Commit(
        Func<NpgsqlTransaction, bool, CancellationToken, Task> original,
        NpgsqlTransaction transaction,
        bool useAsync,
        CancellationToken token
    )
    {
        IEffectInterceptor? interceptor = PostgresInstrumentation.Current.Value;
        return interceptor is { IsActive: true }
            ? interceptor
                .ExecuteAsync(
                    new EffectDescriptor("postgres", "Commit"),
                    cancellation => new ValueTask(original(transaction, useAsync, cancellation)),
                    token
                )
                .AsTask()
            : original(transaction, useAsync, token);
    }
}
