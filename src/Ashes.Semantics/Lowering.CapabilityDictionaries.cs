using Ashes.Frontend;

namespace Ashes.Semantics;

// Dictionary passing for *generic* capability requirements.
//
// A `provide` resolves a capability operation at a concrete instance (Phase 3, static). When the
// operation is used at a *generic* instance — inside a function annotated `needs {Ord(a)}` where `a`
// is a type variable — there is nothing concrete to resolve against at the definition, and inlining
// cannot reach recursive or higher-order shapes. Dictionary passing solves this generally: each
// operation of a parameterized needed capability becomes a hidden *parameter* of the function (an
// "unbundled dictionary"), the operation calls in the body become references to that parameter, and
// each call site supplies the implementations — from a provider (concrete instance) or by threading
// the caller's own op-parameter (still-abstract instance). Because the operation becomes a runtime
// value, a closure captures it like any other, so the higher-order case (a comparator handed to
// `foldLeft`) works with no compile-time ordering constraint.
//
// The transform here prepends the parameters and rewrites the operation references; the call sites
// are handled at lowering (LowerDictionaryFunctionCall), where the concrete instance is known.
public sealed partial class Lowering
{
    /// <summary>A generic function compiled to take unbundled-dictionary parameters: its needed parameterized-capability operations, in call order.</summary>
    private sealed record DictFnInfo(IReadOnlyList<(CapabilitySymbol Cap, string Op)> Ops);

    // Functions (by name) that take leading operation parameters, and the ordered operations.
    private readonly Dictionary<string, DictFnInfo> _dictFunctions = new(StringComparer.Ordinal);

    // Operation parameters in scope while lowering a dictionary function's body: (capability, op) ->
    // the parameter name to thread into a call at a still-abstract instance.
    private readonly Dictionary<(string Cap, string Op), string> _activeOpParams = new();

    // Every synthesized op-parameter name -> the (capability, op) it carries, so LowerLambda can
    // register it as active without re-parsing the name.
    private readonly Dictionary<string, (string Cap, string Op)> _opParamMeta = new(StringComparer.Ordinal);

    // Dictionary-function names currently shadowed by a local binder (a lambda parameter or nested
    // let of the same name): a call to that name is the local, not the top-level dictionary function.
    private readonly HashSet<string> _shadowedDictFns = new(StringComparer.Ordinal);

    /// <summary>Marks <paramref name="name"/> as a shadowing local binder if it collides with a dictionary function; returns whether a shadow was pushed (to pop later). The self-name of the function's own definition does not count as a shadow.</summary>
    private bool PushDictFnShadow(string name, string? selfName = null)
    {
        if (_dictFunctions.ContainsKey(name) && !string.Equals(name, selfName, StringComparison.Ordinal) && _shadowedDictFns.Add(name))
        {
            return true;
        }

        return false;
    }

    private void PopDictFnShadow(string name, bool pushed)
    {
        if (pushed)
        {
            _shadowedDictFns.Remove(name);
        }
    }

    /// <summary>If <paramref name="paramName"/> is a capability op-parameter, marks it active for threading and returns its key to restore later.</summary>
    private (string Cap, string Op)? EnterOpParamScope(string paramName)
    {
        if (!_opParamMeta.TryGetValue(paramName, out var key))
        {
            return null;
        }

        _activeOpParams[key] = paramName;
        return key;
    }

    private void ExitOpParamScope((string Cap, string Op)? key)
    {
        if (key is { } k)
        {
            _activeOpParams.Remove(k);
        }
    }

    /// <summary>The hidden parameter name for a capability operation: <c>__cap_Ord_compare</c>.</summary>
    private static string OpParamName(string capability, string op) => $"__cap_{capability}_{op}";

    /// <summary>The parameterized capabilities named in a function's `needs` annotation, in row order (unparameterized ones such as Clock stay dynamic and are skipped).</summary>
    private List<CapabilitySymbol> ExtractParameterizedNeeds(TypeExpr? annotation)
    {
        var result = new List<CapabilitySymbol>();
        if (annotation is null)
        {
            return result;
        }

        void Visit(TypeExpr? t)
        {
            if (t is not TypeExpr.Arrow arrow)
            {
                return;
            }

            if (arrow.Needs is { } needs)
            {
                foreach (var capRef in needs.Capabilities)
                {
                    if (capRef.Args.Count > 0
                        && _capabilitySymbols.TryGetValue(capRef.Name, out var sym)
                        && sym.TypeParameters.Count > 0
                        && result.All(r => !string.Equals(r.Name, sym.Name, StringComparison.Ordinal)))
                    {
                        result.Add(sym);
                    }
                }
            }

            Visit(arrow.From);
            Visit(arrow.To);
        }

        Visit(annotation);
        return result;
    }

