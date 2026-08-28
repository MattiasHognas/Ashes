using Ashes.Frontend;
using Ashes.Semantics;
using Shouldly;

namespace Ashes.Tests;

public sealed class ReuseTokenTests
{
    [Test]
    public void Tail_self_call_precedes_reuse_specialization_and_remains_a_loop_back_edge()
    {
        IrProgram program = LowerProgramWithImports("""
            import Ashes.IO as io
            import Ashes.Text as text

            let recursive bumpAll values =
                match values with
                    | [] -> []
                    | value :: rest -> value + 1 :: bumpAll(rest)

            let recursive repeat turns values =
                if turns == 0
                then values
                else repeat(turns - 1)(bumpAll(values))

            let recursive sum values total =
                match values with
                    | [] -> total
                    | value :: rest -> sum(rest)(total + value)

            io.print(text.fromInt(sum(repeat(10)([1, 2, 3]))(0)))
            """);

        IrFunction loopBody = program.Functions.Single(function =>
            function.Instructions.Any(instruction =>
                instruction is IrInst.Label label
                && label.Name.EndsWith("_body", StringComparison.Ordinal))
            && string.Equals(
                function.Origin?.Source?.SourceName,
                "repeat",
                StringComparison.Ordinal)
            && function.Origin?.Kind == IrFunctionOriginKind.ClosureHelper);
        string bodyLabel = loopBody.Instructions
            .OfType<IrInst.Label>()
            .Single(label => label.Name.EndsWith("_body", StringComparison.Ordinal))
            .Name;

        loopBody.Instructions.Any(instruction =>
            instruction is IrInst.Jump jump
            && string.Equals(jump.Target, bodyLabel, StringComparison.Ordinal)).ShouldBeTrue();
        program.Functions.Any(function =>
            function.Origin is { Kind: IrFunctionOriginKind.ReuseSpecialization }
            && string.Equals(
                function.Origin.Source?.SourceName,
                "repeat",
                StringComparison.Ordinal)).ShouldBeFalse(
                    "a tail self-call must not recursively specialize its own function");
    }

    [Test]
    public void Fresh_record_list_result_is_rewritten_in_place_before_escape()
    {
        IrProgram program = LowerProgram("""
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
                            | Body(x, velocity) ->
                                Body(x = x + dt * velocity, velocity = velocity)
                                    :: moveBodies(dt)(rest)

            moveBodies(1.0)(makeBodies(3))
            """);

        program.Functions.ShouldContain(function =>
            function.Label.StartsWith("moveBodies__reuse", StringComparison.Ordinal));
        IrFunction reuseSpecialization = program.Functions.Single(function =>
            function.Origin?.Kind == IrFunctionOriginKind.ReuseSpecialization);
        IrFunctionOrigin reuseOrigin = reuseSpecialization.Origin
            ?? throw new InvalidOperationException("Missing reuse origin.");
        SourceFunctionOrigin sourceOrigin = reuseOrigin.Source
            ?? throw new InvalidOperationException("Missing reuse source origin.");
        sourceOrigin.SourceName.ShouldBe("moveBodies");
        reuseOrigin.ParentGeneratedLabel.ShouldNotBeNull();
        string discriminator = reuseOrigin.StableDiscriminator
            ?? throw new InvalidOperationException("Missing reuse discriminator.");
        discriminator.ShouldContain("moveBodies|");

        IrFunction specialization = program.Functions.Single(function =>
            function.Instructions.Count(instruction => instruction is IrInst.AllocReusing) == 2
                && function.Instructions.Any(instruction =>
                    instruction is IrInst.AllocReusing { ListCell: true }));
        IrInst.AllocReusing[] allocations = specialization.Instructions
            .OfType<IrInst.AllocReusing>()
            .ToArray();

        allocations.Length.ShouldBe(2);
        allocations.Count(allocation => allocation.ListCell).ShouldBe(1);
        allocations.Count(allocation => !allocation.ListCell && allocation.FieldCount == 2).ShouldBe(1);
        allocations.ShouldAllBe(allocation => !allocation.RuntimeManaged);
        specialization.Instructions.ShouldNotContain(instruction => instruction is IrInst.AllocAdtToSpace);
        specialization.Instructions.ShouldNotContain(instruction => instruction is IrInst.AllocAdt);
    }

