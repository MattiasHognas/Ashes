namespace Ashes.Semantics;

/// <summary>
/// Result of the state machine transform applied to a coroutine's instruction list.
/// </summary>
/// <param name="Instructions">The transformed instruction list with state dispatch and save/restore sequences.</param>
/// <param name="StateCount">Number of states (N await points produce N+1 states).</param>
/// <param name="StateStructSize">Total size of the task/state struct in bytes (header + captures + live vars).</param>
/// <param name="MaxTemp">Highest temp index used (including temps added by the transform).</param>
public sealed record StateMachineResult(
    List<IrInst> Instructions,
    int StateCount,
    int StateStructSize,
    int MaxTemp
);

/// <summary>
/// Transforms a linear list of IR instructions (from an async body) into a
/// state-machine form. Each AwaitTask instruction becomes a suspend point
/// that splits the coroutine into numbered states.
///
/// The transform:
/// 1. Identifies AwaitTask instructions as split points.
/// 2. Computes which temps are live across each split point.
/// 3. Assigns state-struct slots for live temps.
/// 4. Rewrites instructions with a state-dispatch header, save/restore
///    sequences at suspend/resume points, and proper return values.
///
/// State struct layout:
///   [0]:  state_index (i64)
///   [8]:  coroutine_fn (i64)      — set by CreateTask, not touched here
///   [16]: result_slot (i64)       — awaited task result / final result
///   [24]: awaited_task (i64)      — pointer to sub-task being awaited
///   [32]: next_task (i64)         — linked-list pointer for scheduler/task chaining
///   [40]: sleep_duration_ms (i64) — scheduler delay metadata
///   [48]: capture_0 (i64)         — captured env variables
///   [48 + captureCount*8]: live_var_0 (i64) — live variable slots
///   ...
/// </summary>
public static class StateMachineTransform
{
    /// <summary>
    /// Transforms the instruction list of a coroutine function.
    /// If there are no AwaitTask instructions, the transform adds a minimal
    /// state-0-only wrapper that stores the result and returns COMPLETED.
    /// </summary>
    /// <param name="instructions">The linear instruction list from lowering the async body.</param>
    /// <param name="captureCount">Number of captured env variables (determines offset for live vars).</param>
    /// <returns>The transformed instruction list with state machine structure.</returns>
    public static StateMachineResult Transform(List<IrInst> instructions, int captureCount)
    {
        // Find all AwaitTask instructions and their positions
        var awaitPositions = new List<int>();
        for (int i = 0; i < instructions.Count; i++)
        {
            if (instructions[i] is IrInst.AwaitTask)
            {
                awaitPositions.Add(i);
            }
        }

        int stateCount = awaitPositions.Count + 1;

        // Compute live temps across each await point
        var liveAcross = ComputeLiveTempsAcrossAwaits(instructions, awaitPositions);

        // Compute local slots that are written before and read after each await point
        var liveLocalsAcross = ComputeLiveLocalsAcrossAwaits(instructions, awaitPositions);

        // Build the union of all live temps (each gets a unique slot in the state struct)
        var allLiveTemps = new SortedSet<int>();
        foreach (var set in liveAcross)
        {
            allLiveTemps.UnionWith(set);
        }

        // Build the union of all live locals
        var allLiveLocals = new SortedSet<int>();
        foreach (var set in liveLocalsAcross)
        {
            allLiveLocals.UnionWith(set);
        }

        // Assign state struct offsets for live temps
        int liveVarBaseOffset = TaskStructLayout.HeaderSize + captureCount * 8;
        var tempToSlotOffset = new Dictionary<int, int>();
        int slotIndex = 0;
        foreach (int temp in allLiveTemps)
        {
            tempToSlotOffset[temp] = liveVarBaseOffset + slotIndex * 8;
            slotIndex++;
        }

        // Assign state struct offsets for live locals (after temps)
        int localSaveBaseOffset = liveVarBaseOffset + allLiveTemps.Count * 8;
        var localToSlotOffset = new Dictionary<int, int>();
        int localSlotIndex = 0;
        foreach (int local in allLiveLocals)
        {
            localToSlotOffset[local] = localSaveBaseOffset + localSlotIndex * 8;
            localSlotIndex++;
        }

        int stateStructSize = localSaveBaseOffset + allLiveLocals.Count * 8;

        // Build the transformed instruction list
        var result = new List<IrInst>();
        int maxTemp = 0;

        // Track the highest temp used in original instructions
        foreach (var inst in instructions)
        {
            foreach (int t in GetAllTemps(inst))
            {
                if (t > maxTemp) maxTemp = t;
            }
        }

        // Reserve temps that don't conflict with body temps
        int stateStructTemp = maxTemp + 1;
        int stateIdxTemp = maxTemp + 2;
        int statusTemp = maxTemp + 3;
        int awaitResultTemp = maxTemp + 4;
        maxTemp = maxTemp + 4;

        // Emit: load state struct pointer from local[0] into a dedicated temp
        result.Add(new IrInst.LoadLocal(stateStructTemp, 0));

        if (awaitPositions.Count == 0)
        {
            // No await points — just run the body and store the result.
            // Load env captures into the appropriate env-loading pattern.
            // The body instructions use LoadEnv to access captures via local[0],
            // which is the state struct pointer. Captures are at HeaderSize + i*8.
            // We need to adjust LoadEnv offsets to account for the header.

            // Emit all original instructions except the final Return.
            // Replace the Return with storing the result in the state struct.
            for (int i = 0; i < instructions.Count; i++)
            {
                var inst = instructions[i];
                if (inst is IrInst.Return ret)
                {
                    // Store result in state struct result slot
                    result.Add(new IrInst.StoreMemOffset(stateStructTemp, TaskStructLayout.ResultSlot, ret.Source));
                    // Set state_index = -1 (COMPLETED)
                    int completedConst = ++maxTemp;
                    result.Add(new IrInst.LoadConstInt(completedConst, -1));
                    result.Add(new IrInst.StoreMemOffset(stateStructTemp, TaskStructLayout.StateIndex, completedConst));
                    // Return 1 (COMPLETED status)
                    result.Add(new IrInst.LoadConstInt(statusTemp, 1));
                    result.Add(new IrInst.Return(statusTemp));
                }
                else
                {
                    result.Add(AdjustLoadEnvForStateStruct(inst, stateStructTemp, captureCount));
                }
            }

            return new StateMachineResult(result, stateCount, stateStructSize, maxTemp);
        }

        // --- Multi-state coroutine ---

        // Generate state dispatch header
        // Load state index from state struct
        result.Add(new IrInst.LoadMemOffset(stateIdxTemp, stateStructTemp, TaskStructLayout.StateIndex));

        // Generate labels for each state
        var stateLabels = new string[stateCount];
        for (int i = 0; i < stateCount; i++)
        {
            stateLabels[i] = $"__state_{i}";
        }

        // Dispatch: check state index against each state and jump
        // We use a chain of comparisons since IR doesn't have a switch instruction.
        for (int i = 1; i < stateCount; i++)
        {
            int cmpTemp = ++maxTemp;
            int constTemp = ++maxTemp;
            result.Add(new IrInst.LoadConstInt(constTemp, i));
            result.Add(new IrInst.CmpIntEq(cmpTemp, stateIdxTemp, constTemp));
            result.Add(new IrInst.JumpIfFalse(cmpTemp, i + 1 < stateCount ? $"__dispatch_{i + 1}" : stateLabels[0]));
            result.Add(new IrInst.Jump(stateLabels[i]));
            if (i + 1 < stateCount)
            {
                result.Add(new IrInst.Label($"__dispatch_{i + 1}"));
            }
        }
        // Default: state 0
        result.Add(new IrInst.Jump(stateLabels[0]));

        // Split original instructions into segments at await points
        var segments = SplitAtAwaits(instructions, awaitPositions);

        // Emit each state
        for (int stateIdx = 0; stateIdx < stateCount; stateIdx++)
        {
            result.Add(new IrInst.Label(stateLabels[stateIdx]));

            if (stateIdx > 0)
            {
                // Resume: restore live temps from state struct
                var liveTempsAtThisPoint = liveAcross[stateIdx - 1];
                var restoreVars = new List<(int SlotOffset, int TargetTemp)>();
                foreach (int temp in liveTempsAtThisPoint)
                {
                    if (tempToSlotOffset.TryGetValue(temp, out int offset))
                    {
                        result.Add(new IrInst.LoadMemOffset(temp, stateStructTemp, offset));
                        restoreVars.Add((offset, temp));
                    }
                }

                // Resume: restore live locals from state struct
                var liveLocalsAtThisPoint = liveLocalsAcross[stateIdx - 1];
                foreach (int local in liveLocalsAtThisPoint)
                {
                    if (localToSlotOffset.TryGetValue(local, out int offset))
                    {
                        int loadTemp = ++maxTemp;
                        result.Add(new IrInst.LoadMemOffset(loadTemp, stateStructTemp, offset));
                        result.Add(new IrInst.StoreLocal(local, loadTemp));
                    }
                }

                // Load the result from the awaited sub-task into the AwaitTask's target temp
                var awaitInst = (IrInst.AwaitTask)instructions[awaitPositions[stateIdx - 1]];
                result.Add(new IrInst.LoadMemOffset(awaitInst.Target, stateStructTemp, TaskStructLayout.ResultSlot));

                result.Add(new IrInst.Resume(stateStructTemp, awaitInst.Target, restoreVars));
            }

            // Emit the segment's instructions
            var segment = segments[stateIdx];
            for (int i = 0; i < segment.Count; i++)
            {
                var inst = segment[i];

                if (inst is IrInst.AwaitTask awaitTask)
                {
                    // Suspend: save live temps to state struct
                    var liveTempsAtThisPoint = liveAcross[stateIdx];
                    var saveVars = new List<(int SlotOffset, int SourceTemp)>();
                    foreach (int temp in liveTempsAtThisPoint)
                    {
                        if (tempToSlotOffset.TryGetValue(temp, out int offset))
                        {
                            result.Add(new IrInst.StoreMemOffset(stateStructTemp, offset, temp));
                            saveVars.Add((offset, temp));
                        }
                    }

                    // Suspend: save live locals to state struct
                    var liveLocalsAtThisPoint = liveLocalsAcross[stateIdx];
                    foreach (int local in liveLocalsAtThisPoint)
                    {
                        if (localToSlotOffset.TryGetValue(local, out int offset))
                        {
                            int loadTemp = ++maxTemp;
                            result.Add(new IrInst.LoadLocal(loadTemp, local));
                            result.Add(new IrInst.StoreMemOffset(stateStructTemp, offset, loadTemp));
                        }
                    }

                    // Store the awaited sub-task pointer
                    result.Add(new IrInst.StoreMemOffset(stateStructTemp, TaskStructLayout.AwaitedTask, awaitTask.TaskTemp));

                    // Set next state index
                    int nextStateConst = ++maxTemp;
                    result.Add(new IrInst.LoadConstInt(nextStateConst, stateIdx + 1));
                    result.Add(new IrInst.StoreMemOffset(stateStructTemp, TaskStructLayout.StateIndex, nextStateConst));

                    result.Add(new IrInst.Suspend(stateStructTemp, stateIdx + 1, awaitTask.TaskTemp, saveVars));

                    // Return 0 (SUSPENDED status)
                    result.Add(new IrInst.LoadConstInt(statusTemp, 0));
                    result.Add(new IrInst.Return(statusTemp));
                }
                else if (inst is IrInst.Return ret)
                {
                    // Final state: store result and return COMPLETED
                    result.Add(new IrInst.StoreMemOffset(stateStructTemp, TaskStructLayout.ResultSlot, ret.Source));
                    int completedConst = ++maxTemp;
                    result.Add(new IrInst.LoadConstInt(completedConst, -1));
                    result.Add(new IrInst.StoreMemOffset(stateStructTemp, TaskStructLayout.StateIndex, completedConst));
                    result.Add(new IrInst.LoadConstInt(statusTemp, 1));
                    result.Add(new IrInst.Return(statusTemp));
                }
                else
                {
                    result.Add(AdjustLoadEnvForStateStruct(inst, stateStructTemp, captureCount));
                }
            }
        }

        return new StateMachineResult(result, stateCount, stateStructSize, maxTemp);
    }

