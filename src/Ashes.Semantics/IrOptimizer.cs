namespace Ashes.Semantics;

/// <summary>
/// IR-level optimization pass pipeline (Phase 4).
/// Runs after semantic lowering, before the backend.
/// All optimizations are invisible to the user — observable behaviour is identical.
/// </summary>
public static class IrOptimizer
{
    /// <summary>
    /// Runs the full optimization pipeline on the given IR program.
    /// Returns a new IrProgram with optimized instructions.
    /// </summary>
    public static IrProgram Optimize(IrProgram program)
    {
        var optimizedEntry = OptimizeFunction(program.EntryFunction);
        var optimizedFuncs = program.Functions.Select(OptimizeFunction).ToList();

        return program with
        {
            EntryFunction = optimizedEntry,
            Functions = optimizedFuncs,
        };
    }

    private static IrFunction OptimizeFunction(IrFunction function)
    {
        var instructions = function.Instructions;

        // Pass ordering matters — each pass may enable further optimizations in subsequent passes.
        instructions = ElideBorrowsForConstants(instructions);
        instructions = FoldConstants(instructions);
        instructions = ElideDeadCode(instructions);
        instructions = ElideRedundantDrops(instructions);

        return function with
        {
            Instructions = instructions,
        };
    }

    // ── Pass 1: Elide borrows for constants and copy-type loads ─────────
    // A Borrow of a LoadConstInt/LoadConstFloat/LoadConstBool is a no-op
    // in the backend (simple value copy). The Borrow instruction exists for
    // semantic completeness but copy types have no ownership semantics.
    // Currently this pass is a no-op because the backend already handles
    // Borrow as a trivial value copy that LLVM's codegen optimizes away.
    // When temp aliasing / remapping is added, this pass will eliminate
    // the Borrow instructions entirely for copy-type sources.

    private static List<IrInst> ElideBorrowsForConstants(List<IrInst> instructions)
    {
        // Future: when temp aliasing infrastructure exists, remove Borrow
        // instructions whose source is a copy-type constant and remap uses
        // of the borrow target to the original source.
        return instructions;
    }

    // ── Pass 2: Constant folding ────────────────────────────────────────
    // Evaluate arithmetic on known constant operands at compile time.