    [Test]
    public void Function_parameter_shadow_does_not_inherit_global_fresh_result_reuse()
    {
        IrProgram program = LowerProgram("""
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
                            | Body(x, velocity) ->
                                Body(x = x + dt * velocity, velocity = velocity)
                                    :: moveBodies(dt)(rest)

            let apply makeBodies = moveBodies(1.0)(makeBodies(3))

            apply(given count -> [])
            """);

        program.Functions.ShouldNotContain(function =>
            function.Label.StartsWith("moveBodies__reuse", StringComparison.Ordinal));
    }

    [Test]
    public void Mutual_recursion_wrapper_keeps_source_ownership_for_fresh_result_reuse()
    {
        IrProgram program = LowerProgram("""
            type Body =
                | x: Float
                | velocity: Float

            let recursive makeA count =
                if count == 0
                then []
                else makeB(count - 1)
            and makeB count =
                if count == 0
                then []
                else makeA(count - 1)

            let recursive moveBodies dt bodies =
                match bodies with
                    | [] -> []
                    | body :: rest ->
                        match body with
                            | Body(x, velocity) ->
                                Body(x = x + dt * velocity, velocity = velocity)
                                    :: moveBodies(dt)(rest)

            moveBodies(1.0)(makeA(3))
            """);

        program.Functions.ShouldContain(function =>
            function.Label.StartsWith("__recgroup_dispatch", StringComparison.Ordinal));
        program.Functions.ShouldContain(function =>
            function.Label.StartsWith("moveBodies__reuse", StringComparison.Ordinal));
    }

    [Test]
    public void Recursive_group_member_label_keeps_source_ownership_for_fresh_result_reuse()
    {
        IrProgram program = LowerProgram("""
            type Body =
                | x: Float
                | velocity: Float

            let recursive makeBodies count =
                if count == 0
                then []
                else Body(x = 0.0, velocity = 2.0) :: makeBodies(count - 1)
            and identity value = value

            let recursive moveBodies dt bodies =
                match bodies with
                    | [] -> []
                    | body :: rest ->
                        match body with
                            | Body(x, velocity) ->
                                Body(x = x + dt * velocity, velocity = velocity)
                                    :: moveBodies(dt)(rest)

            moveBodies(1.0)(makeBodies(3))
            """);

        program.Functions.ShouldNotContain(function =>
            function.Label.StartsWith("__recgroup_dispatch", StringComparison.Ordinal));
        program.Functions.ShouldContain(function =>
            function.Label.StartsWith("moveBodies__reuse", StringComparison.Ordinal));
    }

    // Previously asserted that moveBodies got list-reuse-specialized inside run's own TCO loop
    // function, once advance (a fresh-result helper) spliced into the loop's back-edge argument.
    // That relied on ExpandFreshInlinableCaptures unconditionally pre-populating a splice target's
    // OWN transitive callees (moveBodies, makeBodies) into every lambda that merely calls it,
    // regardless of whether that particular call site ever splices — which is also what let an
    // ordinary (never-inlined) caller like HashMap.get capture its callee's callee (Ord's
    // `compareComposite` capturing its own `strCompare`) for no reason, doubling a 48-byte
    // dictionary bundle into every such closure. ExpandFreshInlinableCaptures is now gated to the
    // two contexts (_inSpecialization, a live reuse token) where a splice is actually imminent;
    // recovering the TCO-back-edge case too needs predicting, before the loop's own captures are
    // computed, which argument will splice — a prediction that changed which of the lowering's two
    // passes ends up owning the final capture set (see git history for the abandoned attempt), an
    // effect with no connection to this test's own call shape. moveBodies still runs correctly here,
    // just through an ordinary closure call instead of a list-reusing specialization.
    [Test]
    public void Helper_referencing_reuse_specializable_function_falls_back_to_ordinary_call_outside_reuse_context()
    {
        IrProgram program = LowerProgram("""
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
                            | Body(x, velocity) ->
                                Body(x = x + dt * velocity, velocity = velocity)
                                    :: moveBodies(dt)(rest)

            let advance dt bodies = moveBodies(dt)(makeBodies(3))

            let recursive run turns bodies =
                if turns == 0
                then bodies
                else run(turns - 1)(advance(1.0)(bodies))

            run(10)([])
            """);

        program.Functions
            .Where(function => function.LocalNames?.Values.Contains("turns", StringComparer.Ordinal) == true)
            .Any(function => function.Instructions.Any(instruction =>
                instruction is IrInst.MakeClosure { FuncLabel: var label }
                    && label.StartsWith("moveBodies__reuse", StringComparison.Ordinal)))
            .ShouldBeFalse();

        program.Functions.ShouldContain(function =>
            function.Label.StartsWith("moveBodies__reuse", StringComparison.Ordinal));
    }