    /// <summary>
    /// Registers and transforms every generic (parameterized-`needs`) top-level and entry-body
    /// function into dictionary-passing form, returning the rewritten program. Must run after
    /// capability/provider registration and before value collection and desugaring.
    /// </summary>
    private Program RegisterAndTransformDictionaryFunctions(Program program)
    {
        foreach (var item in program.Items)
        {
            if (item is TopLevelItem.LetDecl let)
            {
                RegisterDictFn(let.Name, let.TypeAnnotation);
            }
        }

        var bodyCursor = program.Body;
        while (bodyCursor is Expr.Let or Expr.LetRecursive)
        {
            switch (bodyCursor)
            {
                case Expr.Let l:
                    RegisterDictFn(l.Name, l.TypeAnnotation);
                    bodyCursor = l.Body;
                    break;
                case Expr.LetRecursive lr:
                    RegisterDictFn(lr.Name, lr.TypeAnnotation);
                    bodyCursor = lr.Body;
                    break;
            }
        }

        if (_dictFunctions.Count == 0)
        {
            return program;
        }

        var newItems = new List<TopLevelItem>(program.Items.Count);
        foreach (var item in program.Items)
        {
            newItems.Add(item is TopLevelItem.LetDecl let ? TransformLetItem(let) : item);
        }

        return program with { Items = newItems, Body = TransformBody(program.Body) };
    }

    private void RegisterDictFn(string name, TypeExpr? annotation)
    {
        var caps = ExtractParameterizedNeeds(annotation);
        if (caps.Count == 0)
        {
            return;
        }

        var ops = new List<(CapabilitySymbol, string)>();
        foreach (var cap in caps)
        {
            foreach (var op in cap.DeclaringSyntax.Operations)
            {
                ops.Add((cap, op.Name));
                _opParamMeta[OpParamName(cap.Name, op.Name)] = (cap.Name, op.Name);
            }
        }

        _dictFunctions[name] = new DictFnInfo(ops);
    }

    private TopLevelItem.LetDecl TransformLetItem(TopLevelItem.LetDecl let)
    {
        if (!_dictFunctions.TryGetValue(let.Name, out var info))
        {
            return let;
        }

        // Drop the `needs` annotation: the requirement is now compiled into parameters, so the
        // annotation no longer matches the transformed value's shape.
        return let with { Value = TransformDictFnValue(let.Value, info, let.Name, let.IsRecursive), TypeAnnotation = null };
    }

    private Expr TransformBody(Expr body)
    {
        switch (body)
        {
            case Expr.Let l when _dictFunctions.TryGetValue(l.Name, out var info):
                return new Expr.Let(l.Name, TransformDictFnValue(l.Value, info, l.Name, isRecursive: false), TransformBody(l.Body));
            case Expr.Let l:
                return new Expr.Let(l.Name, l.Value, TransformBody(l.Body)) { TypeAnnotation = l.TypeAnnotation };
            case Expr.LetRecursive lr when _dictFunctions.TryGetValue(lr.Name, out var info):
                return new Expr.LetRecursive(lr.Name, TransformDictFnValue(lr.Value, info, lr.Name, isRecursive: true), TransformBody(lr.Body));
            case Expr.LetRecursive lr:
                return new Expr.LetRecursive(lr.Name, lr.Value, TransformBody(lr.Body)) { TypeAnnotation = lr.TypeAnnotation };
            default:
                return body;
        }
    }