    private static List<IrInst> FoldConstants(List<IrInst> instructions)
    {
        // Map from temp → known constant value
        var knownInts = new Dictionary<int, long>();
        var knownFloats = new Dictionary<int, double>();
        var knownBools = new Dictionary<int, bool>();

        var result = new List<IrInst>(instructions.Count);
        bool changed = false;

        foreach (var inst in instructions)
        {
            switch (inst)
            {
                case IrInst.LoadConstInt lci:
                    knownInts[lci.Target] = lci.Value;
                    result.Add(inst);
                    break;

                case IrInst.LoadConstFloat lcf:
                    knownFloats[lcf.Target] = lcf.Value;
                    result.Add(inst);
                    break;

                case IrInst.LoadConstBool lcb:
                    knownBools[lcb.Target] = lcb.Value;
                    result.Add(inst);
                    break;

                case IrInst.AddInt add when knownInts.ContainsKey(add.Left) && knownInts.ContainsKey(add.Right):
                {
                    long folded = knownInts[add.Left] + knownInts[add.Right];
                    knownInts[add.Target] = folded;
                    result.Add(new IrInst.LoadConstInt(add.Target, folded) { Location = inst.Location });
                    changed = true;
                    break;
                }

                case IrInst.SubInt sub when knownInts.ContainsKey(sub.Left) && knownInts.ContainsKey(sub.Right):
                {
                    long folded = knownInts[sub.Left] - knownInts[sub.Right];
                    knownInts[sub.Target] = folded;
                    result.Add(new IrInst.LoadConstInt(sub.Target, folded) { Location = inst.Location });
                    changed = true;
                    break;
                }

                case IrInst.MulInt mul when knownInts.ContainsKey(mul.Left) && knownInts.ContainsKey(mul.Right):
                {
                    long folded = knownInts[mul.Left] * knownInts[mul.Right];
                    knownInts[mul.Target] = folded;
                    result.Add(new IrInst.LoadConstInt(mul.Target, folded) { Location = inst.Location });
                    changed = true;
                    break;
                }

                case IrInst.DivInt div when knownInts.ContainsKey(div.Left) && knownInts.ContainsKey(div.Right)
                                           && knownInts[div.Right] != 0:
                {
                    long folded = knownInts[div.Left] / knownInts[div.Right];
                    knownInts[div.Target] = folded;
                    result.Add(new IrInst.LoadConstInt(div.Target, folded) { Location = inst.Location });
                    changed = true;
                    break;
                }

                case IrInst.AddFloat addF when knownFloats.ContainsKey(addF.Left) && knownFloats.ContainsKey(addF.Right):
                {
                    double folded = knownFloats[addF.Left] + knownFloats[addF.Right];
                    knownFloats[addF.Target] = folded;
                    result.Add(new IrInst.LoadConstFloat(addF.Target, folded) { Location = inst.Location });
                    changed = true;
                    break;
                }

                case IrInst.SubFloat subF when knownFloats.ContainsKey(subF.Left) && knownFloats.ContainsKey(subF.Right):
                {
                    double folded = knownFloats[subF.Left] - knownFloats[subF.Right];
                    knownFloats[subF.Target] = folded;
                    result.Add(new IrInst.LoadConstFloat(subF.Target, folded) { Location = inst.Location });
                    changed = true;
                    break;
                }

                case IrInst.MulFloat mulF when knownFloats.ContainsKey(mulF.Left) && knownFloats.ContainsKey(mulF.Right):
                {
                    double folded = knownFloats[mulF.Left] * knownFloats[mulF.Right];
                    knownFloats[mulF.Target] = folded;
                    result.Add(new IrInst.LoadConstFloat(mulF.Target, folded) { Location = inst.Location });
                    changed = true;
                    break;
                }

                case IrInst.DivFloat divF when knownFloats.ContainsKey(divF.Left) && knownFloats.ContainsKey(divF.Right):
                {
                    double folded = knownFloats[divF.Left] / knownFloats[divF.Right];
                    knownFloats[divF.Target] = folded;
                    result.Add(new IrInst.LoadConstFloat(divF.Target, folded) { Location = inst.Location });
                    changed = true;
                    break;
                }

                case IrInst.CmpIntEq cmpEq when knownInts.ContainsKey(cmpEq.Left) && knownInts.ContainsKey(cmpEq.Right):
                {
                    bool folded = knownInts[cmpEq.Left] == knownInts[cmpEq.Right];
                    knownBools[cmpEq.Target] = folded;
                    result.Add(new IrInst.LoadConstBool(cmpEq.Target, folded) { Location = inst.Location });
                    changed = true;
                    break;
                }

                case IrInst.CmpIntNe cmpNe when knownInts.ContainsKey(cmpNe.Left) && knownInts.ContainsKey(cmpNe.Right):
                {
                    bool folded = knownInts[cmpNe.Left] != knownInts[cmpNe.Right];
                    knownBools[cmpNe.Target] = folded;
                    result.Add(new IrInst.LoadConstBool(cmpNe.Target, folded) { Location = inst.Location });
                    changed = true;
                    break;
                }

                case IrInst.CmpIntGe cmpGe when knownInts.ContainsKey(cmpGe.Left) && knownInts.ContainsKey(cmpGe.Right):
                {
                    bool folded = knownInts[cmpGe.Left] >= knownInts[cmpGe.Right];
                    knownBools[cmpGe.Target] = folded;
                    result.Add(new IrInst.LoadConstBool(cmpGe.Target, folded) { Location = inst.Location });
                    changed = true;
                    break;
                }

                case IrInst.CmpIntLe cmpLe when knownInts.ContainsKey(cmpLe.Left) && knownInts.ContainsKey(cmpLe.Right):
                {
                    bool folded = knownInts[cmpLe.Left] <= knownInts[cmpLe.Right];
                    knownBools[cmpLe.Target] = folded;
                    result.Add(new IrInst.LoadConstBool(cmpLe.Target, folded) { Location = inst.Location });
                    changed = true;
                    break;
                }

                // Labels, jumps, and control flow invalidate constant knowledge
                case IrInst.Label:
                    knownInts.Clear();
                    knownFloats.Clear();
                    knownBools.Clear();
                    result.Add(inst);
                    break;

                default:
                    result.Add(inst);
                    break;
            }
        }

        return changed ? result : instructions;
    }

