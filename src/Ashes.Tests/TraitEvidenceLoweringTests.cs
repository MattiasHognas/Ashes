using Ashes.Frontend;
using Ashes.Semantics;
using Shouldly;

namespace Ashes.Tests;

public sealed class TraitEvidenceLoweringTests
{
    [Test]
    public void ConcreteTraitMethodCallUsesTheSelectedImplementation()
    {
        const string source = """
            trait Eq(a) =
                | equal : a -> a -> Bool

            implement Eq(Int) =
                | equal = given (left) -> given (right) -> left == right

            Eq.equal(1)(1)
            """;

        IrProgram program = Lower(source, out Diagnostics diagnostics);

        diagnostics.StructuredErrors.ShouldBeEmpty();
        program.EntryFunction.Instructions.Any(instruction => instruction is IrInst.CallClosure)
            .ShouldBeTrue();
    }

    [Test]
    public void NominalOperatorUsesDictionaryDispatchWhilePrimitiveOperatorKeepsItsSpecializedIr()
    {
        IrProgram primitive = Lower("1 + 2", out Diagnostics primitiveDiagnostics);
        const string nominalSource = """
            type Box = | Box(Int)
            trait Add(a) = | add : a -> a -> a
            implement Add(Box) =
                | add = given (left) -> given (right) -> Box(0)
            Box(1) + Box(2)
            """;
        IrProgram nominal = Lower(nominalSource, out Diagnostics nominalDiagnostics);

        primitiveDiagnostics.StructuredErrors.ShouldBeEmpty();
        nominalDiagnostics.StructuredErrors.ShouldBeEmpty();
        primitive.EntryFunction.Instructions.ShouldContain(instruction => instruction is IrInst.AddInt);
        nominal.EntryFunction.Instructions.ShouldContain(instruction => instruction is IrInst.CallClosure);
    }

    [Test]
    public void PrimitiveOperatorSpecializationCanBeDisabledWithoutChangingTheEvidencePath()
    {
        const string source = """
            trait Eq(a) = | equal : a -> a -> Bool
            implement Eq(Int) =
                | equal = given (left) -> given (right) -> left == right
            7 == 7
            """;

        IrProgram specialized = Lower(source, out Diagnostics specializedDiagnostics);
        IrProgram dictionaryOnly = Lower(
            source,
            out Diagnostics dictionaryDiagnostics,
            new LoweringConfiguration(EnableTraitOperatorSpecialization: false));

        specializedDiagnostics.StructuredErrors.ShouldBeEmpty();
        dictionaryDiagnostics.StructuredErrors.ShouldBeEmpty();
        specialized.EntryFunction.Instructions.ShouldContain(
            instruction => instruction is IrInst.CmpIntEq);
        dictionaryOnly.EntryFunction.Instructions.ShouldContain(
            instruction => instruction is IrInst.CallClosure);
    }

    [Test]
    public void AbstractConstraintIsPassedAsAnImmutableDictionaryAtAConcreteCall()
    {
        const string source = """
            trait Eq(a) =
                | equal : a -> a -> Bool

            implement Eq(Int) =
                | equal = given (left) -> given (right) -> left == right

            let same : a -> a -> Bool requires {Eq(a)} =
                given (left) -> given (right) -> Eq.equal(left)(right)

            same(1)(1)
            """;

        IrProgram program = Lower(source, out Diagnostics diagnostics);

        diagnostics.StructuredErrors.ShouldBeEmpty();
        program.Functions.Count.ShouldBeGreaterThanOrEqualTo(3);
        program.EntryFunction.Instructions.Count(instruction => instruction is IrInst.CallClosure)
            .ShouldBeGreaterThanOrEqualTo(3);
    }

