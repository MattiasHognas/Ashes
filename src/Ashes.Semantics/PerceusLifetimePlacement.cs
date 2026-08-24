namespace Ashes.Semantics;

/// <summary>
/// An instruction list whose ordinary-value lifetime markers have been placed, together with the
/// temp count after the placement's own dup temps.
/// </summary>
/// <param name="Instructions">The instruction list with placed lifetime markers.</param>
/// <param name="TempCount">The temp count including temps the placement introduced.</param>
internal sealed record LifetimePlacementResult(List<IrInst> Instructions, int TempCount);

/// <summary>
/// Moves erased ordinary-value lifetime markers from lexical scope exits to control-flow precise
/// last-use points. Resource cleanup is deliberately outside this pass.
/// </summary>
internal static class PerceusLifetimePlacement
{
    public static IrProgram Place(IrProgram program, IReadOnlySet<IrInst.CallClosure>? borrowedArgumentCalls = null)
    {
        IrFunction entry = Place(program.EntryFunction, borrowedArgumentCalls);
        var functions = new List<IrFunction>(program.Functions.Count);
        foreach (IrFunction function in program.Functions)
        {
            functions.Add(Place(function, borrowedArgumentCalls));
        }

        return program with { EntryFunction = entry, Functions = functions };
    }

    public static IrFunction Place(IrFunction function, IReadOnlySet<IrInst.CallClosure>? borrowedArgumentCalls = null)
    {
        // A coroutine's markers are placed on its linear pre-transform body, where the await
        // boundary is still an ordinary CFG edge. Its split state-dispatch form is not a second
        // placement candidate.
        if (function.LifetimesPlaced)
        {
            return function;
        }

        LifetimePlacementResult placed = Place([.. function.Instructions], function.TempCount, function.Label, borrowedArgumentCalls);
        return function with
        {
            Instructions = placed.Instructions,
            TempCount = placed.TempCount,
            LifetimesPlaced = true,
        };
    }

    /// <summary>
    /// Places lifetime markers on a linear instruction list. A coroutine body uses this before its
    /// state-machine split, so the placement sees the <see cref="IrInst.AwaitTask"/> boundary as an
    /// ordinary control-flow edge rather than a suspend that returns to its caller.
    /// </summary>
    public static LifetimePlacementResult Place(
        List<IrInst> body,
        int bodyTempCount,
        string functionLabel,
        IReadOnlySet<IrInst.CallClosure>? borrowedArgumentCalls = null)
    {
        List<IrInst> instructions = [.. body];
        int tempCount = bodyTempCount;
        int[] ownerSlots = instructions
            .OfType<IrInst.RcDrop>()
            .Where(drop => drop.OwnerSlot >= 0)
            .Select(drop => drop.OwnerSlot)
            .Distinct()
            .ToArray();
        IReadOnlyList<HashSet<int>>? dominators = null;
        var usedTempsByInstruction = new Dictionary<IrInst, int[]>(
            ReferenceEqualityComparer.Instance);

        foreach (int ownerSlot in ownerSlots)
        {
            var anchors = instructions
                .Select((instruction, index) => (instruction, index))
                .Where(pair => pair.instruction is IrInst.RcDrop { OwnerSlot: var slot } && slot == ownerSlot)
                .ToArray();
            if (anchors.Length != 1 || anchors[0].instruction is not IrInst.RcDrop anchor)
            {
                continue;
            }

            PlaceOwner(
                instructions, ownerSlot, anchor, anchors[0].index,
                ref tempCount, ref dominators, usedTempsByInstruction,
                functionLabel, borrowedArgumentCalls);
        }

        return new LifetimePlacementResult(instructions, tempCount);
    }

