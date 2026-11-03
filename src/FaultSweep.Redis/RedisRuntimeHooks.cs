using System.Linq.Expressions;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.ExceptionServices;
using FaultSweep.Effects;
using MonoMod.RuntimeDetour;
using StackExchange.Redis;

namespace FaultSweep.Redis;

internal static class RedisRuntimeHooks
{
    private static readonly AsyncLocal<int> Depth = new();
    private static readonly Lazy<Hook[]> Hooks = new(CreateHooks);

    public static void Install() => _ = Hooks.Value;

    private static Hook[] CreateHooks()
    {
        if (!System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported)
        {
            throw new PlatformNotSupportedException(
                "Redis instrumentation requires a JIT-enabled runtime."
            );
        }

        Assembly assembly = typeof(IDatabase).Assembly;
        string? version = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion.Split('+')[0];
        if (version != "3.4.20")
        {
            throw new NotSupportedException(
                $"Redis instrumentation supports StackExchange.Redis 3.4.20; loaded {version}."
            );
        }
        Type database = assembly.GetType("StackExchange.Redis.RedisDatabase", true)!;
        List<Hook> hooks = [];
        try
        {
            Type[] interfaces = [typeof(IDatabase), .. typeof(IDatabase).GetInterfaces()];
            MethodInfo[] methods = interfaces
                .SelectMany(type => database.GetInterfaceMap(type).TargetMethods)
                .Distinct()
                .ToArray();
            foreach (MethodInfo method in methods)
            {
                if (method.IsSpecialName || RedisOperationClassifier.IsRead(method.Name))
                {
                    continue;
                }

                if (
                    method.IsGenericMethod
                    || method.ReturnType.IsByRefLike
                    || method
                        .GetParameters()
                        .Any(parameter =>
                            parameter.ParameterType.IsByRefLike || parameter.ParameterType.IsByRef
                        )
                )
                {
                    throw new NotSupportedException(
                        $"Redis method {method.Name} cannot be instrumented safely."
                    );
                }

                Type[] parameters =
                [
                    method.DeclaringType!,
                    .. method.GetParameters().Select(parameter => parameter.ParameterType),
                ];
                Type originalType = Expression.GetDelegateType([.. parameters, method.ReturnType]);
                Type result = method.ReturnType;
                string wrapperName =
                    result == typeof(void) ? nameof(InvokeVoid)
                    : result == typeof(Task) ? nameof(InvokeTask)
                    : result.IsGenericType && result.GetGenericTypeDefinition() == typeof(Task<>)
                        ? nameof(InvokeTaskResult)
                    : nameof(InvokeResult);
                MethodInfo wrapper = typeof(RedisRuntimeHooks).GetMethod(
                    wrapperName,
                    BindingFlags.Static | BindingFlags.NonPublic
                )!;
                if (wrapper.IsGenericMethodDefinition)
                {
                    wrapper = wrapper.MakeGenericMethod(
                        wrapperName == nameof(InvokeTaskResult)
                            ? result.GetGenericArguments()[0]
                            : result
                    );
                }
                var detour = new DynamicMethod(
                    "FaultSweep_" + method.Name,
                    result,
                    [originalType, .. parameters],
                    typeof(RedisRuntimeHooks).Module,
                    skipVisibility: true
                );
                ILGenerator il = detour.GetILGenerator();
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldstr, method.Name);
                il.Emit(OpCodes.Ldc_I4, parameters.Length);
                il.Emit(OpCodes.Newarr, typeof(object));
                for (int index = 0; index < parameters.Length; index++)
                {
                    il.Emit(OpCodes.Dup);
                    il.Emit(OpCodes.Ldc_I4, index);
                    il.Emit(OpCodes.Ldarg, index + 1);
                    if (parameters[index].IsValueType)
                    {
                        il.Emit(OpCodes.Box, parameters[index]);
                    }

                    il.Emit(OpCodes.Stelem_Ref);
                }
                il.Emit(OpCodes.Call, wrapper);
                il.Emit(OpCodes.Ret);
                hooks.Add(new Hook(method, detour));
            }
            return [.. hooks];
        }
        catch
        {
            foreach (Hook hook in hooks)
            {
                hook.Dispose();
            }

            throw;
        }
    }

    private static object? Call(Delegate original, object?[] arguments)
    {
        try
        {
            Depth.Value++;
            return original.DynamicInvoke(arguments);
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
            throw;
        }
        finally
        {
            Depth.Value--;
        }
    }

    private static (IEffectInterceptor Interceptor, EffectDescriptor Effect)? Operation(
        string method,
        object?[] arguments
    )
    {
        if (
            Depth.Value != 0
            || RedisInstrumentation.Current.Value is not { IsActive: true } interceptor
        )
        {
            return null;
        }

        RedisOperation operation = RedisOperationClassifier.Classify(method, arguments);
        if (operation.Rejection is { } rejection)
        {
            interceptor.ReportInstrumentationFailure(rejection);
        }

        return operation.Effect is { } effect ? (interceptor, effect) : null;
    }

    private static T InvokeResult<T>(Delegate original, string method, object?[] arguments) =>
        Operation(method, arguments) is { } operation
            ? operation.Interceptor.Execute(operation.Effect, () => (T)Call(original, arguments)!)
            : (T)Call(original, arguments)!;

    private static void InvokeVoid(Delegate original, string method, object?[] arguments)
    {
        if (Operation(method, arguments) is { } operation)
        {
            operation.Interceptor.Execute(
                operation.Effect,
                () =>
                {
                    Call(original, arguments);
                }
            );
            return;
        }

        Call(original, arguments);
    }

    private static Task<T> InvokeTaskResult<T>(
        Delegate original,
        string method,
        object?[] arguments
    ) =>
        Operation(method, arguments) is { } operation
            ? operation
                .Interceptor.ExecuteAsync(
                    operation.Effect,
                    _ => new ValueTask<T>((Task<T>)Call(original, arguments)!)
                )
                .AsTask()
            : (Task<T>)Call(original, arguments)!;

    private static Task InvokeTask(Delegate original, string method, object?[] arguments) =>
        Operation(method, arguments) is { } operation
            ? operation
                .Interceptor.ExecuteAsync(
                    operation.Effect,
                    _ => new ValueTask((Task)Call(original, arguments)!)
                )
                .AsTask()
            : (Task)Call(original, arguments)!;
}
