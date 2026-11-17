using Ashes.Fuzzing.Generation;

namespace Ashes.Fuzzing.Combinations;

/// <summary>
/// A generic tuple updater hands two references to the same borrowed bytes to a caller that
/// normalizes its result. Each retained alias is settled separately, while the source remains usable.
/// </summary>
internal sealed class GenericTupleBorrowedBytesTemplate() : LeakCombinationTemplate(
    "leak.generic-tuple-borrowed-bytes", "genericTupleBorrowedBytes", GeneratedFeature.Record, GeneratedFeature.Tuple, GeneratedFeature.Match)
{
    protected override string Declarations(LeakNames names)
    {
        string item = names.Of("ByteItem");
        string state = names.Of("ByteState");
        string update = names.Of("updateBytes");
        string replacement = names.Coin("text-replacement") ? "text" : "seed";
        return $$"""
            type {{item}} =
                | text: Str
                | number: Int

            type alias {{state}} = (List({{item}}), Bytes, Bytes, Bool)

            let {{update}} (state: {{state}}) replacement =
                match state with
                    | (items, bytes, alias, _flag) -> (items, bytes, alias, replacement)

            let {{names.Entry}} (seed: Int) =
                let text = {{names.Word()}} + Ashes.Text.fromInt(seed)
                in
                    let bytes = Ashes.Byte.fromText(text)
                    in
                        let original = ([{{item}}(text = text, number = seed)], bytes, bytes, false)
                        in
                            match {{update}}(original)({{replacement}}) with
                                | (items, returned, alias, _replacement) ->
                                    Ashes.Byte.length(returned) + Ashes.Byte.length(alias)
                                        + Ashes.Byte.length(bytes) + Ashes.Collection.List.length(items)
            """;
    }
}