    private static void PlaceOwner(
        List<IrInst> instructions,
        int ownerSlot,
        IrInst.RcDrop anchor,
        int anchorIndex,
        ref int tempCount,
        ref IReadOnlyList<HashSet<int>>? dominators,
        Dictionary<IrInst, int[]> usedTempsByInstruction,
        string functionLabel,
        IReadOnlySet<IrInst.CallClosure>? borrowedArgumentCalls)
    {
        if (!TryRemoveLexicalAnchor(instructions, ownerSlot, anchor, anchorIndex, out OwnerRegion owner))
        {
            return;
        }

        List<Block> blocks = BuildBlocks(instructions);
        int definitionBlock = FindBlock(blocks, owner.DefinitionIndex);
        int boundaryBlock = FindBlock(blocks, Math.Min(owner.BoundaryIndex, Math.Max(0, instructions.Count - 1)));
        if (definitionBlock < 0 || boundaryBlock < 0)
        {
            return;
        }

        dominators ??= ComputeDominators(blocks);
        HashSet<int> region = ReachableBeforeBoundary(
            blocks, dominators, definitionBlock, boundaryBlock);
        if (region.Count == 0)
        {
            return;
        }

        HashSet<int> ownerAliases = CollectOwnerAliases(instructions, blocks, region, ownerSlot);
        foreach (int blockIndex in region)
        {
            Block block = blocks[blockIndex];
            block.OwnerLoads = FindOwnerLoads(instructions, block, ownerSlot);
            block.OwnerUses = FindOwnerUses(
                instructions,
                block,
                ownerSlot,
                ownerAliases,
                usedTempsByInstruction);
            block.HasUse = block.OwnerUses.Count > 0;
        }

        ComputeLiveness(blocks, region);
        Dictionary<int, List<IrInst>> insertions = CollectInsertions(
            instructions, blocks, region, definitionBlock, owner, anchor, borrowedArgumentCalls, ref tempCount);

        foreach ((int index, List<IrInst> added) in insertions.OrderByDescending(pair => pair.Key))
        {
            instructions.InsertRange(index, added);
        }
    }

    private static bool TryRemoveLexicalAnchor(
        List<IrInst> instructions,
        int ownerSlot,
        IrInst.RcDrop anchor,
        int anchorIndex,
        out OwnerRegion owner)
    {
        int definitionIndex = instructions.FindIndex(instruction => instruction is IrInst.StoreLocal { Slot: var slot } && slot == ownerSlot);
        if (definitionIndex < 0 || instructions[definitionIndex] is not IrInst.StoreLocal definition)
        {
            owner = null!;
            return false;
        }

        instructions.RemoveAt(anchorIndex);
        int boundaryIndex = anchorIndex;
        if (anchorIndex > 0
            && instructions[anchorIndex - 1] is IrInst.LoadLocal load
            && load.Slot == ownerSlot
            && load.Target == anchor.SourceTemp)
        {
            instructions.RemoveAt(anchorIndex - 1);
            boundaryIndex--;
        }

        owner = new OwnerRegion(definitionIndex, boundaryIndex, definition.Source);
        return true;
    }

    private static void ComputeLiveness(List<Block> blocks, HashSet<int> region)
    {
        bool changed;
        do
        {
            changed = false;
            foreach (int blockIndex in region.OrderByDescending(index => index))
            {
                Block block = blocks[blockIndex];
                bool liveOut = block.Successors.Any(successor => region.Contains(successor) && blocks[successor].LiveIn);
                bool liveIn = block.HasUse || liveOut;
                if (block.LiveOut != liveOut || block.LiveIn != liveIn)
                {
                    block.LiveOut = liveOut;
                    block.LiveIn = liveIn;
                    changed = true;
                }
            }
        }
        while (changed);
    }