    /// <summary>
    /// LoadEnv instructions in lambdas load from env_ptr at offset index*8.
    /// In a coroutine, captures are in the state struct at HeaderSize + index*8.
    /// This adjusts LoadEnv to LoadMemOffset with the correct base temp and offset.
    /// </summary>
    private static IrInst AdjustLoadEnvForStateStruct(IrInst inst, int stateStructTemp, int captureCount)
    {
        if (inst is IrInst.LoadEnv loadEnv)
        {
            // Replace LoadEnv with LoadMemOffset from the state struct temp.
            // Captures are at HeaderSize + index*8 in the state struct.
            return new IrInst.LoadMemOffset(loadEnv.Target, stateStructTemp,
                TaskStructLayout.HeaderSize + loadEnv.Index * 8)
            {
                Location = inst.Location
            };
        }
        return inst;
    }

    /// <summary>
    /// Splits the instruction list into segments separated at AwaitTask positions.
    /// Segment 0: instructions[0..await0] (including the AwaitTask)
    /// Segment 1: instructions[await0+1..await1] (including the AwaitTask)
    /// ...
    /// Segment N: instructions[awaitN-1+1..end] (no AwaitTask, ends with Return)
    /// </summary>
    private static List<List<IrInst>> SplitAtAwaits(List<IrInst> instructions, List<int> awaitPositions)
    {
        var segments = new List<List<IrInst>>();
        int start = 0;
        foreach (int pos in awaitPositions)
        {
            var segment = new List<IrInst>();
            for (int i = start; i <= pos; i++)
            {
                segment.Add(instructions[i]);
            }
            segments.Add(segment);
            start = pos + 1;
        }
        // Final segment (after last await to end)
        var lastSegment = new List<IrInst>();
        for (int i = start; i < instructions.Count; i++)
        {
            lastSegment.Add(instructions[i]);
        }
        segments.Add(lastSegment);
        return segments;
    }

