using EndoraReplay.Core.Abstractions;
using EndoraReplay.Core.Model;

namespace EndoraReplay.Core.Simulation;

/// <summary>Frames held in memory: preloaded stingers and tests.</summary>
public sealed class MemoryFrameSequence(int sourceId, IReadOnlyList<EncodedFrame> frames) : IFrameSequence
{
    public int SourceId { get; } = sourceId;
    public int Count => frames.Count;
    public long StartTicks => frames.Count > 0 ? frames[0].TimestampTicks : 0;
    public long EndTicks => frames.Count > 0 ? frames[^1].TimestampTicks : 0;
    public long TicksAt(int index) => frames[index].TimestampTicks;
    public EncodedFrame Read(int index) => (uint)index < (uint)frames.Count ? frames[index] : throw new FrameUnavailableException($"Frame {index} out of range.");
}
