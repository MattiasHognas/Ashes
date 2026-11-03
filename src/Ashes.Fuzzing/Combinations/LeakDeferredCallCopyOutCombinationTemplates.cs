using Ashes.Fuzzing.Generation;

namespace Ashes.Fuzzing.Combinations;

/// <summary>
/// A record literal takes a fresh list of empty lists built by a generic recursive function, whose
/// element type is known only from the record's field. A compiler that learns the call's type after
/// the call copies the result out at the end of the body, and the field still has to go to an
/// owned slot that releases it, as it does when the type is known at the call.
/// </summary>
internal sealed class DeferredCallCopyOutRecordFieldTemplate() : LeakCombinationTemplate(
    "leak.deferred-call-copy-out-record-field", "deferredCopyOutField", GeneratedFeature.Record, GeneratedFeature.List, GeneratedFeature.Match, GeneratedFeature.RecursiveFunction)
{
    protected override string Declarations(LeakNames names)
    {
        string state = names.Of("DCount");
        string empty = names.Of("emptyLists");
        string size = names.Of("size");
        string probe = names.Of("probe");
        string loop = names.Of("loop");
        string elementType = names.Coin("str-elements") ? "Str" : "Int";
        string literal = names.Coin("count-first")
            ? $"{state}(count = n, flow = {empty}(width))"
            : $"{state}(flow = {empty}(width), count = n)";
        bool readFlow = names.Coin("read-flow");
        string flowBinder = readFlow ? "flow" : "_flow";
        string result = readFlow ? $"{size}(flow)(references)" : "references";
        return $$"""
            type {{state}} =
                | count: Int
                | flow: List(List({{elementType}}))

            let recursive {{empty}} (count: Int) =
                if count == 0
                then []
                else [] :: {{empty}}(count - 1)

            let recursive {{size}} (lists: List(List({{elementType}}))) (acc: Int) =
                match lists with
                    | [] -> acc
                    | _first :: rest -> {{size}}(rest)(acc + 1)

            let {{probe}} (n: Int) (width: Int) =
                match {{literal}} with
                    | {{state}} { count = references, flow = {{flowBinder}} } -> {{result}}

            let recursive {{loop}} (n: Int) (acc: Int) =
                if n == 0
                then acc
                else {{loop}}(n - 1)(acc + {{probe}}(n)({{names.Between(1, 5)}}))

            let {{names.Entry}} (seed: Int) = {{loop}}({{names.Between(3, 9)}})(seed)
            """;
    }
}