    [Test]
    public void Recursive_list_rewriter_specialization_reuses_untagged_cells()
    {
        IrProgram program = LowerProgram("""
            let recursive bumpAll values =
                match values with
                    | [] -> []
                    | value :: rest -> value + 1 :: bumpAll(rest)

            let recursive repeat turns values =
                if turns == 0
                then values
                else repeat(turns - 1)(bumpAll(values))

            repeat(100)([1, 2, 3])
            """);

        IrFunction specialization = program.Functions.Single(function =>
            function.Label.StartsWith("bumpAll__reuse", StringComparison.Ordinal));
        IrInst.DropReuse[] tokens = specialization.Instructions
            .OfType<IrInst.DropReuse>()
            .Where(token => !token.RuntimeManaged)
            .ToArray();
        IrInst.AllocReusing[] allocations = specialization.Instructions
            .OfType<IrInst.AllocReusing>()
            .Where(allocation => allocation.ListCell)
            .ToArray();

        tokens.Length.ShouldBe(1);
        allocations.Length.ShouldBe(1);
        allocations[0].RuntimeManaged.ShouldBeFalse();
        allocations[0].TokenTemp.ShouldBe(tokens[0].Target);
    }

    [Test]
    public void Exhaustive_copy_adt_rebuild_uses_runtime_reuse_tokens()
    {
        IrProgram program = LowerProgram("""
            type Choice =
                | Left(Int)
                | Right(Int)

            let choice = Left(42)
            match choice with
                | Left(value) -> Right(value + 1)
                | Right(value) -> Left(value - 1)
            """);

        IrInst.DropReuse[] tokens = program.EntryFunction.Instructions
            .OfType<IrInst.DropReuse>()
            .Where(token => token.RuntimeManaged)
            .ToArray();
        IrInst.AllocReusing[] allocations = program.EntryFunction.Instructions
            .OfType<IrInst.AllocReusing>()
            .Where(allocation => allocation.RuntimeManaged)
            .ToArray();

        tokens.Length.ShouldBe(2);
        allocations.Length.ShouldBe(2);
        foreach (IrInst.AllocReusing allocation in allocations)
        {
            IrInst.DropReuse token = tokens.Single(candidate => candidate.Target == allocation.TokenTemp);
            token.FieldCount.ShouldBe(allocation.FieldCount);
        }
        program.EntryFunction.Instructions.Any(instruction =>
            instruction is IrInst.RcDrop { TypeName: "Choice", RuntimeManaged: true }).ShouldBeFalse();
    }

    [Test]
    public void Runtime_reuse_releases_token_when_rebuilt_constructor_has_incompatible_layout()
    {
        IrProgram program = LowerProgram("""
            type Choice =
                | Empty
                | One(Int)

            let choice = One(1)
            match choice with
                | Empty -> Empty
                | One(_) -> Empty
            """);

        program.EntryFunction.Instructions.Count(instruction =>
            instruction is IrInst.DropReuse { RuntimeManaged: true }).ShouldBe(2);
        program.EntryFunction.Instructions.Count(instruction =>
            instruction is IrInst.AllocReusing { RuntimeManaged: true }).ShouldBe(1);
        program.EntryFunction.Instructions.Count(instruction =>
            instruction is IrInst.AllocAdt { RuntimeManaged: true }).ShouldBe(2);
        program.EntryFunction.Instructions.Count(instruction =>
            instruction is IrInst.RcDrop { TypeName: "Choice", RuntimeManaged: true }).ShouldBe(1);
    }

