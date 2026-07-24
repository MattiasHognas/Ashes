namespace Ashes.Semantics;

/// <summary>
/// Aggressive compile-time evaluation (partial evaluation) of pure IR.
///
/// Recognizes calls to provably pure, fully-modeled functions whose arguments are
/// compile-time constants, executes them with a small concrete IR interpreter, and
/// replaces the call with a constant load. This removes work LLVM cannot: LLVM will
/// never evaluate a recursive Ashes function (e.g. <c>fib(40)</c>) to a constant, nor
/// hoist an allocating table build out of program startup.
///
/// The pass is best-effort and semantically invisible: whenever evaluation cannot be
/// completed (an unmodeled or impure instruction, a budget overrun, a non-embeddable
/// result) the original runtime code is kept unchanged. Because the interpreter executes
/// the real instruction semantics on the real modeled subset, a completed evaluation
/// yields exactly the value the program would compute at runtime.
///
/// Scope of this stage: scalar results only (Int / Bool / Float) and closures without a
/// captured environment. String and aggregate (list / ADT / record) result embedding are
/// deferred to a later stage that adds a value-graph serializer.
/// </summary>
public static class IrCompileTimeEval
{
    // Bounds keep a valid program from consuming unbounded compiler resources. On overrun
    // the current evaluation aborts and the runtime code is retained (never an error).
    private const long StepBudget = 50_000_000;
    private const int DepthBudget = 20_000;

    // Stack for the interpreter thread. Sized so DepthBudget nested interpreter activations fit
    // comfortably, so the logical depth cap is always hit before any native stack overflow.
    private const int EvalStackBytes = 512 * 1024 * 1024;

    /// <summary>
    /// Folds pure, constant-argument calls in <paramref name="program"/> to their computed results by
    /// interpreting the IR at compile time, returning the rewritten program. Evaluation is bounded by
    /// step and depth budgets and fails open: when a computation exceeds its budget or cannot be folded,
    /// the original runtime code is retained. Setting <c>ASHES_NO_COMPILE_TIME_EVAL</c> disables the pass.
    /// </summary>
    public static IrProgram Evaluate(IrProgram program)
    {
        if (System.Environment.GetEnvironmentVariable("ASHES_NO_COMPILE_TIME_EVAL") is not null)
        {
            return program;
        }

        var functions = new Dictionary<string, IrFunction>(StringComparer.Ordinal)
        {
            [program.EntryFunction.Label] = program.EntryFunction,
        };
        foreach (var f in program.Functions)
        {
            functions[f.Label] = f;
        }

        var stringLiterals = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var s in program.StringLiterals)
        {
            stringLiterals[s.Label] = s.Value;
        }

        var evaluable = ComputeEvaluableFunctions(functions);
        if (evaluable.Count == 0)
        {
            return program;
        }

        bool traceExplain = System.Environment.GetEnvironmentVariable("ASHES_EXPLAIN_COMPILE_TIME") is not null;
        var ctx = new EvalContext(functions, stringLiterals, evaluable, traceExplain);

