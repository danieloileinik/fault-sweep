using MongoDB.Bson;
using MongoDB.Driver;

namespace FaultSweep.Mongo.Tests;

public sealed class MongoHandler(IMongoDatabase database) : IMessageHandler<MongoRequest>
{
    public async ValueTask HandleAsync(MongoRequest message, CancellationToken token)
    {
        IMongoCollection<BsonDocument> collection = database
            .GetCollection<BsonDocument>("payments")
            .WithReadPreference(ReadPreference.Primary);
        var filter = Builders<BsonDocument>.Filter.Eq("_id", 1);
        if (message.Operation == "insert")
        {
            if (await collection.CountDocumentsAsync(filter, cancellationToken: token) == 0)
            {
                var document = new BsonDocument { { "_id", 1 }, { "amount", 10 } };
                if (message.Async)
                    await collection.InsertOneAsync(document, cancellationToken: token);
                else
                    collection.InsertOne(document, cancellationToken: token);
            }
            return;
        }

        if (message.Operation == "bulk")
        {
            try
            {
                await collection.InsertManyAsync(
                    [new BsonDocument("amount", 10)],
                    cancellationToken: token
                );
            }
            catch { }
            return;
        }

        UpdateDefinition<BsonDocument> update = message.Correct
            ? Builders<BsonDocument>.Update.Set("amount", 10)
            : Builders<BsonDocument>.Update.Inc("amount", 10);
        var replacement = new BsonDocument { { "_id", 1 }, { "amount", 10 } };
        switch (message.Operation, message.Async)
        {
            case ("replace", true):
                await collection.ReplaceOneAsync(
                    filter,
                    replacement,
                    new ReplaceOptions { IsUpsert = true },
                    token
                );
                return;
            case ("replace", false):
                collection.ReplaceOne(
                    filter,
                    replacement,
                    new ReplaceOptions { IsUpsert = true },
                    token
                );
                return;
            case ("delete", true):
                await collection.DeleteOneAsync(filter, token);
                return;
            case ("delete", false):
                collection.DeleteOne(filter, token);
                return;
            case ("find-update", true):
                await collection.FindOneAndUpdateAsync(
                    filter,
                    update,
                    new FindOneAndUpdateOptions<BsonDocument> { IsUpsert = true },
                    token
                );
                return;
            case ("find-update", false):
                collection.FindOneAndUpdate(
                    filter,
                    update,
                    new FindOneAndUpdateOptions<BsonDocument> { IsUpsert = true },
                    token
                );
                return;
            case ("find-replace", true):
                await collection.FindOneAndReplaceAsync(
                    filter,
                    replacement,
                    new FindOneAndReplaceOptions<BsonDocument> { IsUpsert = true },
                    token
                );
                return;
            case ("find-replace", false):
                collection.FindOneAndReplace(
                    filter,
                    replacement,
                    new FindOneAndReplaceOptions<BsonDocument> { IsUpsert = true },
                    token
                );
                return;
            case ("find-delete", true):
                await collection.FindOneAndDeleteAsync(filter, cancellationToken: token);
                return;
            case ("find-delete", false):
                collection.FindOneAndDelete(filter, cancellationToken: token);
                return;
        }

        if (message.Async)
            await collection.UpdateOneAsync(
                filter,
                update,
                new UpdateOptions { IsUpsert = true },
                token
            );
        else
            collection.UpdateOne(filter, update, new UpdateOptions { IsUpsert = true }, token);
    }
}