    [Test]
    public void Runtime_token_skips_same_sized_arena_managed_constructor()
    {
        IrProgram program = LowerProgram("""
            type Choice =
                | Left(Int)
                | Right(Int)

            type Box =
                | Box(String)

            let choice = Left(42)
            match choice with
                | Left(value) -> let box = Box("left") in Right(value)
                | Right(value) -> let box = Box("right") in Left(value)
            """);

        program.EntryFunction.Instructions.Count(instruction =>
            instruction is IrInst.DropReuse { RuntimeManaged: true }).ShouldBe(2);
        program.EntryFunction.Instructions.Count(instruction =>
            instruction is IrInst.AllocReusing { RuntimeManaged: true }).ShouldBe(2);
        program.EntryFunction.Instructions.Count(instruction =>
            instruction is IrInst.AllocAdt { RuntimeManaged: false }).ShouldBeGreaterThanOrEqualTo(2);
    }

    [Test]
    public void Recursive_adt_reuse_releases_old_children_before_overwrite()
    {
        IrProgram program = LowerProgram("""
            type Tree =
                | Leaf
                | Node(Tree, Int, Tree)

            let tree = Node(Leaf)(42)(Leaf)
            match tree with
                | Leaf -> Leaf
                | Node(_, value, _) -> Node(Leaf)(value + 1)(Leaf)
            """);

        program.EntryFunction.Instructions.Count(instruction =>
            instruction is IrInst.DropReuse { RuntimeManaged: true }).ShouldBe(2);
        program.EntryFunction.Instructions.Count(instruction =>
            instruction is IrInst.AllocReusing { RuntimeManaged: true }).ShouldBe(2);
        program.EntryFunction.Instructions.Count(instruction =>
            instruction is IrInst.CallKnown { FuncLabel: var label }
                && label.StartsWith("__rcdrop_", StringComparison.Ordinal)).ShouldBeGreaterThanOrEqualTo(2);
    }

    [Test]
    public void Recursive_adt_reuse_transfers_child_with_null_fallback_dup()
    {
        IrProgram program = LowerProgram("""
            type Tree =
                | Leaf
                | Node(Tree, Int, Tree)

            let tree = Node(Leaf)(42)(Leaf)
            match tree with
                | Leaf -> Leaf
                | Node(left, value, _) -> Node(left)(value + 1)(Leaf)
            """);

        program.EntryFunction.Instructions.Count(instruction =>
            instruction is IrInst.DropReuse { RuntimeManaged: true }).ShouldBe(2);
        program.EntryFunction.Instructions.Count(instruction =>
            instruction is IrInst.AllocReusing { RuntimeManaged: true }).ShouldBe(2);
        program.EntryFunction.Instructions.Any(instruction =>
            instruction is IrInst.RcDup { RuntimeManaged: true }).ShouldBeTrue();
    }

    [Test]
    public void Recursive_adt_reuse_declines_when_transferred_child_has_another_use()
    {
        IrProgram program = LowerProgram("""
            type Tree =
                | Leaf
                | Node(Tree, Int, Tree)

            let tree = Node(Leaf)(42)(Leaf)
            match tree with
                | Leaf -> Leaf
                | Node(left, value, _) ->
                    let bonus = match left with
                        | Leaf -> 0
                        | Node(_, childValue, _) -> childValue
                    in Node(left)(value + bonus)(Leaf)
            """);

        program.EntryFunction.Instructions.Any(instruction =>
            instruction is IrInst.DropReuse { RuntimeManaged: true }).ShouldBeFalse();
        program.EntryFunction.Instructions.Any(instruction =>
            instruction is IrInst.AllocReusing { RuntimeManaged: true }).ShouldBeFalse();
    }

    [Test]
    public void Nested_record_reuse_releases_old_child_before_overwrite()
    {
        IrProgram program = LowerProgram("""
            type Leaf =
                | value: Int

            type Node =
                | child: Leaf
                | bonus: Int

            let node = Node(child = Leaf(value = 40), bonus = 2)
            match node with
                | Node(child, bonus) -> Node(child = Leaf(value = bonus), bonus = bonus + 1)
            """);

        program.EntryFunction.Instructions.Count(instruction =>
            instruction is IrInst.DropReuse { RuntimeManaged: true }).ShouldBe(1);
        program.EntryFunction.Instructions.Count(instruction =>
            instruction is IrInst.AllocReusing { RuntimeManaged: true }).ShouldBe(1);
        program.EntryFunction.Instructions.Any(instruction =>
            instruction is IrInst.RcDrop { TypeName: "Leaf", RuntimeManaged: true }).ShouldBeTrue();
    }

