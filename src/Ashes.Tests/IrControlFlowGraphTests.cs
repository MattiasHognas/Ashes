using Ashes.Semantics;
using Shouldly;

namespace Ashes.Tests;

public sealed class IrControlFlowGraphTests
{
    // HashSet<int>.ShouldBe(HashSet<int>) is order-sensitive (Shouldly compares as a sequence,
    // and HashSet<int> iteration order isn't guaranteed) — a set-equality helper avoids tests
    // that only pass by coincidental enumeration order for small integers.
    private static void AssertSet(HashSet<int> actual, params int[] expected)
        => actual.OrderBy(x => x).ShouldBe(expected.OrderBy(x => x));


    [Test]
    public void Straight_line_code_is_a_single_block_with_no_edges()
    {
        var instructions = new List<IrInst>
        {
            new IrInst.LoadConstInt(0, 1),
            new IrInst.LoadConstInt(1, 2),
            new IrInst.Return(0),
        };

        var blocks = IrControlFlowGraph.Build(instructions);

        blocks.Count.ShouldBe(1);
        blocks[0].Start.ShouldBe(0);
        blocks[0].End.ShouldBe(3);
        blocks[0].Successors.ShouldBeEmpty();
        blocks[0].Predecessors.ShouldBeEmpty();
    }

    [Test]
    public void If_else_diamond_has_the_expected_successor_and_predecessor_edges()
    {
        // 0: LoadConstBool          } block 0
        // 1: JumpIfFalse -> else
        // 2: LoadConstInt(1, 10)    } block 1 (then)
        // 3: Jump -> end
        // 4: else:                 } block 2 (else)
        // 5: LoadConstInt(2, 20)
        // 6: end:                  } block 3 (join)
        // 7: Return
        var instructions = new List<IrInst>
        {
            new IrInst.LoadConstBool(0, true),
            new IrInst.JumpIfFalse(0, "else"),
            new IrInst.LoadConstInt(1, 10),
            new IrInst.Jump("end"),
            new IrInst.Label("else"),
            new IrInst.LoadConstInt(2, 20),
            new IrInst.Label("end"),
            new IrInst.Return(1),
        };

        var blocks = IrControlFlowGraph.Build(instructions);

        blocks.Count.ShouldBe(4);
        blocks[0].Successors.OrderBy(x => x).ShouldBe([1, 2]); // then-block, else-block
        blocks[1].Successors.ShouldBe([3]); // then -> join
        blocks[2].Successors.ShouldBe([3]); // else -> join (fall-through)
        blocks[3].Successors.ShouldBeEmpty(); // join ends in Return

        blocks[1].Predecessors.ShouldBe([0]);
        blocks[2].Predecessors.ShouldBe([0]);
        blocks[3].Predecessors.OrderBy(x => x).ShouldBe([1, 2]);
    }

    [Test]
    public void Dominators_of_an_if_else_diamond_reflect_that_neither_arm_dominates_the_join()
    {
        var instructions = new List<IrInst>
        {
            new IrInst.LoadConstBool(0, true),
            new IrInst.JumpIfFalse(0, "else"),
            new IrInst.LoadConstInt(1, 10),
            new IrInst.Jump("end"),
            new IrInst.Label("else"),
            new IrInst.LoadConstInt(2, 20),
            new IrInst.Label("end"),
            new IrInst.Return(1),
        };

        var blocks = IrControlFlowGraph.Build(instructions);
        var dominators = IrControlFlowGraph.ComputeDominators(blocks);

        AssertSet(dominators[0], 0);
        AssertSet(dominators[1], 0, 1);
        AssertSet(dominators[2], 0, 2);
        // The join (block 3) is reached from both arms, so only the shared entry block
        // dominates it — neither arm-specific block does.
        AssertSet(dominators[3], 0, 3);
    }

    [Test]
    public void Post_dominators_of_an_if_else_diamond_show_the_join_post_dominates_both_arms()
    {
        var instructions = new List<IrInst>
        {
            new IrInst.LoadConstBool(0, true),
            new IrInst.JumpIfFalse(0, "else"),
            new IrInst.LoadConstInt(1, 10),
            new IrInst.Jump("end"),
            new IrInst.Label("else"),
            new IrInst.LoadConstInt(2, 20),
            new IrInst.Label("end"),
            new IrInst.Return(1),
        };

        var blocks = IrControlFlowGraph.Build(instructions);
        var postDominators = IrControlFlowGraph.ComputePostDominators(blocks);

        // Every path out of the entry, the then-arm, and the else-arm passes through the
        // join block (3) before the function exits, so it post-dominates all three; the
        // arm-specific blocks do not post-dominate each other or the entry.
        AssertSet(postDominators[0], 0, 3);
        AssertSet(postDominators[1], 1, 3);
        AssertSet(postDominators[2], 2, 3);
        AssertSet(postDominators[3], 3);
    }