    /// <summary>
    /// For each await point, computes which temps are live across that point
    /// (defined before and used after the await).
    /// </summary>
    private static List<HashSet<int>> ComputeLiveTempsAcrossAwaits(
        List<IrInst> instructions, List<int> awaitPositions)
    {
        var result = new List<HashSet<int>>();

        foreach (int awaitPos in awaitPositions)
        {
            // Collect temps defined before the await point
            var definedBefore = new HashSet<int>();
            for (int i = 0; i < awaitPos; i++)
            {
                foreach (int t in GetDefinedTemps(instructions[i]))
                {
                    definedBefore.Add(t);
                }
            }

            // Collect temps used after the await point (not including the AwaitTask itself)
            var usedAfter = new HashSet<int>();
            for (int i = awaitPos + 1; i < instructions.Count; i++)
            {
                foreach (int t in GetUsedTemps(instructions[i]))
                {
                    usedAfter.Add(t);
                }
            }

            // Live across = defined before AND used after
            var live = new HashSet<int>(definedBefore);
            live.IntersectWith(usedAfter);

            // Don't save temp 0 (state struct pointer) — it's always available
            live.Remove(0);

            result.Add(live);
        }

        return result;
    }

    /// <summary>
    /// For each await point, computes which local slots are live across that point
    /// (written before and read after the await). Local slot 0 (state struct) and
    /// slot 1 (dummy arg) are excluded since they're function parameters.
    /// </summary>
    private static List<HashSet<int>> ComputeLiveLocalsAcrossAwaits(
        List<IrInst> instructions, List<int> awaitPositions)
    {
        var result = new List<HashSet<int>>();

        foreach (int awaitPos in awaitPositions)
        {
            // Collect locals written before the await point
            var writtenBefore = new HashSet<int>();
            for (int i = 0; i < awaitPos; i++)
            {
                if (instructions[i] is IrInst.StoreLocal store)
                {
                    writtenBefore.Add(store.Slot);
                }
            }

            // Collect locals read after the await point
            var readAfter = new HashSet<int>();
            for (int i = awaitPos + 1; i < instructions.Count; i++)
            {
                if (instructions[i] is IrInst.LoadLocal load)
                {
                    readAfter.Add(load.Slot);
                }
            }

            // Live across = written before AND read after
            var live = new HashSet<int>(writtenBefore);
            live.IntersectWith(readAfter);

            // Exclude slots 0 and 1 (function parameters)
            live.Remove(0);
            live.Remove(1);

            result.Add(live);
        }

        return result;
    }
    /// <summary>
    /// Returns all temps defined (written to) by an instruction.
    /// IMPORTANT: When adding new IrInst types, you MUST add a case here
    /// and in GetUsedTemps to ensure correct liveness analysis across await points.
    /// </summary>
    private static IEnumerable<int> GetDefinedTemps(IrInst inst)
    {
        return inst switch
        {
            IrInst.LoadConstInt i => [i.Target],
            IrInst.LoadConstFloat i => [i.Target],
            IrInst.LoadConstBool i => [i.Target],
            IrInst.LoadConstStr i => [i.Target],
            IrInst.LoadProgramArgs i => [i.Target],
            IrInst.LoadLocal i => [i.Target],
            IrInst.LoadEnv i => [i.Target],
            IrInst.LoadMemOffset i => [i.Target],
            IrInst.AddInt i => [i.Target],
            IrInst.SubInt i => [i.Target],
            IrInst.MulInt i => [i.Target],
            IrInst.DivInt i => [i.Target],
            IrInst.AddFloat i => [i.Target],
            IrInst.SubFloat i => [i.Target],
            IrInst.MulFloat i => [i.Target],
            IrInst.DivFloat i => [i.Target],
            IrInst.CmpIntGe i => [i.Target],
            IrInst.CmpIntLe i => [i.Target],
            IrInst.CmpIntEq i => [i.Target],
            IrInst.CmpIntNe i => [i.Target],
            IrInst.CmpFloatGe i => [i.Target],
            IrInst.CmpFloatLe i => [i.Target],
            IrInst.CmpFloatEq i => [i.Target],
            IrInst.CmpFloatNe i => [i.Target],
            IrInst.CmpStrEq i => [i.Target],
            IrInst.CmpStrNe i => [i.Target],
            IrInst.ConcatStr i => [i.Target],
            IrInst.MakeClosure i => [i.Target],
            IrInst.CallClosure i => [i.Target],
            IrInst.Alloc i => [i.Target],
            IrInst.AllocAdt i => [i.Target],
            IrInst.GetAdtTag i => [i.Target],
            IrInst.GetAdtField i => [i.Target],
            IrInst.ReadLine i => [i.Target],
            IrInst.FileReadText i => [i.Target],
            IrInst.FileWriteText i => [i.Target],
            IrInst.FileExists i => [i.Target],
            IrInst.HttpGet i => [i.Target],
            IrInst.HttpPost i => [i.Target],
            IrInst.NetTcpConnect i => [i.Target],
            IrInst.NetTcpSend i => [i.Target],
            IrInst.NetTcpReceive i => [i.Target],
            IrInst.NetTcpClose i => [i.Target],
            IrInst.Borrow i => [i.Target],
            IrInst.CreateTask i => [i.Target],
            IrInst.CreateCompletedTask i => [i.Target],
            IrInst.AwaitTask i => [i.Target],
            IrInst.RunTask i => [i.Target],
            IrInst.AsyncSleep i => [i.Target],
            IrInst.AsyncAll i => [i.Target],
            IrInst.AsyncRace i => [i.Target],
            _ => []
        };
    }