    /// <summary>Prepends one lambda parameter per needed operation and rewrites the body's operation references to those parameters.</summary>
    private Expr TransformDictFnValue(Expr value, DictFnInfo info, string selfName, bool isRecursive)
    {
        var needed = info.Ops.Select(o => (o.Cap.Name, o.Op)).ToHashSet();
        var body = RewriteCapabilityOps(value, needed);

        // Thread calls to dictionary functions (this one, recursively, and any sibling the enclosing
        // function can fully satisfy) *syntactically*, so the op-parameter reference appears in the
        // AST and is captured into any nested closure. Doing this at lowering instead would miss the
        // free-variable capture and would loop on a self-call whose type is still being inferred.
        body = RewriteDictFnCalls(body, info);

        for (int i = info.Ops.Count - 1; i >= 0; i--)
        {
            var (cap, op) = info.Ops[i];
            body = new Expr.Lambda(OpParamName(cap.Name, op), body);
        }

        return body;
    }

    /// <summary>
    /// Inside a dictionary function's body, replaces each reference to a dictionary function
    /// <c>Var(g)</c> with <c>g</c> applied to the op-parameters threaded from the enclosing function —
    /// provided the enclosing function carries every operation <c>g</c> needs. (When it does not, the
    /// call is left for the external, provider-resolving path.)
    /// </summary>
    private Expr RewriteDictFnCalls(Expr expr, DictFnInfo enclosing)
    {
        var have = enclosing.Ops.Select(o => (o.Cap.Name, o.Op)).ToHashSet();

        Expr Rewrite(Expr e)
        {
            if (e is Expr.Var v && _dictFunctions.TryGetValue(v.Name, out var callee)
                && callee.Ops.All(o => have.Contains((o.Cap.Name, o.Op))))
            {
                Expr applied = v;
                foreach (var (cap, op) in callee.Ops)
                {
                    applied = new Expr.Call(applied, new Expr.Var(OpParamName(cap.Name, op)));
                }

                return applied;
            }

            // A local binder shadowing a dictionary-function name refers to the local, not the
            // dictionary function: do not descend into its scope for that name.
            if (e is Expr.Lambda lam && _dictFunctions.ContainsKey(lam.ParamName))
            {
                return e;
            }

            return MapChildExpressions(e, Rewrite);
        }

        return Rewrite(expr);
    }

    /// <summary>
    /// Lowers a saturated-or-partial call to a dictionary function: supplies the leading operation
    /// arguments (from a provider at a concrete instance, or by threading an enclosing op-parameter at
    /// a still-abstract instance) ahead of the real arguments. Requires at least one real argument so
    /// the instance is pinned by unification.
    /// </summary>
    private (int, TypeRef) LowerDictionaryFunctionCall(DictFnInfo info, Expr fnExpr, string fnName, List<Expr> realArgs, TextSpan span)
    {
        var (fnTemp, fnTypeRaw) = LowerExpr(fnExpr);

        // Peel one arrow per op-parameter to expose the (body-shared) op-parameter types.
        var opTypes = new List<TypeRef>(info.Ops.Count);
        var cursor = fnTypeRaw;
        for (int i = 0; i < info.Ops.Count; i++)
        {
            cursor = Prune(cursor);
            if (cursor is TypeRef.TVar)
            {
                Unify(cursor, new TypeRef.TFun(NewTypeVar(), NewTypeVar()));
                cursor = Prune(cursor);
            }

            if (cursor is not TypeRef.TFun fun)
            {
                ReportDiagnostic(span, $"'{fnName}' is not applied as a capability-generic function.", UnknownCapabilityCode);
                return ReturnNeverWithDummyTemp();
            }

            SubsumeCalleeRow(fun.Row, span);
            opTypes.Add(fun.Arg);
            cursor = fun.Ret;
        }

        // Lower the real arguments, unifying to pin the instance type variables shared with opTypes.
        var realTemps = new List<int>(realArgs.Count);
        var currentType = cursor;
        foreach (var arg in realArgs)
        {
            var (argTemp, argType) = LowerExpr(arg);
            realTemps.Add(argTemp);
            currentType = Prune(currentType);
            if (currentType is TypeRef.TNever)
            {
                return ReturnNeverWithDummyTemp();
            }

            if (currentType is TypeRef.TVar)
            {
                Unify(currentType, new TypeRef.TFun(NewTypeVar(), NewTypeVar()));
                currentType = Prune(currentType);
            }

            if (currentType is not TypeRef.TFun fun)
            {
                ReportDiagnostic(span, $"'{fnName}' applied to too many arguments.", UnknownCapabilityCode);
                return ReturnNeverWithDummyTemp();
            }

            Unify(fun.Arg, argType);
            SubsumeCalleeRow(fun.Row, span);
            currentType = Prune(fun.Ret);
        }

        // Synthesize each op-argument now that the instance is pinned, then apply the function.
        int applied = fnTemp;
        for (int i = 0; i < info.Ops.Count; i++)
        {
            int opTemp = SynthesizeOpArg(info.Ops[i].Cap, info.Ops[i].Op, opTypes[i], span);
            int next = NewTemp();
            Emit(new IrInst.CallClosure(next, applied, opTemp));
            applied = next;
        }

        foreach (var realTemp in realTemps)
        {
            int next = NewTemp();
            Emit(new IrInst.CallClosure(next, applied, realTemp));
            applied = next;
        }

        return (applied, currentType);
    }

