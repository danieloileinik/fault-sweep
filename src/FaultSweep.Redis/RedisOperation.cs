using FaultSweep.Effects;

namespace FaultSweep.Redis;

internal sealed record RedisOperation(EffectDescriptor? Effect = null, string? Rejection = null);
