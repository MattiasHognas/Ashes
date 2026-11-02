using Ashes.Fuzzing.Generation;

namespace Ashes.Fuzzing.Combinations;

/// <summary>
/// A passthrough function over a list of optional constructors, called with a fresh list (another
/// call's result) by a function that only matches or counts the result. The fresh argument routes
/// the call to the function's reuse specialization, whose compiled body hands its result over
/// reference-counted, and whose cons arm builds its cell over the matched parameter's own cell.
/// </summary>
internal sealed class ReuseSpecializedResultReadByScalarCallerTemplate() : LeakCombinationTemplate(
    "leak.reuse-specialized-result-read-by-scalar-caller", "reuseSpecializedResult", GeneratedFeature.Adt, GeneratedFeature.Match, GeneratedFeature.List)
{
    protected override string Declarations(LeakNames names)
    {
        string type = names.Of("ShapeType");
        string pass = names.Of("ShapePass");
        string grown = names.Of("ShapeGrown");
        string other = names.Of("ShapeOther");
        string merge = names.Of("merge");
        string shapeOf = names.Of("shapeOf");
        string observe = names.Of("observe");
        string initial = names.Of("initial");
        string countSome = names.Of("countSome");
        string head = names.Coin("merged-head") ? $"{merge}(current)({shapeOf}(argument))" : $"Some({shapeOf}(argument))";
        string body = names.Coin("nested-match")
            ? $$"""
                match arguments with
                        | argument :: restArguments ->
                            match shapes with
                                | current :: restShapes -> {{head}} :: {{observe}}(restArguments)(restShapes)
                                | [] -> shapes
                        | [] -> shapes
                """
            : $$"""
                match (arguments, shapes) with
                        | (argument :: restArguments, current :: restShapes) -> {{head}} :: {{observe}}(restArguments)(restShapes)
                        | _ -> shapes
                """;
        int width = names.Between(1, 4);
        string seedList = "[" + string.Join(", ", Enumerable.Range(0, width).Select(index => index == 0 ? "seed" : LeakNames.Text(names.Between(0, 5)))) + "]";
        string call = $"{observe}({seedList})({initial}({LeakNames.Text(width)}))";
        string run = names.Coin("matched-result")
            ? $$"""
                match {{call}} with
                        | [] -> 0
                        | _ :: _ -> 1
                """
            : $"{countSome}({call})";
        return $$"""
            type {{type}} =
                | {{pass}}
                | {{grown}}
                | {{other}}
                deriving {Eq}

            let {{merge}} (seen: Maybe({{type}})) (local: {{type}}) =
                match seen with
                    | None -> Some(local)
                    | Some(previous) ->
                        if previous == local
                        then Some(previous)
                        else Some({{other}})

            let {{shapeOf}} (argument: Int) =
                if argument > 1
                then {{grown}}
                else {{pass}}

            let recursive {{observe}} (arguments: List(Int)) (shapes: List(Maybe({{type}}))) =
                {{body}}

            let recursive {{initial}} (remaining: Int) =
                if remaining == 0
                then []
                else None :: {{initial}}(remaining - 1)

            let recursive {{countSome}} (shapes: List(Maybe({{type}}))) =
                match shapes with
                    | [] -> 0
                    | Some(_shape) :: rest -> 1 + {{countSome}}(rest)
                    | None :: rest -> {{countSome}}(rest)

            let {{names.Entry}} (seed: Int) =
                {{run}}
            """;
    }
}
