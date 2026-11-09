using Ashes.Fuzzing.Generation;

namespace Ashes.Fuzzing.Combinations;

/// <summary>
/// A loop agrees on one label across a list of sites: an absent agreement becomes `Some(label)` in the
/// arm that matched it absent, a present one is handed on. The agreement stays an arena loop parameter
/// (a linear-reuse accumulator), so the `Some` cell built from a label read out of a site's definition
/// keeps a reference nothing releases unless the loop's exit releases the cell's own fields
/// (`GRC_NO_EXITPARAMOWNFIELDS`).
/// </summary>
internal sealed class ExitParameterOwnFieldsTemplate() : LeakCombinationTemplate(
    "leak.exit-parameter-own-fields", "exitParameterOwnFields", GeneratedFeature.RecursiveFunction, GeneratedFeature.TailCall, GeneratedFeature.List, GeneratedFeature.Adt)
{
    protected override string Declarations(LeakNames names)
    {
        string definition = names.Of("Definition");
        string site = names.Of("Site");
        string resolution = names.Of("Resolution");
        string lookup = names.Of("lookup");
        string resolveOne = names.Of("resolveOne");
        string resolveGroup = names.Of("resolveGroup");
        string probe = names.Of("probe");
        string loop = names.Of("loop");
        return $$"""
            type {{definition}} =
                | {{definition}}Closure(Int, Str)
                | {{definition}}Other(Int)
                | {{definition}}Nested({{definition}})

            type {{site}} =
                | creator: List((Int, {{definition}}))
                | sourceTemp: Int

            type {{resolution}} =
                | {{resolution}}Known(Str)
                | {{resolution}}Pending
                | {{resolution}}Unknown

            let recursive {{lookup}} key entries =
                match entries with
                    | [] -> None
                    | (k, v) :: tail ->
                        if k == key
                        then Some(v)
                        else {{lookup}}(key)(tail)

            let {{resolveOne}} (definitions: List((Int, {{definition}}))) (temp: Int) =
                match {{lookup}}(temp)(definitions) with
                    | Some({{definition}}Closure(_, label)) -> {{resolution}}Known(label)
                    | Some({{definition}}Other(_)) -> {{resolution}}Pending
                    | Some({{definition}}Nested(_)) -> {{resolution}}Pending
                    | None -> {{resolution}}Unknown

            let recursive {{resolveGroup}} (sites: List({{site}})) (agreed: Maybe(Str)) (seen: List(Str)) (pending: Bool) =
                match sites with
                    | [] ->
                        if pending
                        then {{resolution}}Pending
                        else
                            match agreed with
                                | Some(label) ->
                                    match seen with
                                        | first :: _ -> {{resolution}}Known(first + label)
                                        | [] -> {{resolution}}Known(label)
                                | None -> {{resolution}}Pending
                    | s :: tail ->
                        match {{resolveOne}}(s.creator)(s.sourceTemp) with
                            | {{resolution}}Unknown -> {{resolution}}Unknown
                            | {{resolution}}Pending -> {{resolveGroup}}(tail)(agreed)(seen)(true)
                            | {{resolution}}Known(label) ->
                                match agreed with
                                    | None -> {{resolveGroup}}(tail)(Some(label))(label :: seen)(pending)
                                    | Some(previous) ->
                                        if previous == label
                                        then {{resolveGroup}}(tail)(agreed)(label :: seen)(pending)
                                        else {{resolution}}Unknown

            let {{probe}} (n: Int) =
                (let definitions = [(1, {{definition}}Closure(1, "lambda_" + Ashes.Text.fromInt(n % 10))), (2, {{definition}}Other(2))]
                in
                    match {{resolveGroup}}([{{site}}(creator = definitions, sourceTemp = 1)])(None)([])(false) with
                        | {{resolution}}Known(l) -> Ashes.Text.length(l)
                        | {{resolution}}Pending -> 7
                        | {{resolution}}Unknown -> 9)

            let recursive {{loop}} (n: Int) (acc: Int) =
                if n == 0
                then acc
                else {{loop}}(n - 1)(acc + {{probe}}(n))

            let {{names.Entry}} (seed: Int) = {{loop}}({{names.Between(3, 9)}})(seed)
            """;
    }
}