    /// <summary>Produces the implementation value for a capability operation at the instance pinned in <paramref name="opType"/>: a provider (concrete) or a threaded op-parameter (abstract).</summary>
    private int SynthesizeOpArg(CapabilitySymbol cap, string op, TypeRef opType, TextSpan span)
    {
        // Recover the concrete instance by matching the operation signature against the pinned type.
        var freshArgs = cap.TypeParameters.Select(_ => (TypeRef)NewTypeVar()).ToList();
        var operation = cap.Operations[op];
        if (operation.DeclaredSignature is not null)
        {
            // Match only the type structure to recover the instance arguments — not the effect rows.
            // The op-parameter's arrows may have picked up the caller's ambient effects (a pure
            // operation called in an effectful body), which a provider's pure impl need not carry.
            var sig = InstantiateEffectSignature(operation.DeclaredSignature, cap.TypeParameters, freshArgs);
            UnifyIgnoringRows(sig, opType);
        }

        var instance = freshArgs.Select(Prune).ToList();
        bool concrete = instance.All(t => t is not (TypeRef.TVar or TypeRef.TTypeParam));

        if (concrete)
        {
            var provider = ResolveProvider(cap, instance);
            if (provider is null)
            {
                ReportDiagnostic(span, $"No provider for '{BuildProviderKey(cap.Name, instance)}' to satisfy a generic '{cap.Name}' requirement.", UnhandledCapabilityCode);
                return EmitDummyTemp();
            }

            var (implTemp, _) = LowerExpr(provider.Operations[op]);
            return implTemp;
        }

        // Abstract instance: thread the enclosing function's own op-parameter.
        if (_activeOpParams.TryGetValue((cap.Name, op), out var paramName))
        {
            var (paramTemp, _) = LowerExpr(new Expr.Var(paramName));
            return paramTemp;
        }

        ReportDiagnostic(span, $"Capability '{cap.Name}' is required at a generic type here, but nothing supplies it (no provider, and no enclosing capability parameter to thread).", CapabilityNotPermittedCode);
        return EmitDummyTemp();
    }

    /// <summary>Unifies two types by structure while leaving arrow effect rows free — used to recover a capability instance from an op-parameter type polluted by the caller's ambient effects.</summary>
    private void UnifyIgnoringRows(TypeRef a, TypeRef b)
    {
        a = Prune(a);
        b = Prune(b);
        if (a is TypeRef.TFun fa && b is TypeRef.TFun fb)
        {
            UnifyIgnoringRows(fa.Arg, fb.Arg);
            UnifyIgnoringRows(fa.Ret, fb.Ret);
            return;
        }

        Unify(a, b);
    }

    private int EmitDummyTemp()
    {
        int t = NewTemp();
        Emit(new IrInst.LoadConstInt(t, 0));
        return t;
    }

    /// <summary>Replaces every <c>Cap.op</c> reference of a needed capability with its op-parameter variable, descending through the whole body (including nested closures).</summary>
    private Expr RewriteCapabilityOps(Expr expr, HashSet<(string, string)> needed)
    {
        // A qualified name can never be shadowed by a local binder, so a plain recursive descent is
        // safe (no scope tracking needed).
        if (expr is Expr.QualifiedVar qv && needed.Contains((qv.Module, qv.Name)))
        {
            return new Expr.Var(OpParamName(qv.Module, qv.Name));
        }

        return MapChildExpressions(expr, e => RewriteCapabilityOps(e, needed));
    }

