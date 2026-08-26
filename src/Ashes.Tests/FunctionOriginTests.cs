using Ashes.Frontend;
using Ashes.Semantics;
using Shouldly;

namespace Ashes.Tests;

/// <summary>
/// Stable source and generated-function identities used by compiler reporting. These tests pin
/// lineage metadata only; it must remain observational and must survive semantic IR rewrites.
/// </summary>
public sealed class FunctionOriginTests
{
    [Test]
    public void Named_curried_function_keeps_source_location_and_generated_lineage()
    {
        const string source =
            """
            let add left right = left + right
            in add(20)(22)
            """;

        (Lowering lowering, IrProgram program) = LowerProgram(
            source,
            filePath: "/tmp/function-origin.ash");

        FunctionOwnershipSummary? summary = lowering.GetOwnershipSummary("add");
        summary.ShouldNotBeNull();
        summary.Origin.SourceName.ShouldBe("add");
        summary.Origin.DeclarationLocation.ShouldBe(
            new SourceLocation("/tmp/function-origin.ash", 1, 5));
        summary.Origin.DeclarationOffset.ShouldBe(4);

        IrFunction sourceFunction = program.Functions.Single(
            function => function.Origin is
            {
                Kind: IrFunctionOriginKind.SourceFunction,
                Source.SourceName: "add"
            });
        IrFunction closureHelper = program.Functions.Single(
            function => function.Origin is
            {
                Kind: IrFunctionOriginKind.ClosureHelper,
                Source.SourceName: "add"
            });

        sourceFunction.Origin.ShouldNotBeNull();
        sourceFunction.Origin.Source.ShouldBe(summary.Origin);
        sourceFunction.Origin.GeneratedLabel.ShouldBe(sourceFunction.Label);
        sourceFunction.Origin.ParentGeneratedLabel.ShouldBeNull();

        closureHelper.Origin.ShouldNotBeNull();
        closureHelper.Origin.Source.ShouldBe(summary.Origin);
        closureHelper.Origin.GeneratedLabel.ShouldBe(closureHelper.Label);
        closureHelper.Origin.ParentGeneratedLabel.ShouldBe(sourceFunction.Label);
        closureHelper.Origin.GenerationLocation.ShouldNotBeNull();
    }

    [Test]
    public void Same_named_local_summaries_have_distinct_stable_source_origins()
    {
        const string source =
            """
            let first input =
                let go value = value
                in go(input)
            let second input =
                let go value = value
                in go(input)
            in (first(1), second(2))
            """;

        (Lowering lowering, _) = LowerProgram(
            source,
            filePath: "/tmp/colliding-function-origins.ash");

        IReadOnlyList<FunctionOwnershipSummary> helpers = lowering.GetOwnershipSummaries("go");
        helpers.Count.ShouldBe(2);

        SourceFunctionOrigin first = helpers[0].Origin;
        SourceFunctionOrigin second = helpers[1].Origin;
        first.SourceName.ShouldBe("go");
        second.SourceName.ShouldBe("go");
        first.ShouldNotBe(second);
        first.DeclarationOffset.ShouldNotBe(second.DeclarationOffset);
        first.DeclarationLocation.ShouldNotBe(second.DeclarationLocation);

        helpers.Select(summary => summary.Origin.DeclarationOffset)
            .Distinct()
            .Count()
            .ShouldBe(2);
    }

    [Test]
    public void Every_emitted_function_has_a_matching_origin()
    {
        const string source =
            """
            let recursive isEven n =
                if n == 0 then true else isOdd(n - 1)
            and isOdd n =
                if n == 0 then false else isEven(n - 1)

            isEven(8)
            """;

        (_, IrProgram program) = LowerProgram(source);

        IrFunction[] functions = [program.EntryFunction, .. program.Functions];
        functions.ShouldNotBeEmpty();
        foreach (IrFunction function in functions)
        {
            function.Origin.ShouldNotBeNull();
        }

        functions.ShouldAllBe(function =>
            string.Equals(
                function.Label,
                function.Origin!.GeneratedLabel,
                StringComparison.Ordinal));
        functions.Select(function => function.Origin!.GeneratedLabel)
            .Distinct(StringComparer.Ordinal)
            .Count()
            .ShouldBe(functions.Length);
    }

    [Test]
    public void Ir_optimizer_preserves_function_origins()
    {
        const string source =
            """
            let add left right = left + right + 0
            in add(20)(22)
            """;

        (_, IrProgram program) = LowerProgram(source);
        IReadOnlyDictionary<string, IrFunctionOrigin?> origins = AllFunctions(program)
            .ToDictionary(
                function => function.Label,
                function => function.Origin,
                StringComparer.Ordinal);

        IrProgram optimized = IrOptimizer.Optimize(program);

        // A function the optimizer itself generates (a scalarized callee variant) names itself and
        // points at the lowered function it was cloned from; every other origin is untouched.
        foreach (IrFunction function in AllFunctions(optimized))
        {
            if (origins.TryGetValue(function.Label, out IrFunctionOrigin? lowered))
            {
                function.Origin.ShouldBe(lowered);
                continue;
            }

            IrFunctionOrigin generated = function.Origin.ShouldNotBeNull();
            generated.GeneratedLabel.ShouldBe(function.Label);
            IrFunctionOrigin parent = origins[generated.ParentGeneratedLabel.ShouldNotBeNull()].ShouldNotBeNull();
            (generated with { GeneratedLabel = parent.GeneratedLabel, ParentGeneratedLabel = parent.ParentGeneratedLabel })
                .ShouldBe(parent);
        }
    }

