using System.Linq.Expressions;
using System.Reflection;

namespace FaultSweep.Execution;

internal static class HandlerInvocation<THandler, TMessage>
    where THandler : class
{
    public static Func<THandler, TMessage, CancellationToken, ValueTask> Create()
    {
        if (typeof(IMessageHandler<TMessage>).IsAssignableFrom(typeof(THandler)))
        {
            return (handler, message, token) =>
                ((IMessageHandler<TMessage>)handler).HandleAsync(message, token);
        }

        MethodInfo[] methods = typeof(THandler)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Where(method =>
                method is { Name: "HandleAsync", ContainsGenericParameters: false }
                && (
                    method.ReturnType == typeof(ValueTask)
                    || typeof(Task).IsAssignableFrom(method.ReturnType)
                )
                && method.GetParameters().Select(parameter => parameter.ParameterType).ToArray()
                    is var parameters
                && (
                    parameters.SequenceEqual([typeof(TMessage)])
                    || parameters.SequenceEqual([typeof(TMessage), typeof(CancellationToken)])
                )
            )
            .ToArray();
        if (methods.Length != 1)
        {
            throw new InvalidOperationException(
                $"{typeof(THandler).Name} must expose one HandleAsync({typeof(TMessage).Name}[, CancellationToken]) "
                    + "returning Task or ValueTask, or implement IMessageHandler<TMessage>."
            );
        }

        ParameterExpression handlerParameter = Expression.Parameter(typeof(THandler), "handler");
        ParameterExpression messageParameter = Expression.Parameter(typeof(TMessage), "message");
        ParameterExpression tokenParameter = Expression.Parameter(
            typeof(CancellationToken),
            "token"
        );
        MethodInfo methodInfo = methods[0];
        Expression call = Expression.Call(
            handlerParameter,
            methodInfo,
            methodInfo.GetParameters().Length == 1
                ? [messageParameter]
                : [messageParameter, tokenParameter]
        );
        if (methodInfo.ReturnType != typeof(ValueTask))
        {
            call = Expression.New(typeof(ValueTask).GetConstructor([typeof(Task)])!, call);
        }

        return Expression
            .Lambda<Func<THandler, TMessage, CancellationToken, ValueTask>>(
                call,
                handlerParameter,
                messageParameter,
                tokenParameter
            )
            .Compile();
    }
}