        // Run the interpreter on a dedicated thread with a large stack. The interpreter recurses
        // once per Ashes call level, and a StackOverflowException is uncatchable in .NET — it would
        // abort the compiler instead of letting the depth budget bail. A large stack guarantees the
        // DepthBudget guard is always reached first and turns a runaway recursion into a clean bail.
        IrProgram result = program;
        var worker = new System.Threading.Thread(
            () => result = RewriteProgram(program, ctx),
            EvalStackBytes);
        worker.Start();
        worker.Join();
        return result;
    }

    private static IrProgram RewriteProgram(IrProgram program, EvalContext ctx)
    {
        IrFunction newEntry = RewriteCalls(program.EntryFunction, ctx);
        var newFuncs = program.Functions.Select(f => RewriteCalls(f, ctx)).ToList();

        return program with
        {
            EntryFunction = newEntry,
            Functions = newFuncs,
        };
    }

    // Whole-program least fixpoint: a function is compile-time-evaluable when every one of
    // its instructions is modeled-and-pure and every function it references by label (via a
    // MakeClosure / CallKnown / LoadFuncAddr) is itself evaluable. Start optimistic and knock
    // out any function that fails, iterating to stability. Closures called through a value
    // (CallClosure) are checked dynamically at evaluation time against this same set.
    private static HashSet<string> ComputeEvaluableFunctions(Dictionary<string, IrFunction> functions)
    {
        var candidates = new HashSet<string>(functions.Keys, StringComparer.Ordinal);
        bool changed = true;
        while (changed)
        {
            changed = false;
            foreach (var (label, fn) in functions)
            {
                if (!candidates.Contains(label))
                {
                    continue;
                }

                foreach (var inst in fn.Instructions)
                {
                    if (!IsModeledPure(inst, candidates))
                    {
                        candidates.Remove(label);
                        changed = true;
                        break;
                    }
                }
            }
        }

        return candidates;
    }

    // An instruction is modeled-and-pure when the interpreter fully understands its value
    // semantics and it performs no observable side effect. References to other functions by
    // label require those callees to be evaluable too.
    private static bool IsModeledPure(IrInst inst, HashSet<string> evaluable) => inst switch
    {
        IrInst.MakeClosure mc => evaluable.Contains(mc.FuncLabel),
        IrInst.MakeClosureStack mcs => evaluable.Contains(mcs.FuncLabel),
        IrInst.LoadFuncAddr lfa => evaluable.Contains(lfa.FuncLabel),
        IrInst.CallKnown ck => evaluable.Contains(ck.FuncLabel),
        _ => IsModeledPureLeaf(inst),
    };

    // Instructions with no function-label reference whose value semantics the interpreter
    // models and which have no side effect. Anything not listed makes its enclosing function
    // non-evaluable, so the interpreter never encounters it (IO, FFI, capability, async,
    // resources, raw memory, reuse machinery, transcendentals, big-int, bytes, ...).
    private static bool IsModeledPureLeaf(IrInst inst) => inst switch
    {
        IrInst.LoadConstInt or IrInst.LoadConstFloat or IrInst.LoadConstBool or IrInst.LoadConstStr
            or IrInst.LoadLocal or IrInst.StoreLocal or IrInst.Borrow or IrInst.RcDup
            or IrInst.AddInt or IrInst.SubInt or IrInst.MulInt or IrInst.DivInt or IrInst.DivUInt
            or IrInst.AndInt or IrInst.OrInt or IrInst.XorInt or IrInst.ShlInt or IrInst.ShrInt
            or IrInst.AddFloat or IrInst.SubFloat or IrInst.MulFloat or IrInst.DivFloat
            or IrInst.IntToFloat or IrInst.FloatToInt
            or IrInst.CmpIntGt or IrInst.CmpIntGe or IrInst.CmpIntLt or IrInst.CmpIntLe
            or IrInst.CmpUIntGt or IrInst.CmpUIntGe or IrInst.CmpUIntLt or IrInst.CmpUIntLe
            or IrInst.CmpIntEq or IrInst.CmpIntNe
            or IrInst.CmpFloatGt or IrInst.CmpFloatGe or IrInst.CmpFloatLt or IrInst.CmpFloatLe
            or IrInst.CmpFloatEq or IrInst.CmpFloatNe
            or IrInst.CmpStrEq or IrInst.CmpStrNe or IrInst.ConcatStr
            or IrInst.AllocAdt or IrInst.AllocAdtStack or IrInst.SetAdtField
            or IrInst.GetAdtTag or IrInst.GetAdtField
            or IrInst.Label or IrInst.Jump or IrInst.JumpIfFalse or IrInst.SwitchTag or IrInst.Return
            or IrInst.SaveArenaState or IrInst.RestoreArenaState or IrInst.ReclaimArenaChunks
            or IrInst.SaveStackPointer or IrInst.RestoreStackPointer
            or IrInst.RcDrop or IrInst.CallClosure
            => true,
        _ => false,
    };

    // Forward straight-line scan of one function. Tracks the compile-time-known value of each
    // temp and local slot; at a constant-argument call to an evaluable function it runs the
    // interpreter and, on a scalar result, replaces the call with a constant load. Constant
    // knowledge is cleared at every control-flow boundary, so a fold only fires when the call's
    // operands are established by straight-line code — the shape a top-level `let x = f(const)`
    // lowers to. Dead closure/argument construction left behind is removed by the existing
    // dead-code and arena-bracket passes.
    private static IrFunction RewriteCalls(IrFunction fn, EvalContext ctx)
    {
        var scan = new ScanState();
        var result = new List<IrInst>(fn.Instructions.Count);
        bool changed = false;

        foreach (var inst in fn.Instructions)
        {
            if (TryTrackKnownValue(inst, scan))
            {
                result.Add(inst);
                continue;
            }

            if (TryFoldCallInst(inst, scan, ctx, out var load, out var value))
            {
                scan.Temps[GetTargetOrThrow(inst)] = value!;
                result.Add(load!);
                changed = true;
                continue;
            }

            if (inst is IrInst.Label or IrInst.Jump or IrInst.JumpIfFalse or IrInst.SwitchTag)
            {
                scan.Clear();
            }
            else if (TryGetTarget(inst, out int producedTarget))
            {
                scan.Temps.Remove(producedTarget);
            }

            result.Add(inst);
        }

        return changed ? fn with { Instructions = result } : fn;
    }

    // Records the compile-time value of a constant-producing instruction the caller scan tracks
    // directly. Returns true when the instruction was a tracked value producer.
    private static bool TryTrackKnownValue(IrInst inst, ScanState scan)
    {
        switch (inst)
        {
            case IrInst.LoadConstInt lci: scan.Temps[lci.Target] = new CtInt(lci.Value); return true;
            case IrInst.LoadConstFloat lcf: scan.Temps[lcf.Target] = new CtFloat(lcf.Value); return true;
            case IrInst.LoadConstBool lcb: scan.Temps[lcb.Target] = new CtBool(lcb.Value); return true;
            case IrInst.Borrow b: CopyTemp(scan.Temps, b.Target, b.SourceTemp); return true;
            case IrInst.RcDup d: CopyTemp(scan.Temps, d.Target, d.SourceTemp); return true;
            case IrInst.MakeClosure mc when mc.EnvSizeBytes == 0:
                scan.Temps[mc.Target] = new CtClosure(mc.FuncLabel);
                return true;
            case IrInst.MakeClosureStack mcs when mcs.EnvSizeBytes == 0:
                scan.Temps[mcs.Target] = new CtClosure(mcs.FuncLabel);
                return true;
            case IrInst.StoreLocal sl:
                if (scan.Temps.TryGetValue(sl.Source, out var stored)) { scan.Slots[sl.Slot] = stored; }
                else { scan.Slots.Remove(sl.Slot); }

                return true;
            case IrInst.LoadLocal ll:
                if (scan.Slots.TryGetValue(ll.Slot, out var loaded)) { scan.Temps[ll.Target] = loaded; }
                else { scan.Temps.Remove(ll.Target); }

                return true;
            default:
                return false;
        }
    }

    private static bool TryFoldCallInst(IrInst inst, ScanState scan, EvalContext ctx, out IrInst? load, out CtValue? value)
    {
        switch (inst)
        {
            case IrInst.CallClosure cc:
                return ctx.TryFoldCall(cc.Target, GetClosureLabel(scan.Temps, cc.ClosureTemp), cc.ArgTemp,
                                       scan.Temps, inst.Location, out load, out value);
            case IrInst.CallKnown ck:
                return ctx.TryFoldCall(ck.Target, ck.FuncLabel, ck.ArgTemp,
                                       scan.Temps, inst.Location, out load, out value);
            default:
                load = null;
                value = null;
                return false;
        }
    }

    private static void CopyTemp(Dictionary<int, CtValue> knownTemps, int target, int source)
    {
        if (knownTemps.TryGetValue(source, out var v)) { knownTemps[target] = v; }
        else { knownTemps.Remove(target); }
    }

    private static string? GetClosureLabel(Dictionary<int, CtValue> knownTemps, int closureTemp) =>
        knownTemps.TryGetValue(closureTemp, out var v) && v is CtClosure c ? c.FuncLabel : null;

    private static int GetTargetOrThrow(IrInst inst) =>
        TryGetTarget(inst, out int t) ? t : throw new System.InvalidOperationException("call instruction without a Target");

    private static bool TryGetTarget(IrInst inst, out int target)
    {
        // Reflectively pick up a `Target` field for the conservative "value became unknown" path
        // in the caller rewrite, and for the folded-call target. Only used off the hot path.
        var prop = inst.GetType().GetProperty("Target");
        if (prop is not null && prop.PropertyType == typeof(int))
        {
            target = (int)prop.GetValue(inst)!;
            return true;
        }

        target = 0;
        return false;
    }

    private sealed class ScanState
    {
        public Dictionary<int, CtValue> Temps { get; } = new();

        public Dictionary<int, CtValue> Slots { get; } = new();

        public void Clear()
        {
            Temps.Clear();
            Slots.Clear();
        }
    }

    // The interpreter and its value domain.
    private sealed class EvalContext(
        Dictionary<string, IrFunction> functions,
        Dictionary<string, string> stringLiterals,
        HashSet<string> evaluable,
        bool traceExplain)
    {
        private readonly Dictionary<string, CtValue> _memo = new(StringComparer.Ordinal);
        private long _steps;

        public bool TryFoldCall(
            int target,
            string? calleeLabel,
            int argTemp,
            Dictionary<int, CtValue> knownTemps,
            SourceLocation? location,
            out IrInst? load,
            out CtValue? value)
        {
            load = null;
            value = null;

            if (calleeLabel is null
                || !evaluable.Contains(calleeLabel)
                || !knownTemps.TryGetValue(argTemp, out var argValue)
                || !functions.TryGetValue(calleeLabel, out var callee))
            {
                return false;
            }

            CtValue resultValue;
            try
            {
                _steps = StepBudget;
                resultValue = EvalFunction(callee, CtUnit.Instance, argValue, 0);
            }
            catch (CtBailException)
            {
                Explain($"did not evaluate {calleeLabel}(...): interpretation bailed (unmodeled instruction, budget, or runtime-only value)");
                return false;
            }

            load = resultValue switch
            {
                CtInt i => new IrInst.LoadConstInt(target, i.Value) { Location = location },
                CtBool b => new IrInst.LoadConstBool(target, b.Value) { Location = location },
                CtFloat f => new IrInst.LoadConstFloat(target, f.Value) { Location = location },
                _ => null,
            };

            if (load is null)
            {
                // Non-scalar result: embedding deferred to a later stage.
                Explain($"evaluated {calleeLabel}(...) but result is not a scalar; keeping runtime code");
                return false;
            }

            Explain($"evaluated {calleeLabel}(...) -> {resultValue}");
            value = resultValue;
            return true;
        }

        private CtValue EvalFunction(IrFunction fn, CtValue env, CtValue arg, int depth)
        {
            if (depth > DepthBudget || !fn.HasEnvAndArgParams)
            {
                throw new CtBailException();
            }

            string? memoKey = TryMemoKey(fn.Label, env, arg);
            if (memoKey is not null && _memo.TryGetValue(memoKey, out var cached))
            {
                return cached;
            }

            var frame = new Frame(fn, env, arg);
            int pc = 0;
            while (pc < fn.Instructions.Count)
            {
                Step();
                var inst = fn.Instructions[pc];
                if (inst is IrInst.Return ret)
                {
                    var value = frame.Read(ret.Source);
                    if (memoKey is not null) { _memo[memoKey] = value; }

                    return value;
                }

                pc = StepControlOrExec(inst, frame, depth, pc);
            }

            // Fell off the end without returning: unmodeled shape.
            throw new CtBailException();
        }

        // Executes control-flow and call instructions (which move the program counter or recurse)
        // and defers all pure value instructions to the frame. Returns the next program counter.
        private int StepControlOrExec(IrInst inst, Frame frame, int depth, int pc)
        {
            switch (inst)
            {
                case IrInst.Jump jmp:
                    return frame.LabelIndex[jmp.Target];
                case IrInst.JumpIfFalse jif:
                    return frame.ReadBool(jif.CondTemp) ? pc + 1 : frame.LabelIndex[jif.Target];
                case IrInst.SwitchTag sw:
                    return frame.LabelIndex[ResolveSwitch(sw, frame)];
                case IrInst.CallClosure cc:
                    frame.SetTemp(cc.Target, EvalCall(ClosureOf(frame.Read(cc.ClosureTemp)), frame.Read(cc.ArgTemp), depth));
                    return pc + 1;
                case IrInst.CallKnown ck:
                    frame.SetTemp(ck.Target, EvalCall(new CtClosure(ck.FuncLabel, frame.Read(ck.EnvTemp)), frame.Read(ck.ArgTemp), depth));
                    return pc + 1;
                default:
                    frame.Exec(inst, stringLiterals);
                    return pc + 1;
            }
        }

        private CtValue EvalCall(CtClosure closure, CtValue arg, int depth)
        {
            if (!evaluable.Contains(closure.FuncLabel) || !functions.TryGetValue(closure.FuncLabel, out var callee))
            {
                throw new CtBailException();
            }

            return EvalFunction(callee, closure.Env, arg, depth + 1);
        }

        private static string ResolveSwitch(IrInst.SwitchTag sw, Frame frame)
        {
            long tag = frame.ReadInt(sw.TagTemp);
            foreach (var (caseTag, caseLabel) in sw.Cases)
            {
                if (caseTag == tag)
                {
                    return caseLabel;
                }
            }

            return sw.DefaultLabel;
        }

        private static CtClosure ClosureOf(CtValue v) => v is CtClosure c ? c : throw new CtBailException();

        private void Step()
        {
            if (--_steps <= 0)
            {
                throw new CtBailException();
            }
        }

        private void Explain(string message)
        {
            if (traceExplain)
            {
                System.Console.Error.WriteLine("[compile-time] " + message);
            }
        }

        private static string? TryMemoKey(string label, CtValue env, CtValue arg)
        {
            string? e = ScalarKey(env);
            string? a = ScalarKey(arg);
            return e is not null && a is not null ? label + "|" + e + "|" + a : null;
        }

        private static string? ScalarKey(CtValue v) => v switch
        {
            CtUnit => "u",
            CtInt i => "i" + i.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
            CtBool b => "b" + (b.Value ? 1 : 0),
            CtFloat f => "f" + System.BitConverter.DoubleToInt64Bits(f.Value).ToString(System.Globalization.CultureInfo.InvariantCulture),
            CtStr s => "s" + s.Value.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + s.Value,
            _ => null,
        };
    }

    // One interpreter activation: local slots, temp registers, and a label -> index map.
    private sealed class Frame
    {
        private readonly CtValue?[] _temps;
        private readonly CtValue?[] _slots;

        public Frame(IrFunction fn, CtValue env, CtValue arg)
        {
            _temps = new CtValue?[fn.TempCount];
            _slots = new CtValue?[fn.LocalCount];
            _slots[0] = env;
            _slots[1] = arg;
            LabelIndex = BuildLabelIndex(fn);
        }

        public Dictionary<string, int> LabelIndex { get; }

        public CtValue Read(int t) => (uint)t < (uint)_temps.Length ? _temps[t] ?? throw new CtBailException() : throw new CtBailException();

        public long ReadInt(int t) => Read(t) is CtInt i ? i.Value : throw new CtBailException();

        public bool ReadBool(int t) => Read(t) is CtBool b ? b.Value : throw new CtBailException();

        public void SetTemp(int t, CtValue v) => Set(t, v);

        private void Set(int t, CtValue v) => _temps[t] = v;

        private double ReadFloat(int t) => Read(t) is CtFloat f ? f.Value : throw new CtBailException();

        private string ReadStr(int t) => Read(t) is CtStr s ? s.Value : throw new CtBailException();

        private static CtAdt ReadAdt(CtValue v) => v is CtAdt a ? a : throw new CtBailException();

        private static Dictionary<string, int> BuildLabelIndex(IrFunction fn)
        {
            var index = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < fn.Instructions.Count; i++)
            {
                if (fn.Instructions[i] is IrInst.Label lbl)
                {
                    index[lbl.Name] = i;
                }
            }

            return index;
        }

        public void Exec(IrInst inst, Dictionary<string, string> stringLiterals)
        {
            if (ExecMemoryAndConst(inst, stringLiterals) || ExecIntOps(inst) || ExecFloatOps(inst)
                || ExecCompares(inst) || ExecStringAndAdt(inst) || ExecNoOp(inst))
            {
                return;
            }

            throw new CtBailException();
        }

        private bool ExecMemoryAndConst(IrInst inst, Dictionary<string, string> stringLiterals)
        {
            switch (inst)
            {
                case IrInst.LoadConstInt lci: Set(lci.Target, new CtInt(lci.Value)); return true;
                case IrInst.LoadConstFloat lcf: Set(lcf.Target, new CtFloat(lcf.Value)); return true;
                case IrInst.LoadConstBool lcb: Set(lcb.Target, new CtBool(lcb.Value)); return true;
                case IrInst.LoadConstStr lcs: Set(lcs.Target, new CtStr(stringLiterals[lcs.StrLabel])); return true;
                case IrInst.LoadLocal ll: Set(ll.Target, _slots[ll.Slot] ?? throw new CtBailException()); return true;
                case IrInst.StoreLocal sl: _slots[sl.Slot] = Read(sl.Source); return true;
                case IrInst.Borrow b: Set(b.Target, Read(b.SourceTemp)); return true;
                case IrInst.RcDup d: Set(d.Target, Read(d.SourceTemp)); return true;
                case IrInst.MakeClosure mc:
                    Set(mc.Target, mc.EnvSizeBytes == 0 ? new CtClosure(mc.FuncLabel, Read(mc.EnvPtrTemp)) : throw new CtBailException());
                    return true;
                case IrInst.MakeClosureStack mcs:
                    Set(mcs.Target, mcs.EnvSizeBytes == 0 ? new CtClosure(mcs.FuncLabel, Read(mcs.EnvPtrTemp)) : throw new CtBailException());
                    return true;
                default:
                    return false;
            }
        }

        private bool ExecIntOps(IrInst inst)
        {
            switch (inst)
            {
                case IrInst.AddInt a: Set(a.Target, new CtInt(ReadInt(a.Left) + ReadInt(a.Right))); return true;
                case IrInst.SubInt s: Set(s.Target, new CtInt(ReadInt(s.Left) - ReadInt(s.Right))); return true;
                case IrInst.MulInt m: Set(m.Target, new CtInt(ReadInt(m.Left) * ReadInt(m.Right))); return true;
                case IrInst.DivInt d:
                    { long r = ReadInt(d.Right); Set(d.Target, new CtInt(r == 0 ? throw new CtBailException() : ReadInt(d.Left) / r)); return true; }
                case IrInst.DivUInt d:
                    { ulong r = (ulong)ReadInt(d.Right); Set(d.Target, new CtInt(r == 0 ? throw new CtBailException() : (long)((ulong)ReadInt(d.Left) / r))); return true; }
                case IrInst.AndInt a: Set(a.Target, new CtInt(ReadInt(a.Left) & ReadInt(a.Right))); return true;
                case IrInst.OrInt o: Set(o.Target, new CtInt(ReadInt(o.Left) | ReadInt(o.Right))); return true;
                case IrInst.XorInt x: Set(x.Target, new CtInt(ReadInt(x.Left) ^ ReadInt(x.Right))); return true;
                case IrInst.ShlInt s: Set(s.Target, new CtInt(ReadInt(s.Left) << (int)(ReadInt(s.Right) & 63))); return true;
                case IrInst.ShrInt s: Set(s.Target, new CtInt((long)((ulong)ReadInt(s.Left) >> (int)(ReadInt(s.Right) & 63)))); return true;
                default:
                    return false;
            }
        }

        private bool ExecFloatOps(IrInst inst)
        {
            switch (inst)
            {
                case IrInst.AddFloat a: Set(a.Target, new CtFloat(ReadFloat(a.Left) + ReadFloat(a.Right))); return true;
                case IrInst.SubFloat s: Set(s.Target, new CtFloat(ReadFloat(s.Left) - ReadFloat(s.Right))); return true;
                case IrInst.MulFloat m: Set(m.Target, new CtFloat(ReadFloat(m.Left) * ReadFloat(m.Right))); return true;
                case IrInst.DivFloat d: Set(d.Target, new CtFloat(ReadFloat(d.Left) / ReadFloat(d.Right))); return true;
                case IrInst.IntToFloat c: Set(c.Target, new CtFloat(ReadInt(c.ValueTemp))); return true;
                case IrInst.FloatToInt c: Set(c.Target, new CtInt((long)ReadFloat(c.ValueTemp))); return true;
                default:
                    return false;
            }
        }

        private bool ExecCompares(IrInst inst)
        {
            switch (inst)
            {
                case IrInst.CmpIntGt c: Set(c.Target, new CtBool(ReadInt(c.Left) > ReadInt(c.Right))); return true;
                case IrInst.CmpIntGe c: Set(c.Target, new CtBool(ReadInt(c.Left) >= ReadInt(c.Right))); return true;
                case IrInst.CmpIntLt c: Set(c.Target, new CtBool(ReadInt(c.Left) < ReadInt(c.Right))); return true;
                case IrInst.CmpIntLe c: Set(c.Target, new CtBool(ReadInt(c.Left) <= ReadInt(c.Right))); return true;
                case IrInst.CmpUIntGt c: Set(c.Target, new CtBool((ulong)ReadInt(c.Left) > (ulong)ReadInt(c.Right))); return true;
                case IrInst.CmpUIntGe c: Set(c.Target, new CtBool((ulong)ReadInt(c.Left) >= (ulong)ReadInt(c.Right))); return true;
                case IrInst.CmpUIntLt c: Set(c.Target, new CtBool((ulong)ReadInt(c.Left) < (ulong)ReadInt(c.Right))); return true;
                case IrInst.CmpUIntLe c: Set(c.Target, new CtBool((ulong)ReadInt(c.Left) <= (ulong)ReadInt(c.Right))); return true;
                case IrInst.CmpIntEq c: Set(c.Target, new CtBool(ReadInt(c.Left) == ReadInt(c.Right))); return true;
                case IrInst.CmpIntNe c: Set(c.Target, new CtBool(ReadInt(c.Left) != ReadInt(c.Right))); return true;
                default:
                    return ExecFloatCompares(inst);
            }
        }

        private bool ExecFloatCompares(IrInst inst)
        {
            switch (inst)
            {
                case IrInst.CmpFloatGt c: Set(c.Target, new CtBool(ReadFloat(c.Left) > ReadFloat(c.Right))); return true;
                case IrInst.CmpFloatGe c: Set(c.Target, new CtBool(ReadFloat(c.Left) >= ReadFloat(c.Right))); return true;
                case IrInst.CmpFloatLt c: Set(c.Target, new CtBool(ReadFloat(c.Left) < ReadFloat(c.Right))); return true;
                case IrInst.CmpFloatLe c: Set(c.Target, new CtBool(ReadFloat(c.Left) <= ReadFloat(c.Right))); return true;
#pragma warning disable S1244 // Intentional IEEE equality: mirrors the CmpFloatEq/Ne the program runs.
                case IrInst.CmpFloatEq c: Set(c.Target, new CtBool(ReadFloat(c.Left) == ReadFloat(c.Right))); return true;
                case IrInst.CmpFloatNe c: Set(c.Target, new CtBool(ReadFloat(c.Left) != ReadFloat(c.Right))); return true;
#pragma warning restore S1244
                default:
                    return false;
            }
        }

        private bool ExecStringAndAdt(IrInst inst)
        {
            switch (inst)
            {
                case IrInst.CmpStrEq c: Set(c.Target, new CtBool(string.Equals(ReadStr(c.Left), ReadStr(c.Right), StringComparison.Ordinal))); return true;
                case IrInst.CmpStrNe c: Set(c.Target, new CtBool(!string.Equals(ReadStr(c.Left), ReadStr(c.Right), StringComparison.Ordinal))); return true;
                case IrInst.ConcatStr { RuntimeManaged: false } c: Set(c.Target, new CtStr(ReadStr(c.Left) + ReadStr(c.Right))); return true;
                case IrInst.AllocAdt aa: Set(aa.Target, new CtAdt(aa.Tag, new CtValue?[aa.FieldCount])); return true;
                case IrInst.AllocAdtStack aa: Set(aa.Target, new CtAdt(aa.Tag, new CtValue?[aa.FieldCount])); return true;
                case IrInst.SetAdtField sf: ReadAdt(Read(sf.Ptr)).Fields[sf.FieldIndex] = Read(sf.Source); return true;
                case IrInst.GetAdtTag gt: Set(gt.Target, new CtInt(ReadAdt(Read(gt.Ptr)).Tag)); return true;
                case IrInst.GetAdtField gf: Set(gf.Target, ReadAdt(Read(gf.Ptr)).Fields[gf.FieldIndex] ?? throw new CtBailException()); return true;
                default:
                    return false;
            }
        }

        // Arena / stack bookkeeping, erased RC drops, and labels have no value semantics for evaluation.
        private static bool ExecNoOp(IrInst inst) => inst
            is IrInst.SaveArenaState or IrInst.RestoreArenaState or IrInst.ReclaimArenaChunks
            or IrInst.SaveStackPointer or IrInst.RestoreStackPointer or IrInst.RcDrop or IrInst.Label;
    }

    // Compile-time value domain.
    private abstract class CtValue;

    private sealed class CtUnit : CtValue
    {
        public static readonly CtUnit Instance = new();

        public override string ToString() => "()";
    }

    private sealed class CtInt(long value) : CtValue
    {
        public long Value { get; } = value;

        public override string ToString() => Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private sealed class CtFloat(double value) : CtValue
    {
        public double Value { get; } = value;

        public override string ToString() => Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private sealed class CtBool(bool value) : CtValue
    {
        public bool Value { get; } = value;

        public override string ToString() => Value ? "true" : "false";
    }

    private sealed class CtStr(string value) : CtValue
    {
        public string Value { get; } = value;

        public override string ToString() => "\"" + Value + "\"";
    }

    private sealed class CtClosure(string funcLabel, CtValue? env = null) : CtValue
    {
        public string FuncLabel { get; } = funcLabel;

        public CtValue Env { get; } = env ?? CtUnit.Instance;

        public override string ToString() => "closure(" + FuncLabel + ")";
    }

    private sealed class CtAdt(long tag, CtValue?[] fields) : CtValue
    {
        public long Tag { get; } = tag;

        public CtValue?[] Fields { get; } = fields;

        public override string ToString() => "adt#" + Tag.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private sealed class CtBailException : System.Exception;
}
