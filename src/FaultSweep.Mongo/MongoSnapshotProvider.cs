using System.Text.Json;
using FaultSweep.Snapshots;
using MongoDB.Bson;
using MongoDB.Bson.IO;
using MongoDB.Driver;

namespace FaultSweep.Mongo;

public sealed class MongoSnapshotProvider : ISnapshotProvider
{
    private readonly IMongoDatabase database;
    private readonly string[] collections;
    private readonly string[] ignoredFields;

    public MongoSnapshotProvider(
        IMongoDatabase database,
        IEnumerable<string> collections,
        IEnumerable<string>? ignore = null
    )
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(collections);
        this.database = database;
        this.collections = collections
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (this.collections.Length == 0 || this.collections.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException(
                "Select at least one named MongoDB collection.",
                nameof(collections)
            );
        }

        ignoredFields = (ignore ?? []).Distinct(StringComparer.Ordinal).ToArray();
    }

    public async ValueTask<StateSnapshot> CaptureAsync(CancellationToken cancellationToken)
    {
        var state = new SortedDictionary<string, List<JsonElement>>(StringComparer.Ordinal);
        foreach (string name in collections)
        {
            List<BsonDocument> documents = await database
                .GetCollection<BsonDocument>(name)
                .Find(FilterDefinition<BsonDocument>.Empty)
                .Sort(Builders<BsonDocument>.Sort.Ascending("_id"))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            List<JsonElement> values = [];
            foreach (BsonDocument document in documents)
            {
                foreach (string field in ignoredFields)
                {
                    document.Remove(field);
                }

                using JsonDocument json = JsonDocument.Parse(
                    document.ToJson(
                        new JsonWriterSettings { OutputMode = JsonOutputMode.CanonicalExtendedJson }
                    )
                );
                values.Add(json.RootElement.Clone());
            }

            state.Add(name, values);
        }

        return StateSnapshot.From(state);
    }
}
