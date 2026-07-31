using Ashes.Frontend;
using Ashes.Semantics;
using Shouldly;

namespace Ashes.Tests;

// Validates the TCO promotion-profitability signal (Lowering.GetTcoPromotionProfitability) against
// the four cases the ownership-unification investigation identified: two real regressions found by
// removing the all-or-nothing TCO frame veto (a station-merge-shaped list/tree loop and a
// line-wrap-shaped string/list loop), the synthetic string/closure case that motivated wanting the
// veto gone in the first place, and a single-heap-parameter loop where the veto (and this signal)
// never had anything to do regardless.
public sealed class TcoPromotionCostSignalTests
{
    private static Lowering LowerProgram(string source)
        => LowerProgramAndIr(source).Lowering;

    private static (Lowering Lowering, IrProgram Program) LowerProgramAndIr(string source)
    {
        var diagnostics = new Diagnostics();
        var program = new Parser(source, diagnostics).ParseProgram();
        var lowering = new Lowering(diagnostics);
        IrProgram ir = lowering.Lower(program);
        diagnostics.Errors.ShouldBeEmpty();
        return (lowering, ir);
    }

    private static TcoParamPlacementTrace GetPlacement(
        Lowering lowering,
        string functionName,
        string parameterName)
    {
        IReadOnlyList<TcoParamPlacementTrace> decisions =
            lowering.GetTcoPlacementDecisions(functionName)
            ?? throw new InvalidOperationException($"No TCO placement snapshot for '{functionName}'.");
        return decisions.Single(trace =>
            string.Equals(trace.Current.ParameterName, parameterName, StringComparison.Ordinal));
    }

    [Test]
    public void Unknown_bytes_nested_in_a_rebuilt_list_element_stays_arena_based()
    {
        const string source =
            """
            let recursive build bytes n acc =
                if n <= 0
                then acc
                else build(bytes)(n - 1)((bytes, n) :: acc)

            match build(Ashes.Byte.fromText("x"))(3)([]) with
                | [] -> 0
                | _ :: _ -> 1
            """;

        (Lowering lowering, IrProgram ir) = LowerProgramAndIr(source);
        TcoParamPlacementTrace placement = GetPlacement(lowering, "build", "acc");

        placement.Current.Representation.ShouldBe(TcoPlacementRepresentation.Arena);
        placement.Current.Eligibility.ResolvedLayoutEligible.ShouldBeFalse();
        ir.Functions.Where(function => string.Equals(
                function.Origin?.Source?.SourceName,
                "build",
                StringComparison.Ordinal))
            .SelectMany(function => function.Instructions)
            .Any(instruction => instruction is IrInst.CopyOutArena
            {
                StaticSizeBytes: -1,
                RuntimeManaged: true,
            }).ShouldBeFalse();
    }

    [Test]
    public void Explicit_owned_bytes_nested_in_a_rebuilt_list_element_are_tco_eligible()
    {
        const string source =
            """
            let recursive build n acc =
                if n <= 0
                then acc
                else build(n - 1)((Ashes.Byte.singleton(1u8), 1) :: acc)

            match build(3)([]) with
                | [] -> 0
                | _ :: _ -> 1
            """;

        Lowering lowering = LowerProgram(source);
        TcoParamPlacementTrace placement = GetPlacement(lowering, "build", "acc");

        placement.Current.Representation.ShouldBe(TcoPlacementRepresentation.RuntimeRc);
        placement.Current.Eligibility.ResolvedLayoutEligible.ShouldBeTrue();
    }

    [Test]
    public void Annotated_string_parameter_is_placed_at_provisional_loop_entry()
    {
        const string source =
            """
            let recursive loop : Int -> Str -> Str = given n -> given acc ->
                if n <= 0
                then acc
                else loop(n - 1)(acc + "x")

            Ashes.IO.print(loop(4)(""))
            """;

        Lowering lowering = LowerProgram(source);
        TcoParamPlacementTrace placement = GetPlacement(lowering, "loop", "acc");

        placement.Current.Representation.ShouldBe(TcoPlacementRepresentation.RuntimeRc);
        placement.Current.FirstPromotedAt.ShouldBe(TcoPlacementResolutionPoint.ProvisionalLoopEntry);
        placement.History.ShouldContain(decision =>
            decision.ResolutionPoint == TcoPlacementResolutionPoint.ProvisionalLoopEntry
            && decision.Representation == TcoPlacementRepresentation.RuntimeRc
            && decision.Eligibility.OwnershipShapeEligible
            && decision.Eligibility.ResolvedLayoutEligible);
        TcoFunctionPlacementSnapshot snapshot = lowering.GetTcoPlacementSnapshots()
            .Single(candidate =>
                string.Equals(candidate.FunctionName, "loop", StringComparison.Ordinal));
        snapshot.FunctionLabel.ShouldNotBeNullOrWhiteSpace();
        snapshot.Parameters.Select(trace => trace.Current.ParameterOrdinal)
            .ShouldBe(snapshot.Parameters.Select(trace => trace.Current.ParameterOrdinal).Order());
    }

