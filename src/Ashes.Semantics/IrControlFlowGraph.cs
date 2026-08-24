namespace Ashes.Semantics;

/// <summary>
/// Exposes a basic block's control-flow edges (as indices into the owning block list), so the
/// dominator/post-dominator algorithms in <see cref="IrControlFlowGraph"/> can operate over any
/// block type that carries this shape — including <see cref="PerceusLifetimePlacement"/>'s own
/// block type, which augments <see cref="IrCfgBlock"/> with liveness-specific mutable state.
/// </summary>
internal interface IHasCfgEdges
{
    List<int> Successors { get; }
    List<int> Predecessors { get; }
}

/// <summary>
/// A basic block within an <see cref="IrControlFlowGraph"/>: an instruction-index range
/// [<see cref="Start"/>, <see cref="End"/>) that begins at a <see cref="IrInst.Label"/> or the
/// function entry and ends at (but excludes) the next block boundary, together with its
/// successor/predecessor block indices.
/// </summary>
internal sealed class IrCfgBlock(int start, int end) : IHasCfgEdges
{
    public int Start { get; } = start;
    public int End { get; } = end;
    public List<int> Successors { get; } = [];
    public List<int> Predecessors { get; } = [];
}

/// <summary>
/// A reusable control-flow-graph view over a flat, label/jump-based IR instruction list: basic
/// blocks split at <see cref="IrInst.Label"/> boundaries and after terminators
/// (<see cref="IrInst.Jump"/>/<see cref="IrInst.JumpIfFalse"/>/<see cref="IrInst.SwitchTag"/>/
/// <see cref="IrInst.Return"/>), with successor/predecessor edges for explicit branch targets and
/// fall-through, plus dominator and post-dominator computation on demand.
///
/// This is purely an analysis structure over the existing flat/label IR — it does not convert Ashes
/// IR itself into a block-structured or SSA representation (LLVM already builds real SSA from the
/// alloca-based lowering once IR reaches it). Before this type existed, the one real block/dominator
/// builder in the compiler was private to <see cref="PerceusLifetimePlacement"/>; every other pass
/// needing predecessor/successor reasoning approximated it with a weaker per-pass heuristic (see
/// OPT-001 through OPT-003's ad hoc `CountBranchRefsToLabels`-based predecessor counting). This type
/// is the shared foundation those heuristics can be rebased onto incrementally.
/// </summary>
internal static class IrControlFlowGraph
{
    /// <summary>
    /// Builds the block graph for <paramref name="instructions"/>: splits into basic blocks at every
    /// <see cref="IrInst.Label"/> and immediately after every terminator, then links each block's
    /// successors (explicit branch targets, plus fall-through for a non-terminator-ending block) and
    /// the corresponding reverse predecessor edges.
    /// </summary>
    public static List<IrCfgBlock> Build(List<IrInst> instructions)
    {
        var starts = new SortedSet<int> { 0 };
        for (int i = 0; i < instructions.Count; i++)
        {
            if (instructions[i] is IrInst.Label)
            {
                starts.Add(i);
            }

            if (IsTerminator(instructions[i]) && i + 1 < instructions.Count)
            {
                starts.Add(i + 1);
            }
        }

        int[] startArray = [.. starts];
        var blocks = new List<IrCfgBlock>(startArray.Length);
        for (int i = 0; i < startArray.Length; i++)
        {
            blocks.Add(new IrCfgBlock(startArray[i], i + 1 < startArray.Length ? startArray[i + 1] : instructions.Count));
        }

        var labels = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < blocks.Count; i++)
        {
            if (instructions[blocks[i].Start] is IrInst.Label label)
            {
                labels[label.Name] = i;
            }
        }

        for (int i = 0; i < blocks.Count; i++)
        {
            IrInst last = instructions[blocks[i].End - 1];
            switch (last)
            {
                case IrInst.Jump jump:
                    AddSuccessor(blocks, i, labels[jump.Target]);
                    break;
                case IrInst.JumpIfFalse jumpIfFalse:
                    AddSuccessor(blocks, i, labels[jumpIfFalse.Target]);
                    if (i + 1 < blocks.Count) AddSuccessor(blocks, i, i + 1);
                    break;
                case IrInst.SwitchTag switchTag:
                    foreach ((_, string label) in switchTag.Cases) AddSuccessor(blocks, i, labels[label]);
                    AddSuccessor(blocks, i, labels[switchTag.DefaultLabel]);
                    break;
                case IrInst.Return:
                    break;
                default:
                    if (i + 1 < blocks.Count) AddSuccessor(blocks, i, i + 1);
                    break;
            }
        }

