using Ashes.Frontend;
using Ashes.Semantics;
using Shouldly;

namespace Ashes.Tests;

public sealed class ReuseTokenTests
{
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

    private static IrProgram LowerProgram(string source)
    {
        Diagnostics diagnostics = new();
        Program program = new Parser(source, diagnostics).ParseProgram();
        diagnostics.ThrowIfAny();
        IrProgram ir = new Lowering(diagnostics).Lower(program);
        diagnostics.ThrowIfAny();
        return ir;
    }
}