    // ── Pass 3: Dead code elimination ───────────────────────────────────
    // Remove LoadConst instructions whose target temp is never used.

    private static List<IrInst> ElideDeadCode(List<IrInst> instructions)
    {
        // Collect all temps that are used as operands (sources)
        var usedTemps = new HashSet<int>();
        foreach (var inst in instructions)
        {
            CollectUsedTemps(inst, usedTemps);
        }

        var result = new List<IrInst>(instructions.Count);
        bool changed = false;

        foreach (var inst in instructions)
        {
            // Only remove LoadConst* instructions whose target is never read
            if (inst is IrInst.LoadConstInt lci && !usedTemps.Contains(lci.Target))
            {
                changed = true;
                continue;
            }

            if (inst is IrInst.LoadConstFloat lcf && !usedTemps.Contains(lcf.Target))
            {
                changed = true;
                continue;
            }

            if (inst is IrInst.LoadConstBool lcb && !usedTemps.Contains(lcb.Target))
            {
                changed = true;
                continue;
            }

            result.Add(inst);
        }

        return changed ? result : instructions;
    }

    // ── Pass 4: Drop elision ────────────────────────────────────────────
    // Remove Drop instructions for slots that were never stored to
    // (the value is uninitialized or was already consumed).
    // Currently a no-op — the Drop instructions in the IR are needed for
    // semantic correctness (Phase 2). When the allocator moves beyond
    // linear/bump allocation to per-object free(), this pass will skip
    // drops for values proven to be dead or moved.

    private static List<IrInst> ElideRedundantDrops(List<IrInst> instructions)
    {
        // Future: analyze ownership flow to identify drops on values that
        // are dead (never initialized) or already consumed (moved).
        return instructions;
    }