    [Test]
    public void InferredLocalConstraintIsElaboratedBeforeRuntimeLowering()
    {
        const string source = """
            trait Eq(a) =
                | equal : a -> a -> Bool

            implement Eq(Int) =
                | equal = given (left) -> given (right) -> left == right

            let same value = Eq.equal(value)(value)
            in same(1)
            """;

        IrProgram program = Lower(source, out Diagnostics diagnostics);

        diagnostics.StructuredErrors.ShouldBeEmpty();
        program.EntryFunction.Instructions.Count(instruction => instruction is IrInst.CallClosure)
            .ShouldBeGreaterThanOrEqualTo(2);
    }

    [Test]
    public void ConcreteRecursiveOperatorTypesAreElaboratedBeforeRuntimeLowering()
    {
        const string source = """
            trait Add(a) = | add : a -> a -> a
            trait Eq(a) = | equal : a -> a -> Bool
            trait Ord(a) requires {Eq(a)} = | greaterOrEqual : a -> a -> Bool

            implement Add(Int) = | add = given (left) -> given (right) -> left + right
            implement Eq(Int) = | equal = given (left) -> given (right) -> left == right
            implement Ord(Int) = | greaterOrEqual = given (left) -> given (right) -> left >= right

            let recursive count current limit =
                if current >= limit
                then current
                else count(current + 1)(limit)

            count(0)(3)
            """;

        IrProgram program = Lower(source, out Diagnostics diagnostics);

        diagnostics.StructuredErrors.ShouldBeEmpty();
        IrInst[] countInstructions = program.Functions
            .Where(function => string.Equals(
                function.Origin?.Source?.SourceName,
                "count",
                StringComparison.Ordinal))
            .SelectMany(function => function.Instructions)
            .ToArray();
        countInstructions
            .ShouldContain(instruction => instruction is IrInst.CmpIntGe);
        countInstructions
            .ShouldContain(instruction => instruction is IrInst.AddInt);
    }

    [Test]
    public void ConcreteCallSpecializesRecursivePrimitiveTraitOperatorsInsideTheLoop()
    {
        const string source = """
            trait Add(a) =
                | add : a -> a -> a
            implement Add(Str) =
                | add = given (left) -> given (right) -> left + right

            let recursive appendWith make count value =
                if count <= 0
                then value
                else appendWith(make)(count - 1)(value + make(count))

            let make : Int -> Str = given (count) -> if count <= 0 then "x" else "y"

            appendWith(make)(10)("")
            """;

        IrProgram program = Lower(source, out Diagnostics diagnostics);

        diagnostics.StructuredErrors.ShouldBeEmpty();
        IrFunction specialization = program.Functions.Single(function =>
            function.Origin?.Kind == IrFunctionOriginKind.TraitOperatorSpecialization
            && string.Equals(
                function.Origin.Source?.SourceName,
                "appendWith",
                StringComparison.Ordinal));
        IrInst[] specializedInstructions = program.Functions
            .Where(function => string.Equals(
                function.Origin?.Source?.SourceName,
                "appendWith",
                StringComparison.Ordinal))
            .SelectMany(function => function.Instructions)
            .ToArray();
        specializedInstructions.ShouldContain(instruction => instruction is IrInst.ConcatStrTip);
        specializedInstructions.Any(instruction => instruction is IrInst.Jump jump
                && jump.Target.Contains("_body", StringComparison.Ordinal))
            .ShouldBeTrue();
        specialization.Label.ShouldContain("__trait");
    }

    [Test]
    public void HandlerArmCanUseAnOuterCapabilityWhileItsResultTypeIsInferred()
    {
        const string source = """
            capability Ask =
                | ask : Unit -> Int

            let inner unit =
                handle Ask.ask(Unit) with
                    | Ask.ask(_) ->
                        let outerValue = Ask.ask(Unit)
                        in resume(outerValue + 1)
                    | return(result) -> result

            handle inner(Unit) with
                | Ask.ask(_) -> resume(41)
                | return(result) -> result
            """;

        _ = Lower(source, out Diagnostics diagnostics);

        diagnostics.StructuredErrors.ShouldBeEmpty();
    }

