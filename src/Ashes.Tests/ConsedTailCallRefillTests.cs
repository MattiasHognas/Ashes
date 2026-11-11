using Ashes.Frontend;
using Ashes.Semantics;
using Shouldly;

namespace Ashes.Tests;

public sealed class ConsedTailCallRefillTests
{
    [Test]
    public void MixedPopPushAndHelperRefillOwnsItsWorklist()
    {
        const string source = """
            type Tree =
                | Leaf(Str)
                | Nested(Tree)
                | Many(List(Tree))
            
            type Job =
                | value: Tree
                | path: List(Tree)
            
            type WalkResult =
                | ok: Bool
                | known: List(Tree)
            
            let resolve value environment =
                if environment == 0
                then value
                else Nested(value)
            
            let recursive prepend values path pending =
                match values with
                    | [] -> pending
                    | value :: rest -> Job(value = value, path = path) :: prepend(rest)(path)(pending)
            
            let recursive walk (pending: List(Job)) (visited: List(Tree)) (known: List(Tree)) (environment: Int) (flag: Bool) =
                match pending with
                    | [] -> WalkResult(ok = true, known = known)
                    | Job { value = value, path = path } :: rest ->
                        let resolved = resolve(value)(environment)
                        in
                            match resolved with
                                | Leaf(_text) -> walk(rest)(visited)(known)(environment)(flag)
                                | Nested(child) -> walk(Job(value = child, path = path) :: rest)(visited)(known)(environment)(flag)
                                | Many(children) ->
                                    walk(prepend(children)(path)(rest))(visited)(known)(environment)(flag)
            
            walk([Job(value = Many([Nested(Leaf("x"))]), path = [])])([])([])(0)(false)
            """;
        Diagnostics diagnostics = new();
        Ashes.Frontend.Program program = new Parser(source, diagnostics).ParseProgram();
        Lowering lowering = new(diagnostics);
        _ = lowering.Lower(program);
        diagnostics.Errors.ShouldBeEmpty();
        TcoParamPlacementTrace placement = lowering.GetTcoPlacementDecisions("walk")!
            .Single(trace => string.Equals(trace.Current.ParameterName, "pending", StringComparison.Ordinal));
        placement.Current.Representation.ShouldBe(TcoPlacementRepresentation.RuntimeRc);
        lowering.GetOwnershipSummary("walk")!.TcoParamFacts[0].ChoiceAccumulatorRebuild.ShouldBeTrue();
    }
}