    /// <summary>
    /// Collects all temp indices that are read (used as operands) by an instruction.
    /// This does NOT include target/destination temps — only sources.
    /// </summary>
    private static void CollectUsedTemps(IrInst inst, HashSet<int> usedTemps)
    {
        switch (inst)
        {
            case IrInst.AddInt a: usedTemps.Add(a.Left); usedTemps.Add(a.Right); break;
            case IrInst.SubInt s: usedTemps.Add(s.Left); usedTemps.Add(s.Right); break;
            case IrInst.MulInt m: usedTemps.Add(m.Left); usedTemps.Add(m.Right); break;
            case IrInst.DivInt d: usedTemps.Add(d.Left); usedTemps.Add(d.Right); break;
            case IrInst.AddFloat a: usedTemps.Add(a.Left); usedTemps.Add(a.Right); break;
            case IrInst.SubFloat s: usedTemps.Add(s.Left); usedTemps.Add(s.Right); break;
            case IrInst.MulFloat m: usedTemps.Add(m.Left); usedTemps.Add(m.Right); break;
            case IrInst.DivFloat d: usedTemps.Add(d.Left); usedTemps.Add(d.Right); break;
            case IrInst.CmpIntGe c: usedTemps.Add(c.Left); usedTemps.Add(c.Right); break;
            case IrInst.CmpIntLe c: usedTemps.Add(c.Left); usedTemps.Add(c.Right); break;
            case IrInst.CmpIntEq c: usedTemps.Add(c.Left); usedTemps.Add(c.Right); break;
            case IrInst.CmpIntNe c: usedTemps.Add(c.Left); usedTemps.Add(c.Right); break;
            case IrInst.CmpFloatGe c: usedTemps.Add(c.Left); usedTemps.Add(c.Right); break;
            case IrInst.CmpFloatLe c: usedTemps.Add(c.Left); usedTemps.Add(c.Right); break;
            case IrInst.CmpFloatEq c: usedTemps.Add(c.Left); usedTemps.Add(c.Right); break;
            case IrInst.CmpFloatNe c: usedTemps.Add(c.Left); usedTemps.Add(c.Right); break;
            case IrInst.CmpStrEq c: usedTemps.Add(c.Left); usedTemps.Add(c.Right); break;
            case IrInst.CmpStrNe c: usedTemps.Add(c.Left); usedTemps.Add(c.Right); break;
            case IrInst.ConcatStr c: usedTemps.Add(c.Left); usedTemps.Add(c.Right); break;
            case IrInst.StoreLocal s: usedTemps.Add(s.Source); break;
            case IrInst.StoreMemOffset s: usedTemps.Add(s.BasePtr); usedTemps.Add(s.Source); break;
            case IrInst.LoadMemOffset l: usedTemps.Add(l.BasePtr); break;
            case IrInst.MakeClosure mc: usedTemps.Add(mc.EnvPtrTemp); break;
            case IrInst.CallClosure cc: usedTemps.Add(cc.ClosureTemp); usedTemps.Add(cc.ArgTemp); break;
            case IrInst.SetAdtField sf: usedTemps.Add(sf.Ptr); usedTemps.Add(sf.Source); break;
            case IrInst.GetAdtTag gt: usedTemps.Add(gt.Ptr); break;
            case IrInst.GetAdtField gf: usedTemps.Add(gf.Ptr); break;
            case IrInst.PrintInt p: usedTemps.Add(p.Source); break;
            case IrInst.PrintStr p: usedTemps.Add(p.Source); break;
            case IrInst.PrintBool p: usedTemps.Add(p.Source); break;
            case IrInst.WriteStr w: usedTemps.Add(w.Source); break;
            case IrInst.FileReadText f: usedTemps.Add(f.PathTemp); break;
            case IrInst.FileWriteText f: usedTemps.Add(f.PathTemp); usedTemps.Add(f.TextTemp); break;
            case IrInst.FileExists f: usedTemps.Add(f.PathTemp); break;
            case IrInst.HttpGet h: usedTemps.Add(h.UrlTemp); break;
            case IrInst.HttpPost h: usedTemps.Add(h.UrlTemp); usedTemps.Add(h.BodyTemp); break;
            case IrInst.NetTcpConnect n: usedTemps.Add(n.HostTemp); usedTemps.Add(n.PortTemp); break;
            case IrInst.NetTcpSend n: usedTemps.Add(n.SocketTemp); usedTemps.Add(n.TextTemp); break;
            case IrInst.NetTcpReceive n: usedTemps.Add(n.SocketTemp); usedTemps.Add(n.MaxBytesTemp); break;
            case IrInst.NetTcpClose n: usedTemps.Add(n.SocketTemp); break;
            case IrInst.Drop d: usedTemps.Add(d.SourceTemp); break;
            case IrInst.Borrow b: usedTemps.Add(b.SourceTemp); break;
            case IrInst.PanicStr p: usedTemps.Add(p.Source); break;
            case IrInst.JumpIfFalse j: usedTemps.Add(j.CondTemp); break;
            case IrInst.Return r: usedTemps.Add(r.Source); break;
            // LoadConstInt, LoadConstFloat, LoadConstBool, LoadConstStr, LoadLocal,
            // LoadEnv, LoadProgramArgs, ReadLine, Alloc, AllocAdt, Label, Jump:
            // These either have no source temps or only define targets.
        }
    }
}