    [Test]
    public void SupertraitOperatorUsesEvidenceProjectedFromTheRootDictionary()
    {
        const string source = """
            trait Eq(a) = | equal : a -> a -> Bool
            trait Ord(a) requires {Eq(a)} = | lessOrEqual : a -> a -> Bool
            implement Eq(Int) = | equal = given (left) -> given (right) -> left == right
            implement Ord(Int) = | lessOrEqual = given (left) -> given (right) -> left <= right

            let cmp left right =
                if left == right
                then 0
                else if left <= right then -1 else 1
            in (cmp(1)(1), cmp(1)(2), cmp(2)(1))
            """;

        IrProgram program = Lower(source, out Diagnostics diagnostics);

        diagnostics.StructuredErrors.ShouldBeEmpty();
        TraitDictionaryAbiAnnotation dictionary = program.TraitEvidence.DictionaryParameters
            .Single(annotation => string.Equals(annotation.Function, "cmp", StringComparison.Ordinal));
        dictionary.Trait.ShouldEndWith("Ord");
        dictionary.Supertraits.ShouldContain(name => name.EndsWith("Eq", StringComparison.Ordinal));
    }

    [Test]
    public void NestedConstrainedAliasUsesTheLexicallyNearestDictionaryOnce()
    {
        const string source = """
            trait Eq(a) = | equal : a -> a -> Bool
            implement Eq(Int) =
                | equal = given (left) -> given (right) -> left == right

            let same : a -> a -> Bool requires {Eq(a)} =
                given (left) -> given (right) -> Eq.equal(left)(right)

            let outer : a -> a -> Bool requires {Eq(a)} =
                let alias = same
                in given (left) -> given (right) -> alias(left)(right)

            outer(1)(1)
            """;

        _ = Lower(source, out Diagnostics diagnostics);

        diagnostics.StructuredErrors.ShouldBeEmpty();
    }

    [Test]
    public void NestedRecursionCapturesEvidenceWithoutBorrowingMetadataFromAnotherBindingNamedGo()
    {
        const string source = """
            trait Eq(a) = | equal : a -> a -> Bool
            implement Eq(Int) =
                | equal = given (left) -> given (right) -> left == right

            let unrelated =
                let recursive go : Int -> Int =
                    given (value) -> if value == 0 then 0 else go(value - 1)
                in go(1)

            let contains : a -> List(a) -> Bool requires {Eq(a)} =
                given (needle) ->
                    let recursive go : List(a) -> Bool =
                        given (items) ->
                            match items with
                                | [] -> false
                                | item :: rest ->
                                    if Eq.equal(needle)(item) then true else go(rest)
                    in go

            if unrelated == 0 then contains(2)([1, 2]) else false
            """;

        _ = Lower(source, out Diagnostics diagnostics);

        diagnostics.StructuredErrors.ShouldBeEmpty();
    }

    [Test]
    public void NestedClosureCapturesTheAbstractDictionaryMethodNormally()
    {
        const string source = """
            trait Eq(a) =
                | equal : a -> a -> Bool

            implement Eq(Int) =
                | equal = given (left) -> given (right) -> left == right

            let makeComparer : a -> (a -> Bool) requires {Eq(a)} =
                given (left) ->
                    given (right) -> Eq.equal(left)(right)

            makeComparer(1)(1)
            """;

        IrProgram program = Lower(source, out Diagnostics diagnostics);

        diagnostics.StructuredErrors.ShouldBeEmpty();
        program.Functions.SelectMany(function => function.Instructions)
            .OfType<IrInst.MakeClosure>()
            .Any(closure => closure.EnvSizeBytes > 0)
            .ShouldBeTrue();
    }

