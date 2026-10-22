using Ashes.Fuzzing.Generation;

namespace Ashes.Fuzzing.Combinations;

/// <summary>
/// Byte buffers built and updated once per call: <c>Byte.fromList</c>, <c>Byte.allocate</c> followed
/// by <c>set</c>, <c>copyRange</c> (into a fresh buffer or within one buffer), the little-endian
/// setters. Each result is either bound by a <c>let</c> or handed straight to the next update,
/// which may write into it in place.
/// </summary>
internal sealed class BytesBufferUpdatesTemplate() : LeakCombinationTemplate(
    "leak.bytes-buffer-updates", "bytesBufferUpdates", GeneratedFeature.Call)
{
    protected override string Declarations(LeakNames names)
    {
        string stamp = names.Of("stampBuffer");
        string size = LeakNames.Text(names.Between(4, 9));
        string first = LeakNames.Text(names.Between(1, 9));
        string body = names.Variant("update", 6) switch
        {
            0 => $$"""
                let listed = Ashes.Byte.fromList([{{first}}u8, 2u8, 3u8])
                    in
                        let updated = Ashes.Byte.set(listed)(0)(7u8)
                        in Ashes.Byte.length(updated) + Ashes.Byte.length(listed) + seed
                """,
            1 => $$"""
                let bytes = Ashes.Byte.allocate({{size}})
                    in
                        let updated = {{stamp}}(bytes)(1)
                        in Ashes.Byte.length(updated) + seed
                """,
            2 => $$"""
                let destination = Ashes.Byte.allocate({{size}})
                    in
                        let source = Ashes.Byte.fromList([{{first}}u8, 5u8, 6u8])
                        in
                            let copied = Ashes.Byte.copyRange(destination)(1)(source)(0)(3)
                            in Ashes.Number.UInt.toInt(Ashes.Byte.get(copied)(1)) + Ashes.Byte.length(copied) + seed
                """,
            3 => $$"""
                let wide = Ashes.Byte.setU32Le(Ashes.Byte.setU16Le(Ashes.Byte.allocate({{size}} + 2))(0)({{first}}u16))(2)(9u32)
                    in Ashes.Number.UInt.toInt(Ashes.Byte.getU16Le(wide)(0)) + Ashes.Byte.length(wide) + seed
                """,
            4 => $$"""
                let wide = Ashes.Byte.setU64Le(Ashes.Byte.allocate({{size}} + 8))(0)({{first}}u64)
                    in Ashes.Number.UInt.toInt(Ashes.Byte.getU32Le(wide)(0)) + Ashes.Byte.length(wide) + seed
                """,
            _ => $$"""
                let base = Ashes.Byte.fromList([{{first}}u8, 2u8, 3u8, 4u8])
                    in
                        let shifted = Ashes.Byte.copyRange(base)(0)(base)(1)(3)
                        in Ashes.Number.UInt.toInt(Ashes.Byte.get(shifted)(0)) + Ashes.Byte.length(shifted) + seed
                """,
        };
        return $$"""
            let {{stamp}} (buffer: Bytes) (i: Int) = Ashes.Byte.set(buffer)(i)(9u8)

            let {{names.Entry}} (seed: Int) =
                {{body}}
            """;
    }
}

/// <summary>
/// A fresh buffer handed straight to a builtin that only reads it: a length, an element or
/// integer read, a search, a hash, a copied-out text, a comparison, or a copyRange source. The
/// builtin keeps nothing of the buffer, so the read is where it is released.
/// </summary>
internal sealed class BytesFreshReadOperandTemplate() : LeakCombinationTemplate(
    "leak.bytes-fresh-read-operand", "bytesFreshReadOperand", GeneratedFeature.Call)
{
    protected override string Declarations(LeakNames names)
    {
        string size = LeakNames.Text(names.Between(4, 9));
        string first = LeakNames.Text(names.Between(1, 9));
        string body = names.Variant("read", 7) switch
        {
            0 => $$"""Ashes.Byte.length(Ashes.Byte.allocate({{size}})) + seed""",
            1 => $$"""Ashes.Number.UInt.toInt(Ashes.Byte.get(Ashes.Byte.set(Ashes.Byte.allocate({{size}}))(0)({{first}}u8))(0)) + seed""",
            2 => $$"""Ashes.Number.UInt.toInt(Ashes.Byte.getU16Le(Ashes.Byte.allocate({{size}}))(0)) + seed""",
            3 => $$"""Ashes.Byte.indexOf(Ashes.Byte.allocate({{size}}))(0)(0) + Ashes.Byte.hash(Ashes.Byte.allocate({{size}})) % 7 + seed""",
            4 => $$"""Ashes.Text.byteLength(Ashes.Byte.subText(Ashes.Byte.allocate({{size}}))(0)(2)) + seed""",
            5 => $$"""Ashes.Byte.compare(Ashes.Byte.allocate({{size}}))(Ashes.Byte.allocate(3)) + seed""",
            _ => $$"""
                let wide = Ashes.Byte.copyRange(Ashes.Byte.allocate({{size}}))(0)(Ashes.Byte.allocate(2))(0)(2)
                    in Ashes.Byte.length(wide) + seed
                """,
        };
        return $$"""
            let {{names.Entry}} (seed: Int) =
                {{body}}
            """;
    }
}

/// <summary>
/// A loop that hands a buffer to itself through an update (<c>set</c>, <c>setU16Le</c>, or a
/// <c>copyRange</c> within the buffer), started from a fresh buffer. The buffer parameter is
/// reference-counted, and each back edge releases the buffer its successor replaces.
/// </summary>
internal sealed class BytesLoopBufferTemplate() : LeakCombinationTemplate(
    "leak.bytes-loop-buffer", "bytesLoopBuffer", GeneratedFeature.Call)
{
    protected override string Declarations(LeakNames names)
    {
        string fill = names.Of("fillBuffer");
        string size = LeakNames.Text(names.Between(4, 9));
        string update = names.Variant("update", 3) switch
        {
            0 => "Ashes.Byte.set(buffer)(i)(Ashes.Number.UInt.fromInt(i))",
            1 => "Ashes.Byte.setU16Le(buffer)(0)(7u16)",
            _ => "Ashes.Byte.copyRange(buffer)(i)(buffer)(0)(1)",
        };
        return $$"""
            let recursive {{fill}} (buffer: Bytes) (i: Int) =
                if i >= Ashes.Byte.length(buffer)
                then buffer
                else {{fill}}({{update}})(i + 1)

            let {{names.Entry}} (seed: Int) =
                let filled = {{fill}}(Ashes.Byte.allocate({{size}}))(1)
                in Ashes.Byte.length(filled) + seed
            """;
    }
}