    [Test]
    public void Loop_back_edge_does_not_corrupt_dominators_of_either_branch_target()
    {
        // head: LoadConstBool; JumpIfFalse -> exit
        //       LoadConstInt; Jump -> head   (back edge)
        // exit: Return
        var instructions = new List<IrInst>
        {
            new IrInst.Label("head"),
            new IrInst.LoadConstBool(0, true),
            new IrInst.JumpIfFalse(0, "exit"),
            new IrInst.LoadConstInt(1, 1),
            new IrInst.Jump("head"),
            new IrInst.Label("exit"),
            new IrInst.Return(1),
        };

        var blocks = IrControlFlowGraph.Build(instructions);
        blocks.Count.ShouldBe(3);

        var dominators = IrControlFlowGraph.ComputeDominators(blocks);

        // The loop header (0) dominates both the body (1) and the exit (2); the back edge
        // from the body must not make the body appear to dominate the header, nor make the
        // body and exit dominate each other.
        AssertSet(dominators[0], 0);
        AssertSet(dominators[1], 0, 1);
        AssertSet(dominators[2], 0, 2);
    }

    [Test]
    public void Switch_links_every_case_and_the_default_as_successors_with_correct_predecessors()
    {
        var instructions = new List<IrInst>
        {
            new IrInst.LoadConstInt(0, 0),
            new IrInst.SwitchTag(0, [(0, "case_a"), (1, "case_b")], "default_0"),
            new IrInst.Label("case_a"),
            new IrInst.Return(0),
            new IrInst.Label("case_b"),
            new IrInst.Return(0),
            new IrInst.Label("default_0"),
            new IrInst.Return(0),
        };

        var blocks = IrControlFlowGraph.Build(instructions);

        blocks.Count.ShouldBe(4); // switch dispatch + 3 targets
        blocks[0].Successors.OrderBy(x => x).ShouldBe([1, 2, 3]);
        blocks[1].Predecessors.ShouldBe([0]);
        blocks[2].Predecessors.ShouldBe([0]);
        blocks[3].Predecessors.ShouldBe([0]);
    }

    [Test]
    public void FindBlock_locates_the_block_containing_an_instruction_index()
    {
        var instructions = new List<IrInst>
        {
            new IrInst.LoadConstBool(0, true),
            new IrInst.JumpIfFalse(0, "else"),
            new IrInst.LoadConstInt(1, 10),
            new IrInst.Jump("end"),
            new IrInst.Label("else"),
            new IrInst.LoadConstInt(2, 20),
            new IrInst.Label("end"),
            new IrInst.Return(1),
        };

        var blocks = IrControlFlowGraph.Build(instructions);

        IrControlFlowGraph.FindBlock(blocks, 0).ShouldBe(0);
        IrControlFlowGraph.FindBlock(blocks, 2).ShouldBe(1);
        IrControlFlowGraph.FindBlock(blocks, 5).ShouldBe(2);
        IrControlFlowGraph.FindBlock(blocks, 7).ShouldBe(3);
        IrControlFlowGraph.FindBlock(blocks, 100).ShouldBe(-1);
    }

    [Test]
    public void IndexLabels_maps_each_label_name_to_its_block_index()
    {
        var instructions = new List<IrInst>
        {
            new IrInst.LoadConstBool(0, true),
            new IrInst.JumpIfFalse(0, "else"),
            new IrInst.LoadConstInt(1, 10),
            new IrInst.Jump("end"),
            new IrInst.Label("else"),
            new IrInst.LoadConstInt(2, 20),
            new IrInst.Label("end"),
            new IrInst.Return(1),
        };

        var blocks = IrControlFlowGraph.Build(instructions);
        var labels = IrControlFlowGraph.IndexLabels(instructions, blocks);

        labels["else"].ShouldBe(2);
        labels["end"].ShouldBe(3);
        labels.ContainsKey("nonexistent").ShouldBeFalse();
    }
}