    [Test]
    public void Nested_record_reuse_transfers_child_with_null_fallback_dup()
    {
        IrProgram program = LowerProgram("""
            type Leaf =
                | value: Int

            type Node =
                | child: Leaf
                | bonus: Int

            let node = Node(child = Leaf(value = 40), bonus = 2)
            match node with
                | Node(child, bonus) -> Node(child = child, bonus = bonus + 1)
            """);

        program.EntryFunction.Instructions.Count(instruction =>
            instruction is IrInst.DropReuse { RuntimeManaged: true }).ShouldBe(1);
        program.EntryFunction.Instructions.Count(instruction =>
            instruction is IrInst.AllocReusing { RuntimeManaged: true }).ShouldBe(1);
        program.EntryFunction.Instructions.Any(instruction =>
            instruction is IrInst.RcDup { RuntimeManaged: true }).ShouldBeTrue();
    }

    [Test]
    public void Nested_record_reuse_declines_when_transferred_child_has_another_use()
    {
        IrProgram program = LowerProgram("""
            type Leaf =
                | value: Int

            type Node =
                | child: Leaf
                | bonus: Int

            let node = Node(child = Leaf(value = 40), bonus = 2)
            match node with
                | Node(child, bonus) ->
                    let childValue = match child with
                        | Leaf(value) -> value
                    in Node(child = child, bonus = bonus + childValue)
            """);

        program.EntryFunction.Instructions.Any(instruction =>
            instruction is IrInst.DropReuse { RuntimeManaged: true }).ShouldBeFalse();
        program.EntryFunction.Instructions.Any(instruction =>
            instruction is IrInst.AllocReusing { RuntimeManaged: true }).ShouldBeFalse();
    }

    [Test]
    public void Pointer_variant_reuse_releases_old_record_child_before_overwrite()
    {
        IrProgram program = LowerProgram("""
            type Leaf =
                | value: Int

            type Choice =
                | Empty
                | Full(Leaf, Int)

            let choice = Full(Leaf(value = 40))(2)
            match choice with
                | Empty -> Empty
                | Full(_, bonus) -> Full(Leaf(value = bonus))(bonus + 1)
            """);

        program.EntryFunction.Instructions.Count(instruction =>
            instruction is IrInst.DropReuse { RuntimeManaged: true }).ShouldBe(2);
        program.EntryFunction.Instructions.Count(instruction =>
            instruction is IrInst.AllocReusing { RuntimeManaged: true }).ShouldBe(2);
        program.EntryFunction.Instructions.Any(instruction =>
            instruction is IrInst.RcDrop { TypeName: "Leaf", RuntimeManaged: true }).ShouldBeTrue();
    }

    [Test]
    public void Pointer_variant_reuse_transfers_record_child_with_null_fallback_dup()
    {
        IrProgram program = LowerProgram("""
            type Leaf =
                | value: Int

            type Choice =
                | Empty
                | Full(Leaf, Int)

            let choice = Full(Leaf(value = 40))(2)
            match choice with
                | Empty -> Empty
                | Full(child, bonus) -> Full(child)(bonus + 1)
            """);

        program.EntryFunction.Instructions.Count(instruction =>
            instruction is IrInst.DropReuse { RuntimeManaged: true }).ShouldBe(2);
        program.EntryFunction.Instructions.Count(instruction =>
            instruction is IrInst.AllocReusing { RuntimeManaged: true }).ShouldBe(2);
        program.EntryFunction.Instructions.Any(instruction =>
            instruction is IrInst.RcDup { RuntimeManaged: true }).ShouldBeTrue();
    }

    [Test]
    public void Pointer_variant_reuse_declines_when_transferred_child_has_another_use()
    {
        IrProgram program = LowerProgram("""
            type Leaf =
                | value: Int

            type Choice =
                | Empty
                | Full(Leaf, Int)

            let choice = Full(Leaf(value = 40))(2)
            match choice with
                | Empty -> Empty
                | Full(child, bonus) ->
                    let childValue = match child with
                        | Leaf(value) -> value
                    in Full(child)(bonus + childValue)
            """);

        program.EntryFunction.Instructions.Any(instruction =>
            instruction is IrInst.DropReuse { RuntimeManaged: true }).ShouldBeFalse();
        program.EntryFunction.Instructions.Any(instruction =>
            instruction is IrInst.AllocReusing { RuntimeManaged: true }).ShouldBeFalse();
    }