    /// <summary>Rebuilds an expression with each immediate child expression replaced by <paramref name="f"/> applied to it. Leaves (literals, variables) are returned unchanged.</summary>
    private static Expr MapChildExpressions(Expr e, Func<Expr, Expr> f)
    {
        switch (e)
        {
            case Expr.IntLit or Expr.UIntLit or Expr.FloatLit or Expr.StrLit or Expr.BoolLit
                or Expr.Var or Expr.QualifiedVar:
                return e;
            case Expr.Add x: return new Expr.Add(f(x.Left), f(x.Right));
            case Expr.Subtract x: return new Expr.Subtract(f(x.Left), f(x.Right));
            case Expr.Multiply x: return new Expr.Multiply(f(x.Left), f(x.Right));
            case Expr.Divide x: return new Expr.Divide(f(x.Left), f(x.Right));
            case Expr.BitwiseAnd x: return new Expr.BitwiseAnd(f(x.Left), f(x.Right));
            case Expr.BitwiseOr x: return new Expr.BitwiseOr(f(x.Left), f(x.Right));
            case Expr.BitwiseXor x: return new Expr.BitwiseXor(f(x.Left), f(x.Right));
            case Expr.ShiftLeft x: return new Expr.ShiftLeft(f(x.Left), f(x.Right));
            case Expr.ShiftRight x: return new Expr.ShiftRight(f(x.Left), f(x.Right));
            case Expr.BitwiseNot x: return new Expr.BitwiseNot(f(x.Operand));
            case Expr.GreaterThan x: return new Expr.GreaterThan(f(x.Left), f(x.Right));
            case Expr.LessThan x: return new Expr.LessThan(f(x.Left), f(x.Right));
            case Expr.GreaterOrEqual x: return new Expr.GreaterOrEqual(f(x.Left), f(x.Right));
            case Expr.LessOrEqual x: return new Expr.LessOrEqual(f(x.Left), f(x.Right));
            case Expr.Equal x: return new Expr.Equal(f(x.Left), f(x.Right));
            case Expr.NotEqual x: return new Expr.NotEqual(f(x.Left), f(x.Right));
            case Expr.ResultPipe x: return new Expr.ResultPipe(f(x.Left), f(x.Right));
            case Expr.ResultMapErrorPipe x: return new Expr.ResultMapErrorPipe(f(x.Left), f(x.Right));
            case Expr.Cons x: return new Expr.Cons(f(x.Head), f(x.Tail));
            case Expr.If x: return new Expr.If(f(x.Cond), f(x.Then), f(x.Else));
            case Expr.Await x: return new Expr.Await(f(x.Task));
            case Expr.Perform x: return new Expr.Perform(f(x.Operation));
            case Expr.Call x: return new Expr.Call(f(x.Func), f(x.Arg)) { IsWhitespaceApplication = x.IsWhitespaceApplication };
            case Expr.Lambda x: return new Expr.Lambda(x.ParamName, f(x.Body));
            case Expr.Let x: return new Expr.Let(x.Name, f(x.Value), f(x.Body)) { TypeAnnotation = x.TypeAnnotation };
            case Expr.LetResult x: return new Expr.LetResult(x.Name, f(x.Value), f(x.Body));
            case Expr.LetRecursive x: return new Expr.LetRecursive(x.Name, f(x.Value), f(x.Body)) { TypeAnnotation = x.TypeAnnotation };
            case Expr.TupleLit x: return new Expr.TupleLit(x.Elements.Select(f).ToList());
            case Expr.ListLit x: return new Expr.ListLit(x.Elements.Select(f).ToList());
            case Expr.RecordLit x: return new Expr.RecordLit(x.TypeName, x.Fields.Select(fld => (fld.Name, f(fld.Value))).ToList());
            case Expr.RecordUpdate x: return new Expr.RecordUpdate(f(x.Target), x.Updates.Select(u => (u.Name, f(u.Value))).ToList());
            case Expr.Match x:
                return new Expr.Match(
                    f(x.Value),
                    x.Cases.Select(mc => new MatchCase(mc.Pattern, f(mc.Body), mc.Guard is null ? null : f(mc.Guard))).ToList(),
                    x.Pos);
            case Expr.Handle x:
                return new Expr.Handle(
                    f(x.Body),
                    x.Arms.Select(a => new HandlerArm(a.CapabilityName, a.OperationName, a.Parameters, f(a.Body))).ToList());
            default:
                throw new NotSupportedException($"MapChildExpressions: unhandled {e.GetType().Name}");
        }
    }
}