    private static Dictionary<int, List<IrInst>> CollectInsertions(
        List<IrInst> instructions,
        List<Block> blocks,
        HashSet<int> region,
        int definitionBlock,
        OwnerRegion owner,
        IrInst.RcDrop anchor,
        IReadOnlySet<IrInst.CallClosure>? borrowedArgumentCalls,
        ref int tempCount)
    {
        var insertions = new Dictionary<int, List<IrInst>>();
        foreach (int blockIndex in region)
        {
            Block block = blocks[blockIndex];
            IrInst.RcDrop placedDrop = anchor with { SourceTemp = owner.DefinitionTemp };
            if (block.HasUse && !block.LiveOut)
            {
                int lastUse = block.OwnerUses[^1];
                AddInsertion(insertions, LifetimeInsertionIndex(instructions, lastUse), placedDrop);
            }
            else if (!block.LiveIn && blockIndex == definitionBlock)
            {
                AddInsertion(insertions, owner.DefinitionIndex + 1, placedDrop);
            }
            else if (!block.LiveIn && HasLiveBranchPredecessor(blocks, region, blockIndex))
            {
                int entryIndex = block.Start < instructions.Count && instructions[block.Start] is IrInst.Label
                    ? block.Start + 1
                    : block.Start;
                AddInsertion(insertions, entryIndex, placedDrop);
            }

            AddCallDups(instructions, block, anchor.RuntimeManaged, borrowedArgumentCalls, ref tempCount, insertions);
        }

        return insertions;
    }

    private static int LifetimeInsertionIndex(List<IrInst> instructions, int lastUse)
    {
        int insertionIndex = lastUse + 1;
        if (IsArenaCopyOut(instructions[lastUse])
            && insertionIndex < instructions.Count
            && instructions[insertionIndex] is IrInst.ReclaimArenaChunks)
        {
            insertionIndex++;
        }

        return insertionIndex;
    }

    private static bool IsArenaCopyOut(IrInst instruction)
        => instruction is IrInst.CopyOutArena
            or IrInst.CopyOutArenaToSpace
            or IrInst.CopyOutList
            or IrInst.CopyOutClosure;

    private static void AddCallDups(
        List<IrInst> instructions,
        Block block,
        bool runtimeManaged,
        IReadOnlySet<IrInst.CallClosure>? borrowedArgumentCalls,
        ref int tempCount,
        Dictionary<int, List<IrInst>> insertions)
    {
        for (int loadOrdinal = 0; loadOrdinal < block.OwnerLoads.Count; loadOrdinal++)
        {
            int loadIndex = block.OwnerLoads[loadOrdinal];
            int sourceTemp = ((IrInst.LoadLocal)instructions[loadIndex]).Target;
            var aliases = new HashSet<int> { sourceTemp };
            for (int i = loadIndex + 1; i < block.End; i++)
            {
                if (instructions[i] is IrInst.Borrow borrow && aliases.Contains(borrow.SourceTemp))
                {
                    aliases.Add(borrow.Target);
                    continue;
                }

                if (instructions[i] is IrInst.CallClosure call
                    && aliases.Contains(call.ArgTemp)
                    && (borrowedArgumentCalls is null || !borrowedArgumentCalls.Contains(call))
                    && (loadOrdinal + 1 < block.OwnerLoads.Count || block.LiveOut))
                {
                    AddInsertion(insertions, i, new IrInst.RcDup(tempCount++, call.ArgTemp, runtimeManaged) { Location = call.Location });
                    break;
                }
            }
        }
    }

    private static HashSet<int> CollectAppliedClosureTemps(List<IrInst> instructions, List<Block> blocks, HashSet<int> region)
    {
        var applied = new HashSet<int>();
        foreach (int blockIndex in region)
        {
            Block block = blocks[blockIndex];
            for (int i = block.Start; i < block.End; i++)
            {
                if (instructions[i] is IrInst.CallClosure call)
                {
                    applied.Add(call.ClosureTemp);
                }
            }
        }

        return applied;
    }