    [Test]
    public void Runtime_reuse_declines_when_tail_arm_would_leave_token_unconsumed()
    {
        IrProgram program = LowerProgram("""
            type Choice =
                | Left(Int)
                | Right(Int)

            let recursive loop n =
                if n <= 0 then 0
                else
                    let choice = Left(n) in
                    match choice with
                        | Left(value) -> loop(value - 1)
                        | Right(value) -> loop(value - 1)

            Ashes.IO.print(loop(3))
            """);

        IrFunction loop = program.Functions.Single(function => function.Instructions.Any(instruction =>
            instruction is IrInst.AllocAdt { RuntimeManaged: true }));
        loop.Instructions.Any(instruction =>
            instruction is IrInst.DropReuse { RuntimeManaged: true }).ShouldBeFalse();
    }

    [Test]
    public void Recursive_adt_accumulator_routes_alloc_reusing_through_drop_reuse()
    {
        IrProgram program = LowerProgram("""
            type Tree =
                | Leaf
                | Node(Tree, Int, Tree)

            let recursive loop n tree =
                if n <= 0 then tree
                else
                    match tree with
                        | Leaf -> loop(n - 1)(Node(Leaf)(n)(Leaf))
                        | Node(left, value, right) -> loop(n - 1)(Node(left)(value + n)(right))

            let result = loop(3)(Node(Leaf)(5)(Leaf))
            match result with
                | Leaf -> Ashes.IO.print(0)
                | Node(_, value, _) -> Ashes.IO.print(value)
            """);

        int reusingAllocations = 0;
        foreach (IrFunction function in program.Functions.Prepend(program.EntryFunction))
        {
            Dictionary<int, (IrInst.DropReuse Token, int Index)> tokens = function.Instructions
                .Select((instruction, index) => (instruction, index))
                .Where(pair => pair.instruction is IrInst.DropReuse)
                .ToDictionary(
                    pair => ((IrInst.DropReuse)pair.instruction).Target,
                    pair => (((IrInst.DropReuse)pair.instruction), pair.index));

            foreach ((IrInst instruction, int index) in function.Instructions.Select((instruction, index) => (instruction, index)))
            {
                if (instruction is not IrInst.AllocReusing allocation)
                {
                    continue;
                }

                tokens.TryGetValue(allocation.TokenTemp, out (IrInst.DropReuse Token, int Index) definition)
                    .ShouldBeTrue($"AllocReusing token %{allocation.TokenTemp} must be defined by DropReuse");
                definition.Index.ShouldBeLessThan(index);
                definition.Token.FieldCount.ShouldBe(allocation.FieldCount);
                definition.Token.RuntimeManaged.ShouldBeFalse();
                reusingAllocations++;
            }
        }

        reusingAllocations.ShouldBeGreaterThan(0);
    }

    [Test]
    public void Inspect_only_record_list_traversal_borrows_instead_of_normalizing()
    {
        // A tail-recursive reduction that only reads inline-copy fields of an all-scalar record
        // element and returns a scalar never retains a cons cell or head, so the caller's graph is
        // borrowed: no defensive per-entry RC normalization (CopyOutArena) and no runtime-managed
        // list cell allocation. (Pointer-bearing analogue of the inline-element borrowed cursor.)
        IrProgram program = LowerProgram("""
            type Body =
                | x: Int
                | y: Int
                | mass: Int

            let recursive sumField bodies acc =
                match bodies with
                    | [] -> acc
                    | b :: rest ->
                        match b with
                            | Body(x, y, mass) -> sumField(rest)(acc + x * mass + y)

            Ashes.IO.print(sumField([Body(x = 1, y = 2, mass = 3)])(0))
            """);

        foreach (IrFunction function in program.Functions.Prepend(program.EntryFunction))
        {
            function.Instructions.Any(instruction =>
                instruction is IrInst.CopyOutArena { Purpose: IrInst.CopyOutPurpose.RcNormalization })
                .ShouldBeFalse("an inspect-only record-list traversal must borrow its argument, not RC-normalize it");
            function.Instructions.Any(instruction =>
                instruction is IrInst.Alloc { RuntimeManaged: true })
                .ShouldBeFalse("borrowing the caller's list allocates no runtime-managed cons cell");
        }
    }

