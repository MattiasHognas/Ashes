namespace Ashes.Semantics;

/// <summary>
/// Byte-level layout of one compiler-managed heap value family. Payload words are always addressed
/// relative to the value pointer; a future RC header can therefore move the payload by changing one
/// descriptor instead of rewriting lowering and backend offset arithmetic.
/// </summary>
internal sealed class HeapLayoutDescriptor
{
    public HeapLayoutDescriptor(int? tagOffsetBytes, int payloadOffsetBytes, int? fixedPayloadWordCount = null)
    {
        ValidateAlignedOffset(tagOffsetBytes, nameof(tagOffsetBytes));
        ValidateAlignedOffset(payloadOffsetBytes, nameof(payloadOffsetBytes));
        if (fixedPayloadWordCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(fixedPayloadWordCount));
        }

        TagOffsetBytes = tagOffsetBytes;
        PayloadOffsetBytes = payloadOffsetBytes;
        FixedPayloadWordCount = fixedPayloadWordCount;
    }

    public int? TagOffsetBytes { get; }
    public int PayloadOffsetBytes { get; }
    public int? FixedPayloadWordCount { get; }

    public int FixedAllocationSizeBytes => FixedPayloadWordCount is int payloadWords
        ? AllocationSizeBytes(payloadWords)
        : throw new InvalidOperationException("This heap layout has a variable payload size.");

    public int PayloadWordOffsetBytes(int payloadWordIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(payloadWordIndex);
        return checked(PayloadOffsetBytes + (payloadWordIndex * HeapLayouts.WordSizeBytes));
    }

    public int AllocationSizeBytes(int payloadWordCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(payloadWordCount);
        return PayloadWordOffsetBytes(payloadWordCount);
    }

    private static void ValidateAlignedOffset(int? offsetBytes, string parameterName)
    {
        if (offsetBytes is int offset && (offset < 0 || offset % HeapLayouts.WordSizeBytes != 0))
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
    }
}

/// <summary>
/// Metadata stored immediately before a runtime-managed value. The public value pointer addresses
/// the legacy payload layout, so arena and RC values use identical tag and field offsets.
/// </summary>
internal sealed class HeapHeaderLayoutDescriptor
{
    public int ReferenceCountOffsetBytes => 0;
    public int AllocationSizeOffsetBytes => HeapLayouts.WordSizeBytes;
    public int SizeBytes => 2 * HeapLayouts.WordSizeBytes;

    public int TotalAllocationSizeBytes(int valueSizeBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(valueSizeBytes);
        return checked(SizeBytes + valueSizeBytes);
    }
}

/// <summary>Current ordinary heap layouts shared by lowering and native backends.</summary>
internal static class HeapLayouts
{
    public const int WordSizeBytes = 8;
    public const int ListHeadIndex = 0;
    public const int ListTailIndex = 1;

    public static HeapHeaderLayoutDescriptor RcHeader { get; } = new();

    // Value pointer layout: [tag, field0, field1, ...]. Runtime-managed values additionally have
    // RcHeader immediately before this pointer; arena-managed values do not.
    public static HeapLayoutDescriptor Adt { get; } = new(tagOffsetBytes: 0, payloadOffsetBytes: WordSizeBytes);

    // Legacy list: nil = 0; cons = [head, tail]. Ticket 5 will prepend the RC header here.
    public static HeapLayoutDescriptor List { get; } = new(
        tagOffsetBytes: null,
        payloadOffsetBytes: 0,
        fixedPayloadWordCount: 2);
}
