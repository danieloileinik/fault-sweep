using System.Reflection;
using FaultSweep.Effects;
using MongoDB.Driver;

namespace FaultSweep.Mongo;

internal static class MongoOperationClassifier
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

    public static MongoOperation Classify(MethodInfo method, object target, object?[] arguments)
    {
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
        bool wrap = returnsCollection || method.ReturnType == typeof(IMongoDatabase);
        if (arguments.OfType<IClientSessionHandle>().Any(session => session.IsInTransaction))
        {
            return new MongoOperation(
                WrapResult: wrap,
                Rejection: "MongoDB transactions require commit instrumentation and are not supported."
            );
        }

        string name = method.Name.EndsWith("Async", StringComparison.Ordinal)
            ? method.Name[..^5]
            : method.Name;
        if (Writes.Contains(name))
        {
            var effect = new EffectDescriptor(
                "mongo",
                name,
                target.GetType().GetProperty("CollectionNamespace")?.GetValue(target)?.ToString()
            );
            bool unacknowledged =
                target.GetType().GetProperty("Settings")?.GetValue(target)
                is MongoCollectionSettings { WriteConcern.IsAcknowledged: false };
            return new MongoOperation(
                effect,
                Rejection: unacknowledged
                    ? "MongoDB unacknowledged writes cannot be verified."
                    : null
            );
        }

        if (method.IsSpecialName && method.Name is "get_Indexes" or "get_SearchIndexes")
        {
            return new MongoOperation(
                Rejection: "MongoDB index management must run during reset or seed, outside the handler."
            );
        }

        return !method.IsSpecialName && !Reads.Contains(method.Name) && !wrap
            ? new MongoOperation(
                Rejection: $"MongoDB operation {method.Name} is not supported. Use acknowledged single-document writes and tracked collections."
            )
            : new MongoOperation(WrapResult: wrap);
    }
}
