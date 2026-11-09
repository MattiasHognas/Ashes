using Ashes.Fuzzing.Generation;

namespace Ashes.Fuzzing.Combinations;

/// <summary>
/// A loop over a list of records returns <c>Some</c> of a string field of the record it finds, and its
/// caller stores the found string into its own accumulator. The loop owns nothing else, so its return
/// copied nothing and the duplicate the field's pattern owner took for the arena <c>Some</c> was never
/// released; it is kept in an owned slot, released behind the return's copy.
/// </summary>
internal sealed class LoopResultPartDuplicateTemplate() : LeakCombinationTemplate(
    "leak.loop-result-part-duplicate", "loopResultPart", GeneratedFeature.RecursiveFunction, GeneratedFeature.TailCall, GeneratedFeature.List, GeneratedFeature.Adt)
{
    protected override string Declarations(LeakNames names)
    {
        string kind = names.Of("LeakKind");
        string item = names.Of("LeakItem");
        string find = names.Of("findLabel");
        string record = names.Of("recordLabel");
        string loop = names.Of("loop");
        bool wide = names.Coin("wide-constructor");
        string extraField = wide ? ", Bool, Int" : "";
        string extraValue = wide ? ", true, 0" : "";
        string extraPattern = wide ? ", _flag, _size" : "";
        return $$"""
            type {{kind}} =
                | {{kind}}Closure(Int, Str{{extraField}})
                | {{kind}}Other(Int)

            type {{item}} =
                | kind: {{kind}}
                | location: Maybe(Int)

            let recursive {{find}} (target: Int) (items: List({{item}})) =
                match items with
                    | [] -> None
                    | {{item}} { kind = {{kind}}Closure(temp, label{{extraPattern}}) } :: rest ->
                        if temp == target
                        then Some(label)
                        else {{find}}(target)(rest)
                    | _ :: rest -> {{find}}(target)(rest)

            let {{record}} (target: Int) (items: List({{item}})) (labels: List(Str)) =
                match {{find}}(target)(items) with
                    | Some(found) -> found :: labels
                    | None -> labels

            let recursive {{loop}} (n: Int) (labels: List(Str)) =
                if n == 0
                then Ashes.Collection.List.length(labels)
                else {{loop}}(n - 1)({{record}}(7)([{{item}}(kind = {{kind}}Other(n), location = None), {{item}}(kind = {{kind}}Closure(7, "lambda_" + Ashes.Text.fromInt(n){{extraValue}}), location = Some(n))])(labels))

            let {{names.Entry}} (seed: Int) = {{loop}}({{names.Between(3, 9)}})([]) + seed
            """;
    }
}
