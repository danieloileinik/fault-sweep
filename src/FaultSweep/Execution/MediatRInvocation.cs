using System.Linq.Expressions;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;

namespace FaultSweep.Execution;

internal static class MediatRInvocation<THandler, TMessage>
    where THandler : class
{
    public static Func<IServiceProvider, TMessage, CancellationToken, ValueTask>? Create()
    {
        Type? contract = typeof(THandler)
            .GetInterfaces()
            .Append(typeof(THandler))
            .FirstOrDefault(type =>
                type.IsGenericType
                && type.GetGenericTypeDefinition().FullName
                    is "MediatR.IRequestHandler`1"
                        or "MediatR.IRequestHandler`2"
                && type.Assembly.GetName().Name == "MediatR"
                && type.GenericTypeArguments[0].IsAssignableFrom(typeof(TMessage))
            );
        if (contract is null)
        {
            return null;
        }

        // Bind to the application's MediatR assembly so FaultSweep does not impose a package version.
        Type sender = contract.Assembly.GetType("MediatR.ISender", throwOnError: true)!;
        MethodInfo send =
            sender.GetMethod("Send", [typeof(object), typeof(CancellationToken)])
            ?? throw new NotSupportedException(
                "This MediatR version does not expose ISender.Send(object, CancellationToken)."
            );
        if (!typeof(Task).IsAssignableFrom(send.ReturnType))
        {
            throw new NotSupportedException("ISender.Send must return a Task.");
        }

        ParameterExpression services = Expression.Parameter(typeof(IServiceProvider), "services");
        ParameterExpression message = Expression.Parameter(typeof(TMessage), "message");
        ParameterExpression token = Expression.Parameter(typeof(CancellationToken), "token");
        MethodInfo resolve = typeof(ServiceProviderServiceExtensions).GetMethod(
            nameof(ServiceProviderServiceExtensions.GetRequiredService),
            [typeof(IServiceProvider), typeof(Type)]
        )!;
        Expression resolveSender = Expression.Convert(
            Expression.Call(resolve, services, Expression.Constant(sender)),
            sender
        );
        Expression call = Expression.Call(
            resolveSender,
            send,
            Expression.Convert(message, typeof(object)),
            token
        );
        return Expression
            .Lambda<Func<IServiceProvider, TMessage, CancellationToken, ValueTask>>(
                Expression.New(typeof(ValueTask).GetConstructor([typeof(Task)])!, call),
                services,
                message,
                token
            )
            .Compile();
    }
}