    [Test]
    public void Disjoint_same_named_pattern_binding_does_not_block_borrowed_traversal()
    {
        IrProgram program = LowerProgram("""
            type Body =
                | value: Int

            let recursive sum values fallback total =
                match values with
                    | [] -> total
                    | body :: tail ->
                        match body with
                            | Body(value) ->
                                if value > 0
                                then sum(tail)(fallback)(total + value)
                                else
                                    match fallback with
                                        | [] -> total
                                        | tail :: _ ->
                                            match tail with
                                                | Body(other) -> total + other

            Ashes.IO.print(sum([Body(value = 1)])([Body(value = 2)])(0))
            """);

        foreach (IrFunction function in program.Functions.Prepend(program.EntryFunction))
        {
            function.Instructions.Any(instruction =>
                instruction is IrInst.CopyOutArena { Purpose: IrInst.CopyOutPurpose.RcNormalization })
                .ShouldBeFalse(
                    "an unrelated same-named pattern binder must not inherit the consumed tail's owner");
            function.Instructions.Any(instruction =>
                instruction is IrInst.Alloc { RuntimeManaged: true })
                .ShouldBeFalse(
                    "the identity-correct inspect-only traversal should keep borrowing the caller graph");
        }
    }

    [Test]
    public void Record_list_traversal_that_hands_tail_to_a_provably_inspect_only_function_borrows()
    {
        // The tail escapes into a second function (not the tail self-call) — but that function
        // (countRest) only counts: it never stores, returns, or captures the list. Its own body is
        // itself proven inspect-only in its one parameter, so the whole-program fixpoint approves the
        // hand-off and the defensive RC normalization is elided. See the companion test below for the
        // same shape with a callee that genuinely retains its argument, which must still normalize.
        // The check is scoped to walk's own functions: the top-level program scope can independently
        // emit an unrelated CopyOutArena of its own (preserving the trailing Unit result across a
        // stack-closure resource cleanup), which is not walk's traversal decision under test here.
        IrProgram program = LowerProgram("""
            type Body =
                | x: Int
                | mass: Int

            let recursive countRest rest =
                match rest with
                    | [] -> 0
                    | _ :: more -> 1 + countRest(more)

            let recursive walk bodies acc =
                match bodies with
                    | [] -> acc
                    | b :: rest ->
                        match b with
                            | Body(x, mass) -> walk(rest)(acc + x * mass + countRest(rest))

            Ashes.IO.print(walk([Body(x = 1, mass = 2)])(0))
            """);

        program.Functions
            .Where(function => string.Equals(
                function.Origin?.Source?.SourceName,
                "walk",
                StringComparison.Ordinal))
            .Any(function => function.Instructions.Any(
                instruction => instruction is IrInst.CopyOutArena { Purpose: IrInst.CopyOutPurpose.RcNormalization }))
            .ShouldBeFalse("a hand-off to a provably inspect-only callee must borrow, not RC-normalize");
    }

    [Test]
    public void Record_list_traversal_that_hands_tail_to_a_retaining_function_still_normalizes()
    {
        // passThrough returns its argument directly — a genuinely unsafe hand-off, unlike the
        // inspect-only countRest above. Nothing proves the caller's list outlives the call without a
        // retained alias, so the borrow must still be declined and RC normalization retained.
        IrProgram program = LowerProgram("""
            type Body =
                | x: Int
                | mass: Int

            let passThrough values = values

            let recursive walk bodies acc =
                match bodies with
                    | [] -> acc
                    | b :: rest ->
                        match b with
                            | Body(x, mass) ->
                                match passThrough(rest) with
                                    | [] -> walk(rest)(acc + x * mass)
                                    | _ :: _ -> walk(rest)(acc + x * mass + 1)

            Ashes.IO.print(walk([Body(x = 1, mass = 2)])(0))
            """);

        program.Functions.Prepend(program.EntryFunction)
            .Any(function => function.Instructions.Any(
                instruction => instruction is IrInst.CopyOutArena { Purpose: IrInst.CopyOutPurpose.RcNormalization }))
            .ShouldBeTrue("a hand-off to a callee that retains its argument must keep RC normalization");
    }

