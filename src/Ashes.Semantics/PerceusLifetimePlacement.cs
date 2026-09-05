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
        var retargets = new Dictionary<int, IrInst>();
        Dictionary<int, List<IrInst>> insertions = CollectInsertions(
            instructions, blocks, region, definitionBlock, owner, ownerSlot, anchor, functionLabel, borrowedArgumentCalls, retargets, ref tempCount);

        foreach ((int index, IrInst replacement) in retargets)
        {
            instructions[index] = replacement;
        }

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
        int ownerSlot,
        IrInst.RcDrop anchor,
        string functionLabel,
        IReadOnlySet<IrInst.CallClosure>? borrowedArgumentCalls,
        Dictionary<int, IrInst> retargets,
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
                if (AllPredecessorsLiveOut(blocks, region, blockIndex))
                {
                    int entryIndex = block.Start < instructions.Count && instructions[block.Start] is IrInst.Label
                        ? block.Start + 1
                        : block.Start;
                    AddInsertion(insertions, entryIndex, placedDrop);
                }
                else
                {
                    foreach (int predecessor in block.Predecessors)
                    {
                        if (IsLiveBranch(blocks, region, predecessor))
                        {
                            SplitLiveEdge(instructions, blocks, predecessor, blockIndex, placedDrop, functionLabel, ownerSlot, retargets, insertions);
                        }
                    }
                }
            }

            AddCallDups(instructions, block, anchor.RuntimeManaged, anchor.MayBeEmpty, borrowedArgumentCalls, ref tempCount, insertions);
        }

        return insertions;
    }

    // A join that some predecessor reaches without the owner (a branch whose own path already
    // released it, or a block outside the region) cannot drop at its entry: the drop would run
    // twice on that path. The live branch's edge gets its own block instead — its explicit
    // target rewritten to a fresh label whose block drops and jumps on to the join — and a
    // fallthrough edge drops right before the join's label, which only that predecessor reaches.
    private static void SplitLiveEdge(
        List<IrInst> instructions,
        List<Block> blocks,
        int predecessorIndex,
        int joinIndex,
        IrInst.RcDrop placedDrop,
        string functionLabel,
        int ownerSlot,
        Dictionary<int, IrInst> retargets,
        Dictionary<int, List<IrInst>> insertions)
    {
        Block predecessor = blocks[predecessorIndex];
        Block join = blocks[joinIndex];
        string? joinLabel = join.Start < instructions.Count && instructions[join.Start] is IrInst.Label label ? label.Name : null;
        if (joinLabel is null)
        {
            AddInsertion(insertions, join.Start, placedDrop);
            return;
        }

        string edgeLabel = $"{functionLabel}_rc_edge_{ownerSlot}_{predecessorIndex}";
        int terminatorIndex = predecessor.End - 1;
        bool retargeted = false;
        switch (instructions[terminatorIndex])
        {
            case IrInst.Jump jump when string.Equals(jump.Target, joinLabel, StringComparison.Ordinal):
                retargets[terminatorIndex] = jump with { Target = edgeLabel };
                retargeted = true;
                break;
            case IrInst.JumpIfFalse jumpIfFalse when string.Equals(jumpIfFalse.Target, joinLabel, StringComparison.Ordinal):
                retargets[terminatorIndex] = jumpIfFalse with { Target = edgeLabel };
                retargeted = true;
                break;
            case IrInst.SwitchTag switchTag when string.Equals(switchTag.DefaultLabel, joinLabel, StringComparison.Ordinal)
                || switchTag.Cases.Any(c => string.Equals(c.Label, joinLabel, StringComparison.Ordinal)):
                retargets[terminatorIndex] = switchTag with
                {
                    Cases = [.. switchTag.Cases.Select(c => string.Equals(c.Label, joinLabel, StringComparison.Ordinal) ? (c.Tag, edgeLabel) : c)],
                    DefaultLabel = string.Equals(switchTag.DefaultLabel, joinLabel, StringComparison.Ordinal) ? edgeLabel : switchTag.DefaultLabel,
                };
                retargeted = true;
                break;
        }

        if (retargeted)
        {
            AddInsertion(insertions, instructions.Count, new IrInst.Label(edgeLabel));
            AddInsertion(insertions, instructions.Count, placedDrop);
            AddInsertion(insertions, instructions.Count, new IrInst.Jump(joinLabel));
        }

        if (predecessorIndex + 1 == joinIndex && instructions[terminatorIndex] is not IrInst.Jump and not IrInst.SwitchTag and not IrInst.Return)
        {
            AddInsertion(insertions, join.Start, placedDrop);
        }
    }

    private static bool IsLiveBranch(List<Block> blocks, HashSet<int> region, int blockIndex)
        => region.Contains(blockIndex) && blocks[blockIndex].Successors.Count > 1 && blocks[blockIndex].LiveOut;

    private static bool AllPredecessorsLiveOut(List<Block> blocks, HashSet<int> region, int blockIndex)
        => blocks[blockIndex].Predecessors.All(predecessor => region.Contains(predecessor) && blocks[predecessor].LiveOut);

    private static int LifetimeInsertionIndex(List<IrInst> instructions, int lastUse)
    {
        // A returned alias (an escaping cell or call result that embeds the owner) is a use whose
        // "after" is unreachable; the drop goes before the return, where lowering's escape handling
        // has already retained or copied what the result keeps.
        if (instructions[lastUse] is IrInst.Return)
        {
            return lastUse;
        }

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
        bool mayBeEmpty,
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

                // A record-field store creates a new reference the field's cell owns outright,
                // while the owner's placed drop still releases the binding's own reference after
                // its last use. Without a compensating dup the two releases outnumber the two
                // references and the field is freed out from under the record.
                if (instructions[i] is IrInst.SetAdtField fieldStore && aliases.Contains(fieldStore.Source))
                {
                    AddInsertion(insertions, i, new IrInst.RcDup(tempCount++, fieldStore.Source, runtimeManaged, mayBeEmpty) { Location = fieldStore.Location });
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

        // Follow aliases to a fixpoint through Borrow; a local slot that holds an alias (StoreLocal
        // then a LoadLocal the store can reach — the conditional runtime-argument retain routes a
        // borrowed owner through a fresh slot); an arena cell that embeds an alias without a reference of its own
        // (StoreMemOffset of an alias into a list literal's cons cell, a tuple, or a closure env),
        // a closure made over such an env, and the result of a call that receives an alias as its
        // argument, closure, or environment. A transient closure holds the borrow in its arena/stack
        // env until applied, so the owner must stay live until that application; a callee's result
        // may carry the argument's pointer out (a curried stage's returned closure captured it, a
        // list-building loop consed a matched head into the list it returns), so the owner must stay
        // live until the result's last use — its copy-out past the call window reads the pointer.
        // Else the drop lands right after the capture or the call, a use-after-free (benign for a
        // recycled small string, a segfault for an OS-backed >4 KiB string). All only lengthen
        // liveness, so the drop lands after the last real use, never earlier.
        var aliasStores = new HashSet<AliasStore>();
        bool changed = true;
        while (changed)
        {
            changed = false;
            foreach (int blockIndex in region.OrderBy(index => blocks[index].Start))
            {
                Block block = blocks[blockIndex];
                for (int i = block.Start; i < block.End; i++)
                {
                    changed |= PropagateAlias(instructions[i], i, block, aliases, aliasStores);
                }
            }
        }

        return aliases;
    }

    // One propagation step over a single instruction; returns whether it discovered a new alias or
    // alias-holding store.
    private static bool PropagateAlias(IrInst instruction, int index, Block block, HashSet<int> aliases, HashSet<AliasStore> aliasStores)
    {
        switch (instruction)
        {
            case IrInst.Borrow borrow when aliases.Contains(borrow.SourceTemp):
                return aliases.Add(borrow.Target);
            case IrInst.StoreLocal store when aliases.Contains(store.Source):
                return aliasStores.Add(new AliasStore(store.Slot, index, block.Start, block.End));
            case IrInst.LoadLocal load when LoadSeesAliasStore(aliasStores, load.Slot, index, block):
                return aliases.Add(load.Target);
            case IrInst.StoreMemOffset cellStore when aliases.Contains(cellStore.Source):
                return aliases.Add(cellStore.BasePtr);
            case IrInst.MakeClosure mc when aliases.Contains(mc.EnvPtrTemp):
                return aliases.Add(mc.Target);
            case IrInst.MakeClosureStack mcs when aliases.Contains(mcs.EnvPtrTemp):
                return aliases.Add(mcs.Target);
            case IrInst.CallClosure call when aliases.Contains(call.ArgTemp) || aliases.Contains(call.ClosureTemp):
                return aliases.Add(call.Target);
            case IrInst.CallKnown known when aliases.Contains(known.ArgTemp) || aliases.Contains(known.EnvTemp):
                return aliases.Add(known.Target);
            default:
                return false;
        }
    }

    // A load reads an alias out of its slot only past a store of one: a store later in the load's
    // own block is a different value (a loop parameter's successor stored after the old value was
    // read for its release walk), while a store in any other block may reach the load.
    private static bool LoadSeesAliasStore(HashSet<AliasStore> aliasStores, int slot, int loadIndex, Block block)
    {
        foreach (AliasStore store in aliasStores)
        {
            if (store.Slot != slot)
            {
                continue;
            }

            bool sameBlock = store.BlockStart == block.Start && store.BlockEnd == block.End;
            if (!sameBlock || store.Index < loadIndex)
            {
                return true;
            }
        }

        return false;
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

    // A `StoreLocal` of an alias into a slot, with the block it sits in.
    private sealed record AliasStore(int Slot, int Index, int BlockStart, int BlockEnd);
}