    /// <summary>
    /// Returns all temps used (read) by an instruction.
    /// IMPORTANT: When adding new IrInst types, you MUST add a case here
    /// and in GetDefinedTemps to ensure correct liveness analysis across await points.
    /// </summary>
    private static IEnumerable<int> GetUsedTemps(IrInst inst)
    {
        return inst switch
        {
            IrInst.StoreLocal s => [s.Source],
            IrInst.StoreMemOffset s => [s.BasePtr, s.Source],
            IrInst.LoadMemOffset l => [l.BasePtr],
            IrInst.AddInt a => [a.Left, a.Right],
            IrInst.SubInt s => [s.Left, s.Right],
            IrInst.MulInt m => [m.Left, m.Right],
            IrInst.DivInt d => [d.Left, d.Right],
            IrInst.AddFloat a => [a.Left, a.Right],
            IrInst.SubFloat s => [s.Left, s.Right],
            IrInst.MulFloat m => [m.Left, m.Right],
            IrInst.DivFloat d => [d.Left, d.Right],
            IrInst.CmpIntGe c => [c.Left, c.Right],
            IrInst.CmpIntLe c => [c.Left, c.Right],
            IrInst.CmpIntEq c => [c.Left, c.Right],
            IrInst.CmpIntNe c => [c.Left, c.Right],
            IrInst.CmpFloatGe c => [c.Left, c.Right],
            IrInst.CmpFloatLe c => [c.Left, c.Right],
            IrInst.CmpFloatEq c => [c.Left, c.Right],
            IrInst.CmpFloatNe c => [c.Left, c.Right],
            IrInst.CmpStrEq c => [c.Left, c.Right],
            IrInst.CmpStrNe c => [c.Left, c.Right],
            IrInst.ConcatStr c => [c.Left, c.Right],
            IrInst.MakeClosure mc => [mc.EnvPtrTemp],
            IrInst.CallClosure cc => [cc.ClosureTemp, cc.ArgTemp],
            IrInst.SetAdtField sf => [sf.Ptr, sf.Source],
            IrInst.GetAdtTag gt => [gt.Ptr],
            IrInst.GetAdtField gf => [gf.Ptr],
            IrInst.PrintInt p => [p.Source],
            IrInst.PrintStr p => [p.Source],
            IrInst.PrintBool p => [p.Source],
            IrInst.WriteStr w => [w.Source],
            IrInst.FileReadText f => [f.PathTemp],
            IrInst.FileWriteText f => [f.PathTemp, f.TextTemp],
            IrInst.FileExists f => [f.PathTemp],
            IrInst.HttpGet h => [h.UrlTemp],
            IrInst.HttpPost h => [h.UrlTemp, h.BodyTemp],
            IrInst.NetTcpConnect n => [n.HostTemp, n.PortTemp],
            IrInst.NetTcpSend n => [n.SocketTemp, n.TextTemp],
            IrInst.NetTcpReceive n => [n.SocketTemp, n.MaxBytesTemp],
            IrInst.NetTcpClose n => [n.SocketTemp],
            IrInst.Drop d => [d.SourceTemp],
            IrInst.Borrow b => [b.SourceTemp],
            IrInst.CreateTask ct => [ct.ClosureTemp],
            IrInst.CreateCompletedTask ct => [ct.ResultTemp],
            IrInst.AwaitTask at => [at.TaskTemp],
            IrInst.RunTask rt => [rt.TaskTemp],
            IrInst.AsyncSleep sl => [sl.MillisecondsTemp],
            IrInst.AsyncAll aa => [aa.TaskListTemp],
            IrInst.AsyncRace ar => [ar.TaskListTemp],
            IrInst.PanicStr p => [p.Source],
            IrInst.JumpIfFalse j => [j.CondTemp],
            IrInst.Return r => [r.Source],
            _ => []
        };
    }

    /// <summary>
    /// Returns all temps referenced (both defined and used) by an instruction.
    /// </summary>
    private static IEnumerable<int> GetAllTemps(IrInst inst)
    {
        return GetDefinedTemps(inst).Concat(GetUsedTemps(inst));
    }
}
