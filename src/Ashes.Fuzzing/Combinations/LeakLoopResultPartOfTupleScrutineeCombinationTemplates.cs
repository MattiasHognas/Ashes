using Ashes.Fuzzing.Generation;

namespace Ashes.Fuzzing.Combinations;

/// <summary>
/// A loop walks a list of names and a list of types side by side by matching the tuple of both, and
/// returns <c>Some</c> of the type under the name it finds. The type's pattern owner is released at
/// its last use, the arena <c>Some</c> holds a duplicate of its own, and the return copies the result
/// anyway: the duplicate's owned slot is withdrawn there, and its leftover store kept the copy's
/// rule from withdrawing the duplicate itself, so one reference per hit was never released.
/// </summary>
internal sealed class LoopResultPartOfTupleScrutineeTemplate() : LeakCombinationTemplate(
    "leak.loop-result-part-of-tuple-scrutinee", "loopResultTuplePart", GeneratedFeature.RecursiveFunction, GeneratedFeature.TailCall, GeneratedFeature.List, GeneratedFeature.Adt, GeneratedFeature.Match)
{
    protected override string Declarations(LeakNames names)
    {
        string type = names.Of("LeakTy");
        string find = names.Of("findField");
        string weigh = names.Of("weighField");
        string all = names.Of("weighFields");
        string loop = names.Of("loop");
        string wanted = names.Coin("heap-element") ? "name" : "\"value\"";
        string named = names.Coin("wide-constructor") ? $"{type}Named(Int, Str)" : $"{type}Named(Str)";
        string namedValue = named.Contains("Int", StringComparison.Ordinal) ? $"{type}Named(n, name)" : $"{type}Named(name)";
        return $$"""
            type {{type}} =
                | {{type}}Int
                | {{type}}List({{type}})
                | {{named}}

            let recursive {{find}} (fieldName: Str) (fieldNames: List(Str)) (fieldTypes: List({{type}})) =
                match (fieldNames, fieldTypes) with
                    | (candidateName :: nameTail, fieldType :: typeTail) ->
                        if fieldName == candidateName
                        then Some(fieldType)
                        else {{find}}(fieldName)(nameTail)(typeTail)
                    | _ -> None

            let {{weigh}} (found: Maybe({{type}})) =
                match found with
                    | None -> 0
                    | Some({{type}}Int) -> 1
                    | Some({{type}}List(_)) -> 2
                    | Some(_) -> 3

            let {{all}} (n: Int) (name: Str) =
                {{weigh}}({{find}}({{wanted}})(["value", name, "owner"])([{{type}}Int, {{type}}List({{type}}Int), {{namedValue}}]))

            let recursive {{loop}} (n: Int) (sum: Int) =
                if n == 0
                then sum
                else {{loop}}(n - 1)(sum + {{all}}(n)("f" + Ashes.Text.fromInt(n)))

            let {{names.Entry}} (seed: Int) = {{loop}}({{names.Between(3, 9)}})(0) + seed
            """;
    }
}