    [Test]
    public void Pass_through_parameter_is_promoted_by_post_body_type_resolution()
    {
        const string source =
            """
            let recursive loop n acc =
                if n > 0
                then loop(n - 1)(acc)
                else Ashes.Text.byteLength(acc)

            Ashes.IO.print(Ashes.Text.fromInt(loop(4)("done")))
            """;

        Lowering lowering = LowerProgram(source);
        TcoParamPlacementTrace placement = GetPlacement(lowering, "loop", "acc");

        placement.Current.Representation.ShouldBe(TcoPlacementRepresentation.RuntimeRc);
        placement.Current.FirstPromotedAt.ShouldBe(TcoPlacementResolutionPoint.PostBodyRefresh);
        placement.History.ShouldContain(decision =>
            decision.ResolutionPoint == TcoPlacementResolutionPoint.ProvisionalLoopEntry
            && decision.Eligibility.Reason == TcoRcEligibilityReason.UnresolvedType);
        placement.History.ShouldContain(decision =>
            decision.ResolutionPoint == TcoPlacementResolutionPoint.PostBodyRefresh
            && decision.Transition == TcoPlacementTransitionKind.PromotedAfterResolution);
    }

    [Test]
    public void Grounded_tail_argument_promotes_parameter_at_resolved_back_edge()
    {
        const string source =
            """
            let recursive loop n acc =
                if n > 0
                then loop(n - 1)(acc + "x")
                else Ashes.Text.byteLength(acc)

            Ashes.IO.print(Ashes.Text.fromInt(loop(4)("")))
            """;

        Lowering lowering = LowerProgram(source);
        TcoParamPlacementTrace placement = GetPlacement(lowering, "loop", "acc");

        placement.Current.Representation.ShouldBe(TcoPlacementRepresentation.RuntimeRc);
        placement.Current.FirstPromotedAt.ShouldBe(TcoPlacementResolutionPoint.ResolvedBackEdge);
        placement.History.ShouldContain(decision =>
            decision.ResolutionPoint == TcoPlacementResolutionPoint.ResolvedBackEdge
            && decision.Transition == TcoPlacementTransitionKind.PromotedAfterResolution
            && decision.ResolvedType == "Str");
    }

    [Test]
    public void Fresh_closure_parameter_is_promoted_after_post_body_type_resolution()
    {
        const string source =
            """
            let recursive loop n text transform =
                if n <= 0
                then transform(text)
                else loop(n - 1)(text + "x")(given value -> value + Ashes.Text.fromInt(n))

            Ashes.IO.print(loop(4)("")(given value -> value))
            """;

        Lowering lowering = LowerProgram(source);
        TcoParamPlacementTrace placement = GetPlacement(lowering, "loop", "transform");

        placement.Current.Representation.ShouldBe(TcoPlacementRepresentation.RuntimeRc);
        placement.Current.Eligibility.Reason.ShouldBe(TcoRcEligibilityReason.FreshClosureRebuild);
        placement.Current.FirstPromotedAt.ShouldBe(TcoPlacementResolutionPoint.PostBodyRefresh);
        placement.History.ShouldContain(decision =>
            decision.ResolutionPoint == TcoPlacementResolutionPoint.ProvisionalLoopEntry
            && decision.Eligibility.Reason == TcoRcEligibilityReason.UnresolvedType);
        placement.History.ShouldContain(decision =>
            decision.ResolutionPoint == TcoPlacementResolutionPoint.PostBodyRefresh
            && decision.Transition == TcoPlacementTransitionKind.PromotedAfterResolution);
    }

    [Test]
    public void Dynamic_capability_boundary_records_a_stable_arena_reason()
    {
        const string source =
            """
            capability Log =
                | log : Str -> Unit

            let recursive loop : Int -> Str -> Str = given n -> given acc ->
                if n <= 0
                then acc
                else loop(n - 1)(acc + "x")

            let runLogged =
                given (u) ->
                    handle Log.log("hi") with
                        | Log.log(msg) -> resume(Unit)

            let runLoopFromPost =
                given (u) ->
                    handle Log.log("hi") with
                        | Log.log(msg) ->
                            let _ = resume(Unit) in
                            let _ = loop(4)("") in
                            Unit

            let _ = runLogged(Unit) in
            let _ = runLoopFromPost(Unit) in
            Ashes.IO.print(loop(4)(""))
            """;

        Lowering lowering = LowerProgram(source);
        TcoParamPlacementTrace placement = GetPlacement(lowering, "loop", "acc");

        placement.Current.Representation.ShouldBe(TcoPlacementRepresentation.Arena);
        placement.Current.Reason.ShouldBe(TcoPlacementReason.DynamicCapabilityBoundary);
        placement.Current.DynamicBoundaryRestricted.ShouldBeTrue();
        placement.History.ShouldAllBe(decision =>
            decision.Representation == TcoPlacementRepresentation.Arena);
    }

