using Ashes.Frontend;
using Ashes.Semantics;
using Shouldly;

namespace Ashes.Tests;

public sealed class TraitInferenceTests
{
    [Test]
    public void DirectMethodUseInfersAndGeneralizesAConstraint()
    {
        const string source = """
            trait Eq(a) =
                | equal : a -> a -> Bool

            let same : a -> a -> Bool requires {Eq(a)} =
                given (left) -> given (right) -> Eq.equal(left)(right)

            same
            """;

        Lowering lowering = Lower(source, out Diagnostics diagnostics);

        diagnostics.StructuredErrors.ShouldBeEmpty();
        Lowering.HoverTypeInfo hover = lowering.GetTypeAtPosition(
            source.IndexOf("same :", StringComparison.Ordinal)).ShouldNotBeNull();
        hover.Constraints.ShouldNotBeNull();
        hover.Constraints.Single().Trait.Name.ShouldBe("Eq");
        lowering.LastTraitConstraints.Single().Trait.Name.ShouldBe("Eq");
    }

    [Test]
    public void OperatorUseEmitsTheMappedTraitConstraintInsteadOfDefaultingToInt()
    {
        const string source = """
            trait Eq(a) =
                | equal : a -> a -> Bool

            let same : a -> Bool requires {Eq(a)} =
                given (value) -> value == value

            same
            """;

        Lowering lowering = Lower(source, out Diagnostics diagnostics);

        diagnostics.StructuredErrors.ShouldBeEmpty();
        lowering.FormatType(lowering.LastLoweredType!).ShouldBe("a -> Bool");
        lowering.LastTraitConstraints.Single().Trait.Name.ShouldBe("Eq");
        lowering.LastTraitConstraints.Single().TypeArgs.Single().ShouldBeOfType<TypeRef.TVar>();
    }

    [Test]
    public void ConstraintsPropagateThroughHigherOrderCallsAndPartialMethodApplication()
    {
        const string source = """
            trait Eq(a) =
                | equal : a -> a -> Bool

            let partial : a -> (a -> Bool) requires {Eq(a)} =
                given (left) -> Eq.equal(left)

            let apply : (a -> b) -> a -> b =
                given (function) -> given (value) -> function(value)

            let through : a -> a -> Bool requires {Eq(a)} =
                given (left) -> given (right) -> apply(partial(left))(right)

            through
            """;

        Lowering lowering = Lower(source, out Diagnostics diagnostics);

        Lowering.HoverTypeInfo partialHover = lowering.GetTypeAtPosition(
            source.IndexOf("partial :", StringComparison.Ordinal)).ShouldNotBeNull();
        TypeRef.TVar partialArgument = ((TypeRef.TFun)partialHover.Type).Arg.ShouldBeOfType<TypeRef.TVar>();
        TypeRef.TVar partialConstraintArgument = partialHover.Constraints!.Single().TypeArgs.Single()
            .ShouldBeOfType<TypeRef.TVar>();
        partialConstraintArgument.Id.ShouldBe(partialArgument.Id);
        diagnostics.StructuredErrors.ShouldBeEmpty();
        lowering.LastTraitConstraints.Single().Trait.Name.ShouldBe("Eq");
    }

    [Test]
    public void WrittenRequiresMustCoverInferredRequirements()
    {
        const string source = """
            trait Eq(a) =
                | equal : a -> a -> Bool
            trait Show(a) =
                | show : a -> Str

            let bad : a -> Bool requires {Show(a)} =
                given (value) -> Eq.equal(value)(value)

            0
            """;

        _ = Lower(source, out Diagnostics diagnostics);

        diagnostics.StructuredErrors.Select(error => error.Message)
            .ShouldContain(message => message.Contains("does not include inferred requirement 'Eq", StringComparison.Ordinal));
    }

    [Test]
    public void WrittenRequiresCannotIntroduceUnjustifiedEvidence()
    {
        const string source = """
            trait Eq(a) =
                | equal : a -> a -> Bool

            let identity : a -> a requires {Eq(a)} = given (value) -> value
            0
            """;

        _ = Lower(source, out Diagnostics diagnostics);

        diagnostics.StructuredErrors.Select(error => error.Message)
            .ShouldContain(message => message.Contains("includes unjustified requirement 'Eq", StringComparison.Ordinal));
    }

    [Test]
    public void ExportedNonRecursiveBindingInfersAndGeneralizesItsConstraint()
    {
        const string source = """
            trait Eq(a) =
                | equal : a -> a -> Bool

            let same = given (value) -> Eq.equal(value)(value)
            0
            """;

        Lowering lowering = Lower(source, out Diagnostics diagnostics);

        diagnostics.StructuredErrors.ShouldBeEmpty();
        Lowering.HoverTypeInfo hover = lowering.GetTypeAtPosition(
            source.IndexOf("same =", StringComparison.Ordinal)).ShouldNotBeNull();
        hover.Constraints.ShouldNotBeNull();
        hover.Constraints.Single().Trait.Name.ShouldBe("Eq");
    }

