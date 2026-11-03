namespace FaultSweep.Effects;

public sealed record EffectDescriptor
{
    public EffectDescriptor(string source, string operation, string? target = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        Source = source;
        Operation = operation;
        Target = target;
    }

    // csharpier-ignore-start -- Keep consecutive properties together.
    public string Source { get; }
    public string Operation { get; }
    /// <summary>A stable label, without volatile values such as timestamps or generated IDs.</summary>
    public string? Target { get; }
    // csharpier-ignore-end

    public override string ToString() =>
        Target is null ? $"{Source}:{Operation}" : $"{Source}:{Operation} ({Target})";
}