    private static HashSet<int> CollectOwnerAliases(
        List<IrInst> instructions,
        List<Block> blocks,
        HashSet<int> region,
        int ownerSlot)
    {
        var aliases = new HashSet<int>();
        foreach (int blockIndex in region)
        {
            foreach (int loadIndex in FindOwnerLoads(instructions, blocks[blockIndex], ownerSlot))
            {
                aliases.Add(((IrInst.LoadLocal)instructions[loadIndex]).Target);
            }
        }

        // Follow aliases to a fixpoint through Borrow; a local slot that only holds an alias
        // (StoreLocal then LoadLocal — the conditional runtime-argument retain routes a borrowed owner
        // through a fresh slot); a closure env that captured an alias (StoreMemOffset of an alias then
        // MakeClosure over that env); and a PARTIAL application (CallClosure(f, alias) whose result is
        // itself applied again, so the returned closure captured the alias). A transient closure holds
        // the borrow in its arena/stack env until applied, so the owner must stay live until that
        // application — else its drop lands right after the capture, a use-after-free (benign for a
        // recycled small string, a segfault for an OS-backed >4 KiB string). All only lengthen
        // liveness, so the drop lands after the last real use, never earlier.
        HashSet<int> partialApplicationResults = CollectAppliedClosureTemps(instructions, blocks, region);
        var aliasHoldingSlots = new HashSet<int>();
        var aliasHoldingEnvs = new HashSet<int>();
        bool changed = true;
        while (changed)
        {
            changed = false;
            foreach (int blockIndex in region.OrderBy(index => blocks[index].Start))
            {
                Block block = blocks[blockIndex];
                for (int i = block.Start; i < block.End; i++)
                {
                    switch (instructions[i])
                    {
                        case IrInst.Borrow borrow when aliases.Contains(borrow.SourceTemp):
                            changed |= aliases.Add(borrow.Target);
                            break;
                        case IrInst.StoreLocal store when aliases.Contains(store.Source):
                            changed |= aliasHoldingSlots.Add(store.Slot);
                            break;
                        case IrInst.LoadLocal load when aliasHoldingSlots.Contains(load.Slot):
                            changed |= aliases.Add(load.Target);
                            break;
                        case IrInst.StoreMemOffset envStore when aliases.Contains(envStore.Source):
                            changed |= aliasHoldingEnvs.Add(envStore.BasePtr);
                            break;
                        case IrInst.MakeClosure mc when aliasHoldingEnvs.Contains(mc.EnvPtrTemp):
                            changed |= aliases.Add(mc.Target);
                            break;
                        case IrInst.MakeClosureStack mcs when aliasHoldingEnvs.Contains(mcs.EnvPtrTemp):
                            changed |= aliases.Add(mcs.Target);
                            break;
                        case IrInst.CallClosure partialCall when aliases.Contains(partialCall.ArgTemp) && partialApplicationResults.Contains(partialCall.Target):
                            changed |= aliases.Add(partialCall.Target);
                            break;
                    }
                }
            }
        }

        return aliases;
    }

    private static List<int> FindOwnerUses(
        List<IrInst> instructions,
        Block block,
        int ownerSlot,
        HashSet<int> aliases,
        Dictionary<IrInst, int[]> usedTempsByInstruction)
    {
        var uses = new List<int>();
        for (int i = block.Start; i < block.End; i++)
        {
            if (instructions[i] is IrInst.LoadLocal { Slot: var slot } && slot == ownerSlot)
            {
                uses.Add(i);
                continue;
            }

            int[] usedTemps = GetUsedTemps(
                instructions[i],
                usedTempsByInstruction);
            if (usedTemps.Any(aliases.Contains))
            {
                uses.Add(i);
            }
        }

        return uses;
    }

    private static int[] GetUsedTemps(
        IrInst instruction,
        Dictionary<IrInst, int[]> usedTempsByInstruction)
    {
        if (usedTempsByInstruction.TryGetValue(instruction, out int[]? usedTemps))
        {
            return usedTemps;
        }

        HashSet<int> collected = [];
        IrOptimizer.CollectUsedTemps(instruction, collected);
        usedTemps = [.. collected];
        usedTempsByInstruction[instruction] = usedTemps;
        return usedTemps;
    }