        return blocks;
    }

    /// <summary>
    /// Maps each <see cref="IrInst.Label"/>'s name to the index of the block it starts, for
    /// looking up a label's block (and from there its predecessor/successor edges) by name.
    /// </summary>
    public static Dictionary<string, int> IndexLabels(List<IrInst> instructions, IReadOnlyList<IrCfgBlock> blocks)
    {
        var labels = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < blocks.Count; i++)
        {
            if (instructions[blocks[i].Start] is IrInst.Label label)
            {
                labels[label.Name] = i;
            }
        }

        return labels;
    }

    /// <summary>Finds the index of the block containing <paramref name="instructionIndex"/>, or -1.</summary>
    public static int FindBlock(IReadOnlyList<IrCfgBlock> blocks, int instructionIndex)
    {
        for (int i = 0; i < blocks.Count; i++)
        {
            if (instructionIndex >= blocks[i].Start && instructionIndex < blocks[i].End)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// Computes, for every block, the set of blocks that dominate it (every path from the entry
    /// block, index 0, to that block passes through each member of its set) — an iterative
    /// meet-over-predecessors fixpoint restricted to blocks reachable from the entry.
    /// </summary>
    public static IReadOnlyList<HashSet<int>> ComputeDominators<T>(IReadOnlyList<T> blocks) where T : IHasCfgEdges
        => ComputeDominatorsCore(
            i => blocks[i].Successors,
            i => blocks[i].Predecessors,
            root: 0,
            count: blocks.Count);

    /// <summary>
    /// Computes, for every block, the set of blocks that post-dominate it (every path from that
    /// block to any function exit passes through each member of its set) — the same
    /// meet-over-predecessors fixpoint run over the reversed graph, rooted at a virtual exit node
    /// connected from every block with no real successors (e.g. a <see cref="IrInst.Return"/>).
    /// </summary>
    public static IReadOnlyList<HashSet<int>> ComputePostDominators<T>(IReadOnlyList<T> blocks) where T : IHasCfgEdges
    {
        int n = blocks.Count;
        int virtualExit = n;
        var reverseSuccessors = new List<int>[n + 1];
        var reversePredecessors = new List<int>[n + 1];
        for (int i = 0; i <= n; i++)
        {
            reverseSuccessors[i] = [];
            reversePredecessors[i] = [];
        }

        for (int i = 0; i < n; i++)
        {
            foreach (int predecessor in blocks[i].Predecessors)
            {
                // A forward edge predecessor -> i becomes the reverse edge i -> predecessor.
                reverseSuccessors[i].Add(predecessor);
                reversePredecessors[predecessor].Add(i);
            }

            if (blocks[i].Successors.Count == 0)
            {
                // A real exit block: forward edge i -> virtualExit becomes reverse edge virtualExit -> i.
                reverseSuccessors[virtualExit].Add(i);
                reversePredecessors[i].Add(virtualExit);
            }
        }

        IReadOnlyList<HashSet<int>> withVirtualExit = ComputeDominatorsCore(
            i => reverseSuccessors[i],
            i => reversePredecessors[i],
            root: virtualExit,
            count: n + 1);

        var result = new List<HashSet<int>>(n);
        for (int i = 0; i < n; i++)
        {
            var set = new HashSet<int>(withVirtualExit[i]);
            set.Remove(virtualExit);
            result.Add(set);
        }

        return result;
    }

    private static IReadOnlyList<HashSet<int>> ComputeDominatorsCore(
        Func<int, IReadOnlyList<int>> successorsOf,
        Func<int, IReadOnlyList<int>> predecessorsOf,
        int root,
        int count)
    {
        var reachable = new HashSet<int>();
        var pending = new Stack<int>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            int current = pending.Pop();
            if (!reachable.Add(current))
            {
                continue;
            }

            foreach (int successor in successorsOf(current))
            {
                pending.Push(successor);
            }
        }

        var dominators = new List<HashSet<int>>(count);
        for (int index = 0; index < count; index++)
        {
            dominators.Add(index == root
                ? [root]
                : reachable.Contains(index)
                    ? new HashSet<int>(reachable)
                    : [index]);
        }

        bool changed;
        do
        {
            changed = false;
            foreach (int blockIndex in reachable.Where(index => index != root).OrderBy(index => index))
            {
                int[] predecessors = [.. predecessorsOf(blockIndex).Where(reachable.Contains)];
                var next = predecessors.Length == 0
                    ? new HashSet<int>()
                    : new HashSet<int>(dominators[predecessors[0]]);
                foreach (int predecessor in predecessors.Skip(1))
                {
                    next.IntersectWith(dominators[predecessor]);
                }

                next.Add(blockIndex);
                if (!dominators[blockIndex].SetEquals(next))
                {
                    dominators[blockIndex] = next;
                    changed = true;
                }
            }
        }
        while (changed);

        return dominators;
    }

    private static void AddSuccessor(List<IrCfgBlock> blocks, int from, int to)
    {
        if (!blocks[from].Successors.Contains(to))
        {
            blocks[from].Successors.Add(to);
            blocks[to].Predecessors.Add(from);
        }
    }

    private static bool IsTerminator(IrInst instruction)
        => instruction is IrInst.Jump or IrInst.JumpIfFalse or IrInst.SwitchTag or IrInst.Return;
}
