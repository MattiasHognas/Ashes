using Ashes.Frontend;
using Ashes.Semantics;
using Shouldly;

namespace Ashes.Tests;

// A generic tree lookup's `| Leaf -> None` arm speculatively reuses the dead Leaf cell for the
// None (an AllocReusing over the scrutinee's token); a body without structural reuse then has
// that reuse reverted to a fresh arena AllocAdt (PrepareDirectReuseBody). The arm's ownership
// scope must not have reset the arena under that result on the strength of the speculative
// reuse: the fresh None is above the watermark, and a reset followed by ReclaimArenaChunks freed
// it whenever the allocation happened to open a new chunk, so the caller read its tag out of an
// unmapped page. The stage-1 compiler hit exactly this in Ashes.Collection.Map.getStr once its
// maps grew past a chunk.
public sealed class RevertedNullaryReuseArmResetTests
{
    private const string TreeLookupSource = """
        type Tree(v) =
            | Leaf
            | Node(Tree(v), Int, v, Tree(v))

        let recursive getTree key tree =
            match tree with
                | Leaf -> None
                | Node(left, nodeKey, nodeValue, right) ->
                    if key == nodeKey
                    then Some(nodeValue)
                    else
                        if key <= nodeKey
                        then getTree(key)(left)
                        else getTree(key)(right)

        let tree = Node(Leaf)(2)("two")(Node(Leaf)(5)("five")(Leaf))

        match getTree(7)(tree) with
            | None -> Ashes.IO.print("missing")
            | Some(found) -> Ashes.IO.print(found)
        """;

    [Test]
    public void A_reverted_nullary_reuse_result_is_not_returned_across_an_arena_reset()
    {
        var diag = new Diagnostics();
        var program = new Parser(TreeLookupSource, diag).ParseProgram();
        diag.ThrowIfAny();
        IrProgram ir = new Lowering(diag).Lower(program);
        diag.ThrowIfAny();

        ir.Functions.Append(ir.EntryFunction)
            .SelectMany(ArmResetsUnderFreshNullaryCell)
            .ShouldBeEmpty();
    }

    // Every match arm of `function` that allocates a nullary cell, stores it as the arm's result,
    // resets the arena without copying the result out, and jumps to the match end with it: the
    // arm then hands out a cell the reset just abandoned. A scope that copies its result out
    // before resetting (a let bracket around a constructed value) is the sound shape.
    private static IEnumerable<string> ArmResetsUnderFreshNullaryCell(IrFunction function)
    {
        List<IrInst> instructions = function.Instructions;
        for (int i = 0; i < instructions.Count; i++)
        {
            if (instructions[i] is not IrInst.AllocAdt { FieldCount: 0 } nullary)
            {
                continue;
            }

            bool stored = false;
            int resetAt = -1;
            bool copiedOut = false;
            for (int j = i + 1; j < instructions.Count; j++)
            {
                IrInst inst = instructions[j];
                if (inst is IrInst.Return or IrInst.JumpIfFalse)
                {
                    break;
                }

                if (inst is IrInst.Jump jump)
                {
                    if (stored && resetAt >= 0 && !copiedOut && jump.Target.StartsWith("match_end", StringComparison.Ordinal))
                    {
                        yield return $"{function.Label}: {nullary} at {i} reset at {resetAt} before {jump}";
                    }

                    break;
                }

                if (inst is IrInst.StoreLocal store && store.Source == nullary.Target)
                {
                    stored = true;
                }
                else if (stored && inst is IrInst.RestoreArenaState)
                {
                    resetAt = j;
                }
                else if (stored && inst is IrInst.CopyOutArena or IrInst.CopyOutList)
                {
                    copiedOut = true;
                }
            }
        }
    }
}