    private static List<int> FindOwnerLoads(List<IrInst> instructions, Block block, int ownerSlot)
    {
        var loads = new List<int>();
        for (int i = block.Start; i < block.End; i++)
        {
            if (instructions[i] is IrInst.LoadLocal { Slot: var slot } && slot == ownerSlot)
            {
                loads.Add(i);
            }
        }

        return loads;
    }

    private static bool HasLiveBranchPredecessor(List<Block> blocks, HashSet<int> region, int blockIndex)
    {
        foreach (int predecessor in blocks[blockIndex].Predecessors)
        {
            if (region.Contains(predecessor)
                && blocks[predecessor].Successors.Count > 1
                && blocks[predecessor].LiveOut)
            {
                return true;
            }
        }

        return false;
    }

    private static void AddInsertion(Dictionary<int, List<IrInst>> insertions, int index, IrInst instruction)
    {
        if (!insertions.TryGetValue(index, out List<IrInst>? added))
        {
            added = [];
            insertions[index] = added;
        }

        added.Add(instruction);
    }

    private static HashSet<int> ReachableBeforeBoundary(
        List<Block> blocks,
        IReadOnlyList<HashSet<int>> dominators,
        int start,
        int boundary)
    {
        var reachable = new HashSet<int>();
        var pending = new Stack<int>();
        pending.Push(start);
        while (pending.Count > 0)
        {
            int current = pending.Pop();
            // Pattern-bound owners can be defined inside a recursive match arm. A back edge from
            // that arm reaches the loop header and sibling arms, but the owner does not exist on
            // paths entering those blocks from the function entry. Restrict placement to blocks
            // dominated by the definition so a drop never references the arm-local definition
            // temp on an unrelated path.
            if (current > boundary
                || !dominators[current].Contains(start)
                || !reachable.Add(current)
                || current == boundary)
            {
                continue;
            }

            foreach (int successor in blocks[current].Successors)
            {
                pending.Push(successor);
            }
        }

        return reachable;
    }

    // Block graph construction and dominators are shared infrastructure (IrControlFlowGraph) —
    // this pass builds its own Block wrapper only to carry its liveness-specific mutable state
    // (OwnerLoads/OwnerUses/HasUse/LiveIn/LiveOut) alongside the shared graph shape. Wrapping,
    // rather than reimplementing, keeps this pass's block graph and its Successors/Predecessors
    // edges byte-for-byte identical to what IrControlFlowGraph.Build produces.

    private static IReadOnlyList<HashSet<int>> ComputeDominators(List<Block> blocks)
        => IrControlFlowGraph.ComputeDominators(blocks);

    private static List<Block> BuildBlocks(List<IrInst> instructions)
        => [.. IrControlFlowGraph.Build(instructions).Select(b => new Block(b))];

    private static int FindBlock(List<Block> blocks, int instructionIndex)
        => blocks.FindIndex(block => instructionIndex >= block.Start && instructionIndex < block.End);

    private sealed class Block : IHasCfgEdges
    {
        public Block(IrCfgBlock cfgBlock)
        {
            Start = cfgBlock.Start;
            End = cfgBlock.End;
            Successors = cfgBlock.Successors;
            Predecessors = cfgBlock.Predecessors;
        }

        public int Start { get; }
        public int End { get; }
        public List<int> Successors { get; }
        public List<int> Predecessors { get; }
        public List<int> OwnerLoads { get; set; } = [];
        public List<int> OwnerUses { get; set; } = [];
        public bool HasUse { get; set; }
        public bool LiveIn { get; set; }
        public bool LiveOut { get; set; }
    }

    private sealed record OwnerRegion(int DefinitionIndex, int BoundaryIndex, int DefinitionTemp);
}
