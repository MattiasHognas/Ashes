using Ashes.Fuzzing.Generation;

namespace Ashes.Fuzzing.Combinations;

/// <summary>
/// A finder with untyped parameters compares names with <c>==</c>, so it is constrained by
/// <c>Eq</c> and a call at concrete types goes to a copy lowered with the comparison on strings.
/// The copy returns its <c>Some</c> owned, as a typed finder does, but the call to the copy left the
/// result unowned: the caller matched it and released neither the <c>Some</c> nor the type it holds.
/// </summary>
internal sealed class UntypedFinderResultTemplate() : LeakCombinationTemplate(
    "leak.untyped-finder-result", "untypedFinderResult", GeneratedFeature.RecursiveFunction, GeneratedFeature.TailCall, GeneratedFeature.List, GeneratedFeature.Adt, GeneratedFeature.Match)
{
    protected override string Declarations(LeakNames names)
    {
        string type = names.Of("LeakTy");
        string find = names.Of("findField");
        string weigh = names.Of("weighField");
        string loop = names.Of("loop");
        string wanted = names.Coin("heap-element") ? "name" : "\"value\"";
        string scrutinee = names.Coin("tuple-scrutinee") ? "(fieldNames, fieldTypes)" : "(fieldTypes, fieldNames)";
        string pattern = scrutinee.StartsWith("(fieldNames", StringComparison.Ordinal)
            ? "(candidateName :: nameTail, fieldType :: typeTail)"
            : "(fieldType :: typeTail, candidateName :: nameTail)";
        return $$"""
            type {{type}} =
                | {{type}}Int
                | {{type}}List({{type}})
                | {{type}}Named(Int, Str, List({{type}}))

            let {{find}} fieldName fieldNames fieldTypes =
                match {{scrutinee}} with
                    | {{pattern}} ->
                        if fieldName == candidateName
                        then Some(fieldType)
                        else None
                    | _ -> None

            let {{weigh}} (n: Int) (name: Str) =
                match {{find}}({{wanted}})([name, "value"])([{{type}}Named(n, name, [{{type}}Int]), {{type}}List({{type}}Int)]) with
                    | None -> 0
                    | Some({{type}}Named(_, _, _)) -> 3
                    | Some(_fieldType) -> 1

            let recursive {{loop}} (n: Int) (sum: Int) =
                if n == 0
                then sum
                else {{loop}}(n - 1)(sum + {{weigh}}(n)("f" + Ashes.Text.fromInt(n)))

            let {{names.Entry}} (seed: Int) = {{loop}}({{names.Between(3, 9)}})(0) + seed
            """;
    }
}
