using System.ComponentModel;
using System.Reflection;
using System.Runtime.ExceptionServices;
using FaultSweep.Effects;
using MongoDB.Driver;

namespace FaultSweep.Mongo;

/// <summary>Dispatch proxy infrastructure. Use AddFaultSweepMongo or InstrumentMongo to create an adapter.</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public class MongoProxy : DispatchProxy
{
    private static readonly HashSet<string> Writes =
    [
        "InsertOne",
        "UpdateOne",
        "ReplaceOne",
        "DeleteOne",
        "FindOneAndUpdate",
        "FindOneAndReplace",
        "FindOneAndDelete",
    ];
    private static readonly HashSet<string> Reads =
    [
        "FindSync",
        "FindAsync",
        "CountDocuments",
        "CountDocumentsAsync",
        "EstimatedDocumentCount",
        "EstimatedDocumentCountAsync",
        "Distinct",
        "DistinctAsync",
        "DistinctMany",
        "DistinctManyAsync",
        "ToString",
        "Equals",
        "GetHashCode",
    ];
    private object inner = null!;
    private IEffectInterceptor interceptor = null!;

    internal static object Wrap(Type contract, object inner, IEffectInterceptor interceptor)
    {
        if (inner is MongoProxy)
        {
            return inner;
        }

        object proxy = Create(contract, typeof(MongoProxy));
        var implementation = (MongoProxy)proxy;
        implementation.inner = inner;
        implementation.interceptor = interceptor;
        return proxy;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        MethodInfo method = targetMethod ?? throw new ArgumentNullException(nameof(targetMethod));
        object?[] arguments = args ?? [];
        string operation = method.Name.EndsWith("Async", StringComparison.Ordinal)
            ? method.Name[..^5]
            : method.Name;
        if (
            interceptor.IsActive
            && arguments.OfType<IClientSessionHandle>().Any(session => session.IsInTransaction)
        )
        {
            interceptor.ReportInstrumentationFailure(
                "MongoDB transactions require commit instrumentation and are not supported."
            );
        }

        if (Writes.Contains(operation))
        {
            if (interceptor.IsActive)
            {
                object? settings = inner.GetType().GetProperty("Settings")?.GetValue(inner);
                if (settings is MongoCollectionSettings { WriteConcern.IsAcknowledged: false })
                {
                    interceptor.ReportInstrumentationFailure(
                        "MongoDB unacknowledged writes cannot be verified."
                    );
                }
            }

            var effect = new EffectDescriptor(
                "mongo",
                operation,
                inner.GetType().GetProperty("CollectionNamespace")?.GetValue(inner)?.ToString()
            );
            CancellationToken token = arguments.OfType<CancellationToken>().LastOrDefault();
            if (method.ReturnType == typeof(Task))
            {
                return interceptor
                    .ExecuteAsync(effect, _ => new ValueTask((Task)Call(method, arguments)!), token)
                    .AsTask();
            }
            if (
                method.ReturnType.IsGenericType
                && method.ReturnType.GetGenericTypeDefinition() == typeof(Task<>)
            )
            {
                return typeof(MongoProxy)
                    .GetMethod(
                        nameof(ExecuteAsync),
                        BindingFlags.NonPublic | BindingFlags.Instance
                    )!
                    .MakeGenericMethod(method.ReturnType.GetGenericArguments()[0])
                    .Invoke(this, [effect, method, arguments, token]);
            }

            return interceptor.Execute(effect, () => Call(method, arguments));
        }

        bool returnsCollection =
            method.ReturnType.IsInterface
            && (
                method.ReturnType.IsGenericType
                    && method.ReturnType.GetGenericTypeDefinition() == typeof(IMongoCollection<>)
                || method
                    .ReturnType.GetInterfaces()
                    .Any(type =>
                        type.IsGenericType
                        && type.GetGenericTypeDefinition() == typeof(IMongoCollection<>)
                    )
            );
        bool returnsDatabase = method.ReturnType == typeof(IMongoDatabase);
        if (
            interceptor.IsActive
            && !method.IsSpecialName
            && !Reads.Contains(method.Name)
            && !returnsCollection
            && !returnsDatabase
        )
        {
            interceptor.ReportInstrumentationFailure(
                $"MongoDB operation {method.Name} is not supported. Use acknowledged single-document writes and tracked collections."
            );
        }
        if (
            interceptor.IsActive
            && method.IsSpecialName
            && method.Name is "get_Indexes" or "get_SearchIndexes"
        )
        {
            interceptor.ReportInstrumentationFailure(
                "MongoDB index management must run during reset or seed, outside the handler."
            );
        }

        object? result = Call(method, arguments);
        return result is not null && (returnsCollection || returnsDatabase)
            ? Wrap(method.ReturnType, result, interceptor)
            : result;
    }

    private Task<T> ExecuteAsync<T>(
        EffectDescriptor effect,
        MethodInfo method,
        object?[] arguments,
        CancellationToken token
    )
    {
        return interceptor
            .ExecuteAsync(effect, _ => new ValueTask<T>((Task<T>)Call(method, arguments)!), token)
            .AsTask();
    }

    private object? Call(MethodInfo method, object?[] arguments)
    {
        try
        {
            return method.Invoke(inner, arguments);
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
            throw;
        }
    }
}