    [Test]
    public void AmbiguousConstraintVariablesAreRejected()
    {
        const string source = """
            trait Default(a) =
                | value : Unit -> a

            let ambiguous : Int requires {Default(a)} = 1
            0
            """;

        _ = Lower(source, out Diagnostics diagnostics);

        diagnostics.StructuredErrors.Select(error => error.Message)
            .ShouldContain(message => message.Contains("is ambiguous", StringComparison.Ordinal));
    }

    [Test]
    public void EveryMappedOperatorFamilyCanCarryAnAbstractConstraint()
    {
        const string source = """
            trait Add(a) = | add : a -> a -> a
            trait Subtract(a) = | subtract : a -> a -> a
            trait Multiply(a) = | multiply : a -> a -> a
            trait Divide(a) = | divide : a -> a -> a
            trait Remainder(a) = | remainder : a -> a -> a
            trait Negate(a) = | negate : a -> a
            trait Not(a) = | not : a -> a
            trait BitAnd(a) = | bitAnd : a -> a -> a
            trait BitOr(a) = | bitOr : a -> a -> a
            trait BitXor(a) = | bitXor : a -> a -> a
            trait ShiftLeft(a) = | shiftLeft : a -> a -> a
            trait ShiftRight(a) = | shiftRight : a -> a -> a
            trait BitwiseNot(a) = | bitwiseNot : a -> a
            trait Eq(a) =
                | equal : a -> a -> Bool
                | notEqual : a -> a -> Bool
            trait Ord(a) requires {Eq(a)} =
                | compare : a -> a -> Int
                | less : a -> a -> Bool
                | lessOrEqual : a -> a -> Bool
                | greater : a -> a -> Bool
                | greaterOrEqual : a -> a -> Bool

            let add : a -> a -> a requires {Add(a)} = given (x) -> given (y) -> x + y
            let subtract : a -> a -> a requires {Subtract(a)} = given (x) -> given (y) -> x - y
            let multiply : a -> a -> a requires {Multiply(a)} = given (x) -> given (y) -> x * y
            let divide : a -> a -> a requires {Divide(a)} = given (x) -> given (y) -> x / y
            let remainder : a -> a -> a requires {Remainder(a)} = given (x) -> given (y) -> x % y
            let negate : a -> a requires {Negate(a)} = given (x) -> -x
            let invert : a -> a requires {Not(a)} = given (x) -> !x
            let bitAnd : a -> a -> a requires {BitAnd(a)} = given (x) -> given (y) -> x & y
            let bitOr : a -> a -> a requires {BitOr(a)} = given (x) -> given (y) -> x | y
            let bitXor : a -> a -> a requires {BitXor(a)} = given (x) -> given (y) -> x ^ y
            let shiftLeft : a -> a -> a requires {ShiftLeft(a)} = given (x) -> given (y) -> x << y
            let shiftRight : a -> a -> a requires {ShiftRight(a)} = given (x) -> given (y) -> x >> y
            let bitwiseNot : a -> a requires {BitwiseNot(a)} = given (x) -> ~x
            let equal : a -> a -> Bool requires {Eq(a)} = given (x) -> given (y) -> x == y
            let notEqual : a -> a -> Bool requires {Eq(a)} = given (x) -> given (y) -> x != y
            let less : a -> a -> Bool requires {Ord(a)} = given (x) -> given (y) -> x < y
            let lessEqual : a -> a -> Bool requires {Ord(a)} = given (x) -> given (y) -> x <= y
            let greater : a -> a -> Bool requires {Ord(a)} = given (x) -> given (y) -> x > y
            let greaterEqual : a -> a -> Bool requires {Ord(a)} = given (x) -> given (y) -> x >= y
            0
            """;

        _ = Lower(source, out Diagnostics diagnostics);

        diagnostics.StructuredErrors.ShouldBeEmpty();
    }

    [Test]
    public void RecursiveConstraintIsCheckedAndPropagatedToTheContinuation()
    {
        const string source = """
            trait Eq(a) =
                | equal : a -> a -> Bool

            let recursive contains : a -> List(a) -> Bool requires {Eq(a)} =
                given (needle) -> given (items) ->
                    match items with
                        | [] -> false
                        | item :: rest ->
                            if item == needle then true else contains(needle)(rest)

            contains
            """;

        Lowering lowering = Lower(source, out Diagnostics diagnostics);

        diagnostics.StructuredErrors.ShouldBeEmpty();
        lowering.LastTraitConstraints.Single().Trait.Name.ShouldBe("Eq");
    }