    [Test]
    public void ConcreteHigherOrderUseAppliesEvidenceBeforeCapturingTheFunction()
    {
        const string source = """
            trait Combine(a) =
                | combine : a -> a -> a

            implement Combine(Int) =
                | combine = given (left) -> given (right) -> left + right

            let use folder = folder(20)(22)
            in
                let merge left right = Combine.combine(left)(right)
                in use(merge)
            """;

        IrProgram program = Lower(source, out Diagnostics diagnostics);

        diagnostics.StructuredErrors.ShouldBeEmpty();
        program.Functions
            .SelectMany(function => function.Instructions)
            .Concat(program.EntryFunction.Instructions)
            .Any(instruction => instruction is IrInst.CopyOutClosure
            {
                RuntimeManaged: true,
                Purpose: IrInst.CopyOutPurpose.RcNormalization,
            })
            .ShouldBeTrue();
    }

    [Test]
    public void ExpectedCallResultResolvesAnImplicitConstrainedFunctionArgument()
    {
        const string source = """
            trait Ord(a) =
                | before : a -> a -> Bool

            implement Ord(Int) =
                | before = given (left) -> given (right) -> left <= right

            let sortBy : (a -> a -> Bool) -> List(a) -> List(a) =
                given (_before) -> given (values) -> values

            let asc left right = Ord.before(left)(right)

            let show : List(Int) -> Int = given (_values) -> 0

            show(sortBy(asc)([]))
            """;

        _ = Lower(source, out Diagnostics diagnostics);

        diagnostics.StructuredErrors.ShouldBeEmpty();
    }

    [Test]
    public void DefaultMethodDispatchesThroughTheSelectedDictionary()
    {
        const string source = """
            trait Eq(a) =
                | equal : a -> a -> Bool
                | notEqual : a -> a -> Bool =
                    given (left) -> given (right) -> !Eq.equal(left)(right)

            implement Eq(Int) =
                | equal = given (left) -> given (right) -> left == right

            Eq.notEqual(1)(2)
            """;

        IrProgram program = Lower(source, out Diagnostics diagnostics);

        diagnostics.StructuredErrors.ShouldBeEmpty();
        program.EntryFunction.Instructions.Count(instruction => instruction is IrInst.CallClosure)
            .ShouldBeGreaterThanOrEqualTo(2);
    }

    [Test]
    public void SupertraitEvidenceIsStoredInAndDestructuredFromTheDictionary()
    {
        const string source = """
            trait Eq(a) =
                | equal : a -> a -> Bool

            trait Ord(a) requires {Eq(a)} =
                | less : a -> a -> Bool

            implement Eq(Int) =
                | equal = given (left) -> given (right) -> left == right

            implement Ord(Int) =
                | less = given (left) -> given (right) -> left < right

            let orderedEqual : a -> a -> Bool requires {Ord(a)} =
                given (left) -> given (right) ->
                    if Ord.less(left)(right) then false else Eq.equal(left)(right)

            orderedEqual(1)(1)
            """;

        IrProgram program = Lower(source, out Diagnostics diagnostics);

        diagnostics.StructuredErrors.ShouldBeEmpty();
        program.EntryFunction.Instructions.Any(instruction => instruction is IrInst.Alloc)
            .ShouldBeTrue();
    }

    [Test]
    public void RecursiveFunctionThreadsItsDictionaryOnTheSelfEdge()
    {
        const string source = """
            trait Eq(a) =
                | equal : a -> a -> Bool
            implement Eq(Int) =
                | equal = given (left) -> given (right) -> left == right

            let recursive contains : a -> List(a) -> Bool requires {Eq(a)} =
                given (needle) -> given (items) ->
                    match items with
                        | [] -> false
                        | item :: rest ->
                            if Eq.equal(needle)(item) then true else contains(needle)(rest)

            contains(2)([1, 2, 3])
            """;

        IrProgram program = Lower(source, out Diagnostics diagnostics);

        diagnostics.StructuredErrors.ShouldBeEmpty();
        program.Functions.SelectMany(function => function.Instructions)
            .Any(instruction => instruction is IrInst.CallClosure or IrInst.Jump)
            .ShouldBeTrue();
    }

