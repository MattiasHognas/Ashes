using Ashes.Fuzzing.Generation;

namespace Ashes.Fuzzing.Combinations;

/// <summary>
/// A worklist loop that takes the head call apart with a helper returning a tuple and pushes its
/// arguments back through an append, the way stage 1 walks a body's application chains. Both the
/// tuple and the appended list are owned call results kept in slots that shift into predecessor
/// slots at every back edge; the successor names the tuple's parts and hands the old tail only to
/// the append, so nothing of an earlier iteration's values is held without a reference of its own,
/// yet the reach test kept the predecessors and each back edge overwrote one unreleased.
/// </summary>
internal sealed class OwnedResultSuccessorPartsTemplate() : LeakCombinationTemplate(
    "leak.owned-result-successor-parts", "ownedResultSuccessorParts", GeneratedFeature.Match, GeneratedFeature.List)
{
    protected override string Declarations(LeakNames names)
    {
        string expr = names.Of("Expr");
        string leaf = names.Of("Leaf");
        string apply = names.Of("Apply");
        string spine = names.Of("spine");
        string join = names.Of("join");
        string walk = names.Of("walk");
        string second = names.Word();
        return $$"""
            type {{expr}} =
                | {{leaf}}(Str)
                | {{apply}}({{expr}}, {{expr}})

            let recursive {{spine}} (expression: {{expr}}) (arguments: List({{expr}})) =
                match expression with
                    | {{apply}}(function, argument) -> {{spine}}(function)(argument :: arguments)
                    | root -> (root, arguments)

            let recursive {{join}} (xs: List({{expr}})) (ys: List({{expr}})) =
                match xs with
                    | [] -> ys
                    | x :: rest -> x :: {{join}}(rest)(ys)

            let recursive {{walk}} (pending: List({{expr}})) (count: Int) =
                match pending with
                    | [] -> count
                    | ({{apply}}(_function, _argument) as call) :: rest ->
                        match {{spine}}(call)([]) with
                            | (root, arguments) -> {{walk}}(root :: {{join}}(arguments)(rest))(count + 1)
                    | _ :: rest -> {{walk}}(rest)(count + 1)

            let {{names.Entry}} (seed: Int) = {{walk}}([{{apply}}({{apply}}({{leaf}}({{names.Word()}} + Ashes.Text.fromInt(seed)), {{apply}}({{leaf}}({{second}}), {{leaf}}("a"))), {{apply}}({{leaf}}("h"), {{leaf}}("c")))])(0)
            """;
    }
}
