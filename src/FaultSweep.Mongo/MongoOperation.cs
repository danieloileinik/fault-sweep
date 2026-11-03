using FaultSweep.Effects;

namespace FaultSweep.Mongo;

internal sealed record MongoOperation(
    EffectDescriptor? Effect = null,
    bool WrapResult = false,
    string? Rejection = null
);
