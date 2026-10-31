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
            throw new PlatformNotSupportedException("Redis instrumentation requires a JIT-enabled runtime.");
        }

        Assembly assembly = typeof(IDatabase).Assembly;
        string? version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0];
        if (version != "3.4.20")
        {
            throw new NotSupportedException($"Redis instrumentation supports StackExchange.Redis 3.4.20; loaded {version}.");
        }
        Type database = assembly.GetType("StackExchange.Redis.RedisDatabase", true)!;
        List<Hook> hooks = [];
        try
        {
            Type[] interfaces = [typeof(IDatabase), .. typeof(IDatabase).GetInterfaces()];
            MethodInfo[] methods = interfaces.SelectMany(type => database.GetInterfaceMap(type).TargetMethods).Distinct().ToArray();
            foreach (MethodInfo method in methods)
            {
                if (method.IsSpecialName || RedisWriteClassifier.IsRead(method.Name))
                {
                    continue;
                }

                if (method.IsGenericMethod || method.ReturnType.IsByRefLike ||
                    method.GetParameters().Any(parameter => parameter.ParameterType.IsByRefLike || parameter.ParameterType.IsByRef))
                {
                    throw new NotSupportedException($"Redis method {method.Name} cannot be instrumented safely.");
                }

                Type[] parameters = [method.DeclaringType!, .. method.GetParameters().Select(parameter => parameter.ParameterType)];
                Type originalType = Expression.GetDelegateType([.. parameters, method.ReturnType]);
                Type result = method.ReturnType;
                string wrapperName = result == typeof(void) ? nameof(InvokeVoid)
                    : result == typeof(Task) ? nameof(InvokeTask)
                    : result.IsGenericType && result.GetGenericTypeDefinition() == typeof(Task<>) ? nameof(InvokeTaskResult) : nameof(InvokeResult);
                MethodInfo wrapper = typeof(RedisRuntimeHooks).GetMethod(wrapperName, BindingFlags.Static | BindingFlags.NonPublic)!;
                if (wrapper.IsGenericMethodDefinition)
                {
                    wrapper = wrapper.MakeGenericMethod(wrapperName == nameof(InvokeTaskResult) ? result.GetGenericArguments()[0] : result);
                }
                var detour = new DynamicMethod("FaultSweep_" + method.Name, result,
                    [originalType, .. parameters], typeof(RedisRuntimeHooks).Module, skipVisibility: true);
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

    private static IEffectInterceptor? Interceptor(string method, object?[] arguments)
    {
        if (Depth.Value != 0)
        {
            return null;
        }

        IEffectInterceptor? interceptor = RedisInstrumentation.Current.Value;
        if (interceptor is not { IsActive: true })
        {
            return null;
        }

        string name = method.EndsWith("Async", StringComparison.Ordinal) ? method[..^5] : method;
        if (name is "CreateTransaction" or "CreateBatch" || arguments[0]!.GetType().Name is "RedisTransaction" or "RedisBatch")
        {
            interceptor.ReportInstrumentationFailure("Redis transactions and queued batches are not supported by fault injection.");
        }
        if (name == "Publish" || name.StartsWith("Array", StringComparison.Ordinal) || name.StartsWith("VectorSet", StringComparison.Ordinal) ||
            name is "StreamReadGroup" or "StreamAcknowledge" or "StreamAcknowledgeAndDelete" or "StreamAutoClaim" or "StreamAutoClaimIdsOnly" or "StreamClaim" or "StreamClaimIdsOnly" or "StreamCreateConsumerGroup" or "StreamDeleteConsumerGroup" or "StreamDeleteConsumer" or "StreamConsumerGroupSetPosition" or "StreamNegativeAcknowledge")
        {
            interceptor.ReportInstrumentationFailure("This Redis operation has state outside the supported key snapshot types.");
        }
        if (arguments.OfType<CommandFlags>().Any(flags => flags.HasFlag(CommandFlags.FireAndForget)))
        {
            interceptor.ReportInstrumentationFailure("Redis FireAndForget cannot confirm whether an effect was applied.");
        }
        if (name is "Execute" or "ExecuteResp" && !RedisWriteClassifier.RawCommandWrites(arguments[1]?.ToString() ?? ""))
        {
            return null;
        }

        return interceptor;
    }

    private static EffectDescriptor Describe(string method, object?[] arguments)
    {
        string name = method.EndsWith("Async", StringComparison.Ordinal) ? method[..^5] : method;
        string target = arguments.Skip(1).OfType<RedisKey>().Select(key => key.ToString()).FirstOrDefault() ?? "";
        return new EffectDescriptor("redis", name, target);
    }

    private static T InvokeResult<T>(Delegate original, string method, object?[] arguments)
    {
        IEffectInterceptor? interceptor = Interceptor(method, arguments);
        return interceptor is null ? (T)Call(original, arguments)! : interceptor.Execute(Describe(method, arguments), () => (T)Call(original, arguments)!);
    }

    private static void InvokeVoid(Delegate original, string method, object?[] arguments)
    {
        IEffectInterceptor? interceptor = Interceptor(method, arguments);
        if (interceptor is null)
        {
            Call(original, arguments);
            return;
        }
        interceptor.Execute(Describe(method, arguments), () => { Call(original, arguments); });
    }

    private static Task<T> InvokeTaskResult<T>(Delegate original, string method, object?[] arguments)
    {
        IEffectInterceptor? interceptor = Interceptor(method, arguments);
        return interceptor is null ? (Task<T>)Call(original, arguments)! : interceptor.ExecuteAsync(Describe(method, arguments),
            _ => new ValueTask<T>((Task<T>)Call(original, arguments)!)).AsTask();
    }

    private static Task InvokeTask(Delegate original, string method, object?[] arguments)
    {
        IEffectInterceptor? interceptor = Interceptor(method, arguments);
        return interceptor is null ? (Task)Call(original, arguments)! : interceptor.ExecuteAsync(Describe(method, arguments),
            _ => new ValueTask((Task)Call(original, arguments)!)).AsTask();
    }
}