    [Test]
    public void GenericImplementationAnnotationsReuseTheImplementationHeadTypeParameters()
    {
        const string source = """
            trait Eq(a) =
                | equal : a -> a -> Bool

            implement Eq(Int) =
                | equal = given (left) -> given (right) -> left == right

            implement Eq(List(a)) requires {Eq(a)} =
                | equal =
                    let recursive equalLists : List(a) -> List(a) -> Bool requires {Eq(a)} =
                        given (left) ->
                            given (right) ->
                                match (left, right) with
                                    | ([], []) -> true
                                    | (x :: restX, y :: restY) ->
                                        if x == y then equalLists(restX)(restY) else false
                                    | _ -> false
                    in equalLists

            Eq.equal([1])([1])
            """;

        IrProgram program = Lower(source, out Diagnostics diagnostics);

        diagnostics.StructuredErrors.ShouldBeEmpty();
        program.Functions.SelectMany(function => function.Instructions)
            .ShouldContain(instruction => instruction is IrInst.CmpIntEq);
    }

    [Test]
    public void MutualRecursionThreadsTheDictionaryAcrossEverySiblingEdge()
    {
        const string source = """
            trait Eq(a) = | equal : a -> a -> Bool
            implement Eq(Int) =
                | equal = given (left) -> given (right) -> left == right

            let recursive first : a -> a -> Bool requires {Eq(a)} =
                given (left) -> given (right) ->
                    if Eq.equal(left)(right) then true else second(left)(right)
            and second : a -> a -> Bool requires {Eq(a)} =
                given (left) -> given (right) ->
                    if Eq.equal(left)(right) then true else first(left)(right)

            first(1)(1)
            """;

        _ = Lower(source, out Diagnostics diagnostics);

        diagnostics.StructuredErrors.ShouldBeEmpty();
    }

    [Test]
    public void ConstrainedFunctionStoredInAnAggregateRetainsEnclosingEvidence()
    {
        const string source = """
            trait Eq(a) = | equal : a -> a -> Bool
            implement Eq(Int) =
                | equal = given (left) -> given (right) -> left == right

            let same : a -> a -> Bool requires {Eq(a)} =
                given (left) -> given (right) -> Eq.equal(left)(right)

            let throughTuple : a -> a -> Bool requires {Eq(a)} =
                given (left) -> given (right) ->
                    match (same, left) with
                        | (compare, captured) -> compare(captured)(right)

            throughTuple(3)(3)
            """;

        _ = Lower(source, out Diagnostics diagnostics);

        diagnostics.StructuredErrors.ShouldBeEmpty();
    }