    [Test]
    public void Mutual_recursion_members_dispatch_and_wrappers_keep_actual_origin_kinds()
    {
        const string source =
            """
            let recursive isEven n =
                if n == 0 then true else isOdd(n - 1)
            and isOdd n =
                if n == 0 then false else isEven(n - 1)

            isEven(8)
            """;

        (_, IrProgram program) = LowerProgram(
            source,
            filePath: "/tmp/mutual-function-origins.ash");
        IrFunction[] functions = program.Functions.ToArray();

        IrFunction[] sourceMembers = functions
            .Where(function => function.Origin?.Kind == IrFunctionOriginKind.SourceFunction)
            .ToArray();
        sourceMembers.Length.ShouldBe(2);
        sourceMembers.Select(function => function.Origin!.Source!.SourceName)
            .Order(StringComparer.Ordinal)
            .ShouldBe(["isEven", "isOdd"]);

        IrFunction dispatch = functions.Single(
            function => function.Origin?.Kind == IrFunctionOriginKind.MutualRecursionDispatch);
        dispatch.Origin.ShouldNotBeNull();
        dispatch.Origin.Source.ShouldBeNull();
        dispatch.Origin.CompilerOwner.ShouldNotBeNull();
        dispatch.Origin.CompilerOwner.Kind.ShouldBe(CompilerFunctionOwnerKind.MutualRecursionGroup);

        IrFunction[] wrappers = functions
            .Where(function => function.Origin?.Kind == IrFunctionOriginKind.MutualRecursionWrapper)
            .ToArray();
        wrappers.Length.ShouldBe(2);
        wrappers.Select(function => function.Origin!.Source!.SourceName)
            .Order(StringComparer.Ordinal)
            .ShouldBe(["isEven", "isOdd"]);

        IReadOnlyDictionary<string, IrFunction> sourceMemberByLabel = sourceMembers.ToDictionary(
            function => function.Label,
            StringComparer.Ordinal);
        foreach (IrFunction wrapper in wrappers)
        {
            wrapper.Origin.ShouldNotBeNull();
            wrapper.Origin.ParentGeneratedLabel.ShouldNotBeNull();
            sourceMemberByLabel.ShouldContainKey(wrapper.Origin.ParentGeneratedLabel);
        }
    }

    [Test]
    public void Compilation_layout_retains_module_qualified_source_name()
    {
        const string source =
            """
            let addOne value = value + 1
            in addOne(41)
            """;
        const string filePath = "/tmp/project-main.ash";
        CombinedCompilationLayout layout = ProjectSupport.BuildStandaloneCompilationLayout(
            source,
            [],
            filePath);

        Diagnostics diagnostics = new();
        Program syntax = new Parser(layout.Source, diagnostics).ParseProgram();
        diagnostics.StructuredErrors.ShouldBeEmpty();
        Lowering lowering = new(diagnostics);
        lowering.SetSourceContext(layout);

        IrProgram program = lowering.Lower(syntax);

        diagnostics.StructuredErrors.ShouldBeEmpty();
        FunctionOwnershipSummary? summary = lowering.GetOwnershipSummary("addOne");
        summary.ShouldNotBeNull();
        summary.Origin.SourceName.ShouldBe("addOne");
        summary.Origin.QualifiedName.ShouldBe("Main.addOne");
        summary.Origin.DeclarationLocation.ShouldBe(new SourceLocation(filePath, 1, 5));

        IrFunction sourceFunction = program.Functions.Single(
            function => function.Origin?.Kind == IrFunctionOriginKind.SourceFunction);
        sourceFunction.Origin!.Source.ShouldBe(summary.Origin);
    }

    private static (Lowering Lowering, IrProgram Program) LowerProgram(
        string source,
        string? filePath = null)
    {
        Diagnostics diagnostics = new();
        Program syntax = new Parser(source, diagnostics).ParseProgram();
        diagnostics.StructuredErrors.ShouldBeEmpty();
        Lowering lowering = new(diagnostics);
        if (filePath is not null)
        {
            lowering.SetSourceContext(filePath, source);
        }

        IrProgram program = lowering.Lower(syntax);
        diagnostics.StructuredErrors.ShouldBeEmpty();
        return (lowering, program);
    }

    private static IEnumerable<IrFunction> AllFunctions(IrProgram program)
    {
        yield return program.EntryFunction;
        foreach (IrFunction function in program.Functions)
        {
            yield return function;
        }
    }
}
