using System.ComponentModel;
using System.Reflection;
using System.Runtime.ExceptionServices;
using FaultSweep.Effects;

namespace FaultSweep.Mongo;

/// <summary>Dispatch proxy infrastructure. Use AddFaultSweepMongo or InstrumentMongo to create an adapter.</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public class MongoProxy : DispatchProxy
{
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
        MongoOperation operation = MongoOperationClassifier.Classify(method, inner, arguments);
        if (interceptor.IsActive && operation.Rejection is { } rejection)
        {
            interceptor.ReportInstrumentationFailure(rejection);
        }

        if (operation.Effect is { } effect)
        {
            return InvokeEffect(effect, method, arguments);
        }

        object? result = Call(method, arguments);
        return result is not null && operation.WrapResult
            ? Wrap(method.ReturnType, result, interceptor)
            : result;
    }

    private object? InvokeEffect(EffectDescriptor effect, MethodInfo method, object?[] arguments)
    {
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
                .GetMethod(nameof(ExecuteAsync), BindingFlags.NonPublic | BindingFlags.Instance)!
                .MakeGenericMethod(method.ReturnType.GetGenericArguments()[0])
                .Invoke(this, [effect, method, arguments, token]);
        }

        return interceptor.Execute(effect, () => Call(method, arguments));
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