    [Test]
    public void ConstrainedRecursiveBindingInfersItsEvidence()
    {
        const string source = """
            trait Eq(a) =
                | equal : a -> a -> Bool

            implement Eq(Int) =
                | equal = given (left) -> given (right) -> left == right

            let recursive same =
                given (left) -> given (right) ->
                    if left == right then true else same(left)(right)

            same(1)(1)
            """;

        _ = Lower(source, out Diagnostics diagnostics);

        diagnostics.StructuredErrors.ShouldBeEmpty();
    }

    [Test]
    public void MutualRecursiveConstraintsPropagateAcrossSiblingCalls()
    {
        const string source = """
            trait Eq(a) =
                | equal : a -> a -> Bool

            let recursive first : a -> a -> Bool requires {Eq(a)} =
                given (left) -> given (right) ->
                    if Eq.equal(left)(right) then true else second(left)(right)
            and second : a -> a -> Bool requires {Eq(a)} =
                given (left) -> given (right) ->
                    if Eq.equal(left)(right) then true else first(left)(right)

            first
            """;

        Lowering lowering = Lower(source, out Diagnostics diagnostics);

        diagnostics.StructuredErrors.ShouldBeEmpty();
        lowering.LastTraitConstraints.Single().Trait.Name.ShouldBe("Eq");
    }

    [Test]
    public void ConstraintsRemainSeparateFromCapabilityRows()
    {
        const string source = """
            capability Log =
                | emit : Str -> Unit

            trait Eq(a) =
                | equal : a -> a -> Bool

            let compare : a -> a -> Bool needs {Log} requires {Eq(a)} =
                given (left) -> given (right) ->
                    let ignored = perform Log.emit("compare")
                    in Eq.equal(left)(right)

            compare
            """;

        Lowering lowering = Lower(source, out Diagnostics diagnostics);

        diagnostics.StructuredErrors.ShouldBeEmpty();
        lowering.LastTraitConstraints.Single().Trait.Name.ShouldBe("Eq");
        lowering.FormatType(lowering.LastLoweredType!).ShouldContain("uses {Log}");
    }

    [Test]
    public void ConstraintPropagatesOutOfAnAsyncBody()
    {
        const string source = """
            trait Eq(a) =
                | equal : a -> a -> Bool

            let delayed : a -> Task(Str, Bool) requires {Eq(a)} =
                given (value) -> async(Eq.equal(value)(value))

            delayed
            """;

        Lowering lowering = Lower(source, out Diagnostics diagnostics);

        diagnostics.StructuredErrors.ShouldBeEmpty();
        lowering.LastTraitConstraints.Single().Trait.Name.ShouldBe("Eq");
    }

    [Test]
    public void ConstraintPropagatesThroughAnImportedQualifiedFunction()
    {
        string root = Path.Combine(Path.GetTempPath(), $"ashes-trait-inference-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(
                Path.Combine(root, "ashes.json"),
                """{"name":"trait-inference","entry":"Main.ash","sourceRoots":["."]}""");
            File.WriteAllText(Path.Combine(root, "Compare.ash"), """
                trait Equivalent(a) =
                    | equal : a -> a -> Bool

                let same : a -> a -> Bool requires {Equivalent(a)} =
                    given (left) -> given (right) -> Equivalent.equal(left)(right)
                """);
            File.WriteAllText(Path.Combine(root, "Main.ash"), """
                import Compare
                implement Equivalent(Int) =
                    | equal = given (left) -> given (right) -> left == right
                Compare.same(1)(1)
                """);

            ProjectCompilationPlan plan = ProjectSupport.BuildCompilationPlan(
                ProjectSupport.LoadProject(Path.Combine(root, "ashes.json")));
            CombinedCompilationLayout layout = ProjectSupport.BuildCompilationLayout(plan);
            Diagnostics diagnostics = new();
            Ashes.Frontend.Program program = new Parser(layout.Source, diagnostics).ParseProgram();
            Lowering lowering = new(diagnostics);
            lowering.SetSourceContext(layout);
            _ = lowering.Lower(program);

            diagnostics.StructuredErrors.ShouldBeEmpty();
            lowering.LastTraitConstraints.ShouldBeEmpty();
            TraitEvidencePlan.Instance evidence = lowering.ResolvedConcreteTraitEvidence.Single(plan => string.Equals(
                    plan.Goal.Trait.QualifiedName,
                    "Compare.Equivalent",
                    StringComparison.Ordinal))
                .ShouldBeOfType<TraitEvidencePlan.Instance>();
            evidence.Goal.Trait.QualifiedName.ShouldBe("Compare.Equivalent");
            evidence.Goal.TypeArgs.Single().ShouldBeOfType<TypeRef.TInt>();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static Lowering Lower(string source, out Diagnostics diagnostics)
    {
        diagnostics = new Diagnostics();
        Ashes.Frontend.Program program = new Parser(source, diagnostics).ParseProgram();
        Lowering lowering = new(diagnostics);
        lowering.SetSourceContext("<memory>", source);
        _ = lowering.Lower(program);
        return lowering;
    }
}
