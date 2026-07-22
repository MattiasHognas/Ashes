namespace Ashes.Semantics;

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
        List<IrInst> instructions = [.. function.Instructions];
        int tempCount = function.TempCount;
        int[] ownerSlots = instructions
            .OfType<IrInst.RcDrop>()
            .Where(drop => drop.OwnerSlot >= 0)
            .Select(drop => drop.OwnerSlot)
            .Distinct()
            .ToArray();

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

            PlaceOwner(instructions, ownerSlot, anchor, anchors[0].index, ref tempCount, function.Label, borrowedArgumentCalls);
        }

        return function with { Instructions = instructions, TempCount = tempCount };
    }

    private static void PlaceOwner(
        List<IrInst> instructions,
        int ownerSlot,
        IrInst.RcDrop anchor,
        int anchorIndex,
        ref int tempCount,
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

        HashSet<int> region = ReachableBeforeBoundary(blocks, definitionBlock, boundaryBlock);
        if (region.Count == 0)
        {
            return;
        }

        foreach (int blockIndex in region)
        {
            Block block = blocks[blockIndex];
            block.OwnerLoads = FindOwnerLoads(instructions, block, ownerSlot);
            block.HasUse = block.OwnerLoads.Count > 0;
        }

        ComputeLiveness(blocks, region);
        Dictionary<int, List<IrInst>> insertions = CollectInsertions(
            instructions, blocks, region, definitionBlock, owner, anchor, borrowedArgumentCalls, ref tempCount);

        foreach ((int index, List<IrInst> added) in insertions.OrderByDescending(pair => pair.Key))
        {
            instructions.InsertRange(index, added);
        }

        if (ShouldExplain())
        {
            int dropCount = insertions.Values.SelectMany(value => value).Count(instruction => instruction is IrInst.RcDrop);
            int dupCount = insertions.Values.SelectMany(value => value).Count(instruction => instruction is IrInst.RcDup);
            Console.Error.WriteLine($"[ownership] place {functionLabel} slot={ownerSlot} dup={dupCount} drop={dropCount}");
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
                int lastUse = FindLastOwnerUse(instructions, block, block.OwnerLoads);
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

    private static int FindLastOwnerUse(List<IrInst> instructions, Block block, List<int> ownerLoads)
    {
        int lastUse = ownerLoads[^1];
        var aliases = new HashSet<int>();
        foreach (int loadIndex in ownerLoads)
        {
            aliases.Add(((IrInst.LoadLocal)instructions[loadIndex]).Target);
        }

        var usedTemps = new HashSet<int>();
        for (int i = ownerLoads[0] + 1; i < block.End; i++)
        {
            if (instructions[i] is IrInst.Borrow borrow && aliases.Contains(borrow.SourceTemp))
            {
                aliases.Add(borrow.Target);
            }

            usedTemps.Clear();
            IrOptimizer.CollectUsedTemps(instructions[i], usedTemps);
            if (usedTemps.Overlaps(aliases))
            {
                lastUse = i;
            }
        }

        return lastUse;
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

    private static HashSet<int> ReachableBeforeBoundary(List<Block> blocks, int start, int boundary)
    {
        var reachable = new HashSet<int>();
        var pending = new Stack<int>();
        pending.Push(start);
        while (pending.Count > 0)
        {
            int current = pending.Pop();
            if (current > boundary || !reachable.Add(current) || current == boundary)
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

    private static List<Block> BuildBlocks(List<IrInst> instructions)
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
        var blocks = new List<Block>(startArray.Length);
        for (int i = 0; i < startArray.Length; i++)
        {
            blocks.Add(new Block(startArray[i], i + 1 < startArray.Length ? startArray[i + 1] : instructions.Count));
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

    private static void AddSuccessor(List<Block> blocks, int from, int to)
    {
        if (!blocks[from].Successors.Contains(to))
        {
            blocks[from].Successors.Add(to);
            blocks[to].Predecessors.Add(from);
        }
    }

    private static int FindBlock(List<Block> blocks, int instructionIndex)
        => blocks.FindIndex(block => instructionIndex >= block.Start && instructionIndex < block.End);

    private static bool IsTerminator(IrInst instruction)
        => instruction is IrInst.Jump or IrInst.JumpIfFalse or IrInst.SwitchTag or IrInst.Return;

    private static bool ShouldExplain()
        => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ASHES_EXPLAIN_OWNERSHIP"));

    private sealed class Block(int start, int end)
    {
        public int Start { get; } = start;
        public int End { get; } = end;
        public List<int> Successors { get; } = [];
        public List<int> Predecessors { get; } = [];
        public List<int> OwnerLoads { get; set; } = [];
        public bool HasUse { get; set; }
        public bool LiveIn { get; set; }
        public bool LiveOut { get; set; }
    }

    private sealed record OwnerRegion(int DefinitionIndex, int BoundaryIndex, int DefinitionTemp);
}
