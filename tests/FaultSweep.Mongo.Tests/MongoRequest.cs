namespace FaultSweep.Mongo.Tests;

public sealed record MongoRequest(bool Correct, bool Async, string Operation = "update");