    [Test]
    public void Record_list_traversal_that_hands_tail_to_guard_function_still_normalizes()
    {
        // A nested constructor pattern directly in the cons head (Body(x, mass) :: rest) is a
        // separate, pre-existing limitation of the pattern-binding walk (it only re-taints a cons
        // head/tail bound to a plain variable or wildcard, not one destructured inline) — this keeps
        // normalizing regardless of the guard hand-off itself. See the companion test below, which
        // isolates the guard hand-off with a two-step match and shows it borrows on its own.
        IrProgram program = LowerProgram("""
            type Body =
                | x: Int
                | mass: Int

            let hasAny values =
                match values with
                    | [] -> false
                    | _ :: _ -> true

            let recursive walk bodies acc =
                match bodies with
                    | [] -> acc
                    | Body(x, mass) :: rest when hasAny(rest) ->
                        walk(rest)(acc + x * mass)
                    | Body(x, mass) :: rest ->
                        walk(rest)(acc + x * mass)

            Ashes.IO.print(walk([Body(x = 1, mass = 2)])(0))
            """);

        program.Functions.Prepend(program.EntryFunction)
            .Any(function => function.Instructions.Any(
                instruction => instruction is IrInst.CopyOutArena { Purpose: IrInst.CopyOutPurpose.RcNormalization }))
            .ShouldBeTrue("a traversal whose tail escapes through a guard must keep RC normalization");
    }

    [Test]
    public void Record_list_traversal_that_hands_tail_to_a_provably_inspect_only_guard_function_borrows()
    {
        // Same guard hand-off as the companion test above, but with the cons head bound to a plain
        // variable and destructured in a separate nested match — avoiding the pattern-binding
        // limitation that blocks that test, so this isolates the guard hand-off itself: hasAny only
        // inspects its argument, so the whole-program fixpoint approves the hand-off through the
        // guard clause and the traversal borrows.
        // The check is scoped to walk's own functions, for the same reason as the companion test
        // above: hasAny's own closure is stack-allocated here (it is used only as a direct callee),
        // which makes the top-level program scope emit its own unrelated result-preserving
        // CopyOutArena across that resource's cleanup — not walk's traversal decision under test.
        IrProgram program = LowerProgram("""
            type Body =
                | x: Int
                | mass: Int

            let hasAny values =
                match values with
                    | [] -> false
                    | _ :: _ -> true

            let recursive walk bodies acc =
                match bodies with
                    | [] -> acc
                    | b :: rest ->
                        match b with
                            | Body(x, mass) when hasAny(rest) -> walk(rest)(acc + x * mass + 1)
                            | Body(x, mass) -> walk(rest)(acc + x * mass)

            Ashes.IO.print(walk([Body(x = 1, mass = 2)])(0))
            """);

        program.Functions
            .Where(function => string.Equals(
                function.Origin?.Source?.SourceName,
                "walk",
                StringComparison.Ordinal))
            .Any(function => function.Instructions.Any(
                instruction => instruction is IrInst.CopyOutArena { Purpose: IrInst.CopyOutPurpose.RcNormalization }))
            .ShouldBeFalse("a hand-off through a guard to a provably inspect-only callee must borrow");
    }

    private static IrProgram LowerProgram(string source)
    {
        Diagnostics diagnostics = new();
        Program program = new Parser(source, diagnostics).ParseProgram();
        diagnostics.ThrowIfAny();
        IrProgram ir = new Lowering(diagnostics).Lower(program);
        diagnostics.ThrowIfAny();
        return ir;
    }

    private static IrProgram LowerProgramWithImports(string source)
    {
        ParsedImportHeader parsed = ProjectSupport.ParseImportHeader(source, "<memory>");
        CombinedCompilationLayout layout = ProjectSupport.BuildStandaloneCompilationLayout(
            parsed.SourceWithoutImports,
            parsed.ImportNames);
        HashSet<string> importedStandardModules = parsed.ImportNames
            .Where(ProjectSupport.IsStdModule)
            .ToHashSet(StringComparer.Ordinal);
        Diagnostics diagnostics = new();
        Program program = new Parser(layout.Source, diagnostics).ParseProgram();
        diagnostics.ThrowIfAny();
        IrProgram ir = new Lowering(
            diagnostics,
            importedStandardModules,
            parsed.ImportAliases.Count == 0 ? null : parsed.ImportAliases).Lower(program);
        diagnostics.ThrowIfAny();
        return ir;
    }
}