    [Test]
    public void Unresolved_post_body_refresh_preserves_resolved_edge_runtime_type()
    {
        const string source =
            """
            type Body =
                | x: Float
                | velocity: Float

            let recursive makeBodies count =
                if count == 0
                then []
                else Body(x = 0.0, velocity = 2.0) :: makeBodies(count - 1)

            let recursive moveBodies dt bodies =
                match bodies with
                    | [] -> []
                    | body :: rest ->
                        match body with
                            | Body(x, velocity) -> Body(x = x + dt * velocity, velocity = velocity) :: moveBodies(dt)(rest)

            let advance dt _ = moveBodies(dt)(makeBodies(3))

            let recursive run turns bodies =
                if turns == 0
                then bodies
                else run(turns - 1)(advance(1.0)(bodies))

            run(10)([])
            """;

        Lowering lowering = LowerProgram(source);
        TcoParamPlacementTrace placement = GetPlacement(lowering, "run", "bodies");

        placement.Current.ResolutionPoint.ShouldBe(TcoPlacementResolutionPoint.PostBodyRefresh);
        placement.Current.Representation.ShouldBe(TcoPlacementRepresentation.RuntimeRc);
        placement.Current.Reason.ShouldBe(TcoPlacementReason.EarlierPlacementRetained);
        placement.Current.ResolvedType.ShouldBe("List<Body>");
        placement.Current.FirstPromotedAt.ShouldBe(TcoPlacementResolutionPoint.ResolvedBackEdge);
    }

    [Test]
    public void Shadowed_duplicate_parameter_records_its_decisive_arena_reason()
    {
        const string source =
            """
            let recursive build value value remaining =
                if remaining <= 0
                then value
                else build(["x"])(value)(remaining - 1)

            build([])([])(2)
            """;

        Lowering lowering = LowerProgram(source);
        TcoFunctionPlacementSnapshot snapshot = lowering.GetTcoPlacementSnapshots()
            .Single(candidate =>
                string.Equals(candidate.FunctionName, "build", StringComparison.Ordinal));
        TcoParamPlacementTrace shadowed = snapshot.Parameters.Single(trace =>
            trace.Current.ParameterOrdinal == 0);
        TcoParamPlacementTrace visible = snapshot.Parameters.Single(trace =>
            trace.Current.ParameterOrdinal == 1);

        shadowed.Current.ParameterName.ShouldBe("value");
        visible.Current.ParameterName.ShouldBe("value");
        shadowed.Current.Slot.ShouldNotBe(visible.Current.Slot);
        shadowed.Current.Representation.ShouldBe(TcoPlacementRepresentation.Arena);
        shadowed.Current.Reason.ShouldBe(TcoPlacementReason.ShadowedBinding);
        shadowed.History.ShouldAllBe(decision =>
            decision.Representation == TcoPlacementRepresentation.Arena
            && decision.Reason == TcoPlacementReason.ShadowedBinding);
    }

    [Test]
    public void Consumed_list_tail_alongside_a_self_recursive_multi_constructor_tree_is_not_profitable()
    {
        // Shape of challenges/1brc/brc.ash's mergeEntries: a list walked one cons cell at a time
        // (entries) alongside a self-recursive, multi-constructor accumulator (acc: Tree) that is
        // rebuilt fresh every iteration by a helper call and can never itself be RC-eligible.
        const string source =
            """
            type Tree =
                | Leaf
                | Node(Tree, Int, Tree)

            let recursive insert n acc =
                match acc with
                    | Leaf -> Node(Leaf)(n)(Leaf)
                    | Node(l, v, r) ->
                        if n < v
                        then Node(insert(n)(l))(v)(r)
                        else Node(l)(v)(insert(n)(r))

            let recursive mergeEntries entries acc =
                match entries with
                    | [] -> acc
                    | (key, value) :: tail -> mergeEntries(tail)(insert(value + Ashes.Text.byteLength(key))(acc))

            let recursive buildEntries n acc =
                if n <= 0
                then acc
                else buildEntries(n - 1)((Ashes.Text.fromInt(n), n) :: acc)

            let result = mergeEntries(buildEntries(50)([]))(Leaf)

            let recursive depth acc =
                match acc with
                    | Leaf -> 0
                    | Node(l, _, r) -> 1 + depth(l)

            Ashes.IO.print(Ashes.Text.fromInt(depth(result)))
            """;

        var lowering = LowerProgram(source);
        var verdicts = lowering.GetTcoPromotionProfitability("mergeEntries");

        verdicts.ShouldNotBeNull();
        verdicts.ShouldContainKey("entries");
        verdicts["entries"].Verdict.ShouldBe(TcoPromotionVerdict.NotProfitable);
        TcoParamPlacementTrace placement = GetPlacement(lowering, "mergeEntries", "entries");
        placement.Current.Representation.ShouldBe(TcoPlacementRepresentation.Arena);
        placement.Current.Reason.ShouldBe(TcoPlacementReason.BlockingSiblingNotProfitable);
        placement.Current.BlockingSiblingOrdinal.ShouldBe(1);
    }