    [Test]
    public void AbstractEvidenceCrossesTwoImportedModuleBoundaries()
    {
        string root = Path.Combine(Path.GetTempPath(), $"ashes-trait-evidence-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "ashes.json"),
                """{"name":"trait-evidence","entry":"Main.ash","sourceRoots":["."]}""");
            File.WriteAllText(Path.Combine(root, "Traits.ash"),
                "trait Equivalent(a) = | equal : a -> a -> Bool\n");
            File.WriteAllText(Path.Combine(root, "Compare.ash"), """
                import Traits.Equivalent
                let same : a -> a -> Bool requires {Equivalent(a)} =
                    given (left) -> given (right) -> Equivalent.equal(left)(right)
                """);
            File.WriteAllText(Path.Combine(root, "Forward.ash"), """
                import Traits.Equivalent
                import Compare.same
                let forwarded : a -> a -> Bool requires {Equivalent(a)} =
                    given (left) -> given (right) -> same(left)(right)
                """);
            File.WriteAllText(Path.Combine(root, "Main.ash"), """
                import Traits.Equivalent
                import Forward.forwarded
                implement Equivalent(Int) =
                    | equal = given (left) -> given (right) -> left == right
                forwarded(9)(9)
                """);

            ProjectCompilationPlan plan = ProjectSupport.BuildCompilationPlan(
                ProjectSupport.LoadProject(Path.Combine(root, "ashes.json")));
            CombinedCompilationLayout layout = ProjectSupport.BuildCompilationLayout(plan);
            Diagnostics diagnostics = new();
            Ashes.Frontend.Program syntax = new Parser(layout.Source, diagnostics).ParseProgram();
            Lowering lowering = new(
                diagnostics,
                plan.ImportedStdModules,
                plan.MergedAliases,
                layout.ConstructorModules);
            lowering.SetSourceContext(layout);
            _ = lowering.Lower(syntax);

            diagnostics.StructuredErrors.ShouldBeEmpty();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void InferredConstraintCrossesAModuleBoundaryAndSupportsMultipleTypes()
    {
        string root = Path.Combine(Path.GetTempPath(), $"ashes-inferred-trait-evidence-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "ashes.json"),
                """{"name":"inferred-trait-evidence","entry":"Main.ash","sourceRoots":["."]}""");
            File.WriteAllText(Path.Combine(root, "Traits.ash"),
                "trait Equivalent(a) = | equal : a -> a -> Bool\n");
            File.WriteAllText(Path.Combine(root, "Compare.ash"), """
                import Traits.Equivalent
                let same left right = Equivalent.equal(left)(right)
                """);
            File.WriteAllText(Path.Combine(root, "Main.ash"), """
                import Traits.Equivalent
                import Compare.same
                implement Equivalent(Int) =
                    | equal = given (left) -> given (right) -> left == right
                implement Equivalent(Str) =
                    | equal = given (left) -> given (right) -> left == right
                (same(9)(9), same("ashes")("ashes"))
                """);

            ProjectCompilationPlan plan = ProjectSupport.BuildCompilationPlan(
                ProjectSupport.LoadProject(Path.Combine(root, "ashes.json")));
            CombinedCompilationLayout layout = ProjectSupport.BuildCompilationLayout(plan);
            Diagnostics diagnostics = new();
            Ashes.Frontend.Program syntax = new Parser(layout.Source, diagnostics).ParseProgram();
            Lowering lowering = new(
                diagnostics,
                plan.ImportedStdModules,
                plan.MergedAliases,
                layout.ConstructorModules);
            lowering.SetSourceContext(layout);
            _ = lowering.Lower(syntax);

            diagnostics.StructuredErrors.ShouldBeEmpty();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void ImportedAliasWrappedFunctionRetainsItsConcreteOperatorAnnotation()
    {
        string root = Path.Combine(Path.GetTempPath(), $"ashes-trait-annotation-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "ashes.json"),
                """{"name":"trait-annotation","entry":"Main.ash","sourceRoots":["."]}""");
            File.WriteAllText(Path.Combine(root, "Support.ash"),
                "let identity value = value\n");
            File.WriteAllText(Path.Combine(root, "Compare.ash"), """
                import Support.identity
                let equalInts : Int -> Int -> Bool =
                    given (left) -> given (right) -> identity(left) == identity(right)
                """);
            File.WriteAllText(Path.Combine(root, "Main.ash"), """
                import Compare.equalInts
                equalInts(4)(4)
                """);

            ProjectCompilationPlan plan = ProjectSupport.BuildCompilationPlan(
                ProjectSupport.LoadProject(Path.Combine(root, "ashes.json")));
            CombinedCompilationLayout layout = ProjectSupport.BuildCompilationLayout(plan);
            Diagnostics diagnostics = new();
            Ashes.Frontend.Program syntax = new Parser(layout.Source, diagnostics).ParseProgram();
            Lowering lowering = new(
                diagnostics,
                plan.ImportedStdModules,
                plan.MergedAliases,
                layout.ConstructorModules);
            lowering.SetSourceContext(layout);
            IrProgram program = lowering.Lower(syntax);

            diagnostics.StructuredErrors.ShouldBeEmpty();
            program.Functions.SelectMany(function => function.Instructions)
                .ShouldContain(instruction => instruction is IrInst.CmpIntEq);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void AsyncTransformationCapturesTraitEvidenceInTheTaskFrame()
    {
        const string source = """
            trait Eq(a) = | equal : a -> a -> Bool
            implement Eq(Int) =
                | equal = given (left) -> given (right) -> left == right
            let delayed : a -> Task(Str, Bool) requires {Eq(a)} =
                given (value) ->
                    async(match await async(value) with
                        | Ok(resumed) -> Eq.equal(value)(resumed)
                        | Error(_) -> false)
            delayed(7)
            """;

        IrProgram program = Lower(source, out Diagnostics diagnostics);

        diagnostics.StructuredErrors.ShouldBeEmpty();
        program.Functions.Any(function => function.Coroutine is not null).ShouldBeTrue();
    }

    [Test]
    public void FunctionAnnotationSeedsOnlyItsResultLambdaAcrossLeadingLetBindings()
    {
        const string source = """
            let generated : Int -> Str =
                let intermediate =
                    let floats =
                        [let identity = given (value: Float) -> value
                        in identity(2.5)]
                    in
                        match floats with
                            | [] -> 1.5
                            | head :: _ -> head
                in
                    given (number) -> ""
            in generated
            """;

        _ = Lower(source, out Diagnostics diagnostics);

        diagnostics.StructuredErrors.ShouldBeEmpty();
    }

    [Test]
    public void AbstractDictionaryMethodPreservesItsConcreteResultType()
    {
        const string source = """
            trait Hash(a) =
                | hash : a -> Int
            implement Hash(BigInt) =
                | hash = given (_) -> 7
            let sameHash : a -> Bool requires {Hash(a)} =
                given (value) -> Hash.hash(value) == Hash.hash(value)
            sameHash(7N)
            """;

        IrProgram program = Lower(source, out Diagnostics diagnostics);

        diagnostics.StructuredErrors.ShouldBeEmpty();
        program.Functions.SelectMany(function => function.Instructions)
            .ShouldContain(instruction => instruction is IrInst.CmpIntEq);
    }

    [Test]
    public void TraitMethodResultUsesTheExpectedConstrainedCallArgumentTypeBeforeResolution()
    {
        const string source = """
            trait Eq(a) =
                | equal : a -> a -> Bool
            trait Default(a) =
                | default : Unit -> a
            implement Eq(BigInt) =
                | equal = given (left) -> given (right) -> left == right
            implement Default(BigInt) =
                | default = given (_) -> 0N
            let assertEqual : a -> a -> Bool requires {Eq(a)} =
                given (expected) -> given (actual) -> Eq.equal(expected)(actual)
            assertEqual(0N)(Default.default(Unit))
            """;

        IrProgram program = Lower(source, out Diagnostics diagnostics);

        diagnostics.StructuredErrors.ShouldBeEmpty();
        program.EntryFunction.Instructions.ShouldContain(instruction => instruction is IrInst.BigIntFromInt);
    }

    [Test]
    public void DictionaryMethodClosureRetainsItsCapturedImplementationValue()
    {
        const string source = """
            trait Eq(a) = | equal : a -> a -> Bool
            let offset = 4
            implement Eq(Int) =
                | equal = given (left) -> given (right) ->
                    left + offset == right + offset
            let same : a -> a -> Bool requires {Eq(a)} =
                given (left) -> given (right) -> Eq.equal(left)(right)
            same(8)(8)
            """;

        IrProgram program = Lower(source, out Diagnostics diagnostics);

        diagnostics.StructuredErrors.ShouldBeEmpty();
        program.Functions.SelectMany(function => function.Instructions)
            .OfType<IrInst.MakeClosure>()
            .Any(closure => closure.EnvSizeBytes > 0)
            .ShouldBeTrue();
    }

    private static IrProgram Lower(
        string source,
        out Diagnostics diagnostics,
        LoweringConfiguration? configuration = null)
    {
        diagnostics = new Diagnostics();
        Ashes.Frontend.Program syntax = new Parser(source, diagnostics).ParseProgram();
        Lowering lowering = new(diagnostics, configuration: configuration);
        return lowering.Lower(syntax);
    }
}