    [Test]
    public void Affine_string_accumulator_alongside_a_re_passed_unchanged_list_is_not_profitable()
    {
        // Shape of challenges/reverse-complement/reverse-complement.ash's emit: an affine-growth
        // string accumulator (buf, unconditionally RC-eligible by type) alongside a list (chars)
        // that is re-passed UNCHANGED at one of its own two tail-call sites (the column-wrap branch),
        // which disqualifies it from the consumed-tail exemption entirely, leaving it permanently
        // arena-only.
        const string source =
            """
            let recursive emit chars col buf =
                match chars with
                    | [] -> Ashes.Text.byteLength(buf)
                    | c :: rest ->
                        if col == 60
                        then emit(chars)(0)("")
                        else emit(rest)(col + 1)(buf + c)

            let recursive buildChars n acc =
                if n <= 0
                then acc
                else buildChars(n - 1)(Ashes.Text.fromInt(n) :: acc)

            let result = emit(buildChars(200)([]))(0)("")

            Ashes.IO.print(Ashes.Text.fromInt(result))
            """;

        var lowering = LowerProgram(source);
        var verdicts = lowering.GetTcoPromotionProfitability("emit");

        verdicts.ShouldNotBeNull();
        verdicts.ShouldContainKey("buf");
        verdicts["buf"].Verdict.ShouldBe(TcoPromotionVerdict.NotProfitable);
    }

    [Test]
    public void Affine_string_accumulator_alongside_a_sometimes_fresh_closure_is_profitable()
    {
        // The original synthetic adversarial case: a Str accumulator (unconditionally RC-eligible)
        // threaded alongside a closure that is a fresh lambda on some tail self-calls and threaded
        // unchanged on others -- the shape that made the all-or-nothing veto wipe the Str
        // accumulator's own eligibility purely because of the co-resident closure's unrelated
        // failure to be independently RC-eligible. A closure sibling must never count as
        // "permanently blocking" the way a self-recursive ADT or a non-arena-resettable list does:
        // a non-escaping closure is stack-allocated, not arena/RC at all.
        const string source =
            """
            let recursive loop n str fn =
                if n <= 0
                then str + Ashes.Text.fromInt(fn(n))
                else
                    if n % 2 == 0
                    then loop(n - 1)(str + "x")(given y -> y + 1)
                    else loop(n - 1)(str + "y")(fn)

            let result = loop(10)("")(given y -> y)

            Ashes.IO.print(result)
            """;

        var lowering = LowerProgram(source);
        var verdicts = lowering.GetTcoPromotionProfitability("loop");

        verdicts.ShouldNotBeNull();
        verdicts.ShouldContainKey("str");
        verdicts["str"].Verdict.ShouldBe(TcoPromotionVerdict.Profitable);
    }

    [Test]
    public void Single_heap_parameter_loop_is_unaffected_by_the_signal()
    {
        // fannkuch-redux's own loop/nextPerm shape: the sole heap-typed TCO parameter in the frame.
        // With no sibling to interact with, this signal must never mark it NotProfitable -- the
        // veto's own multi-parameter logic (and this signal's) never had anything to do here either
        // way, so this is a sanity check that the new machinery does not touch what it shouldn't.
        const string source =
            """
            type State =
                | S(List(Int), Int)

            let recursive loop n st =
                match st with
                    | S(perm, count) ->
                        if n <= 0
                        then count
                        else loop(n - 1)(S(perm)(count + 1))

            let result = loop(10)(S([1, 2, 3])(0))

            Ashes.IO.print(Ashes.Text.fromInt(result))
            """;

        var lowering = LowerProgram(source);
        var verdicts = lowering.GetTcoPromotionProfitability("loop");

        if (verdicts is not null && verdicts.TryGetValue("st", out var verdict))
        {
            verdict.Verdict.ShouldBe(TcoPromotionVerdict.Profitable);
        }
    }
}
