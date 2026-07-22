using System.Diagnostics;
using Ashes.Frontend;

namespace Ashes.Semantics;

public sealed partial class Lowering
{
    // ---------------- scopes / helpers ----------------

    private Binding? Lookup(string name)
    {
        return _scopes.Peek().TryGetValue(name, out var b) ? b : null;
    }

    // --- Resource tracking helpers ---

    /// <summary>
    /// Returns true if the given pruned type is a resource type requiring deterministic cleanup.
    /// </summary>
    private static bool IsResourceType(TypeRef prunedType)
    {
        return prunedType is TypeRef.TNamedType named && BuiltinRegistry.IsResourceTypeName(named.Symbol.Name);
    }

    /// <summary>
    /// Returns the owned type name if the type is an owned type (heap-allocated),
    /// otherwise null. Copy types (Int, Float, Bool) return null.
    /// </summary>
    private static string? GetOwnedTypeName(TypeRef prunedType)
    {
        return prunedType switch
        {
            TypeRef.TStr => "String",
            TypeRef.TBytes => "Bytes",
            TypeRef.TList => "List",
            TypeRef.TTuple => "Tuple",
            TypeRef.TFun => "Function",
            TypeRef.TNamedType named => named.Symbol.Name,
            _ => null // Copy types (Int, Float, Bool), TNever, TVar, TTypeParam
        };
    }

    /// <summary>
    /// Returns the resource type name if the type is a resource type, otherwise null.
    /// Resource types are a subset of owned types with special cleanup behavior.
    /// </summary>
    private static string? GetResourceTypeName(TypeRef prunedType)
    {
        return prunedType is TypeRef.TNamedType named && BuiltinRegistry.IsResourceTypeName(named.Symbol.Name)
            ? named.Symbol.Name
            : null;
    }

    /// <summary>
    /// Returns true if the type is, or transitively contains, a resource type that needs
    /// deterministic cleanup — e.g. <c>Result(Str, FileHandle)</c>, <c>Maybe(Socket)</c>,
    /// <c>(FileHandle, Int)</c>, <c>List(Socket)</c>. Closures (<see cref="TypeRef.TFun"/>) are
    /// excluded: a resource captured by a closure is handled by the escape logic (Gap B), not by
    /// dropping the closure value. Recursion is cycle-guarded for recursive ADTs.
    /// </summary>
    private bool IsResourceBearing(TypeRef type) => IsResourceBearing(type, new HashSet<string>(StringComparer.Ordinal));

    private bool IsResourceBearing(TypeRef type, HashSet<string> visiting)
    {
        var pruned = Prune(type);
        switch (pruned)
        {
            case TypeRef.TNamedType named when BuiltinRegistry.IsResourceTypeName(named.Symbol.Name):
                return true;

            case TypeRef.TNamedType named:
                {
                    var key = Pretty(named);
                    if (!visiting.Add(key))
                    {
                        return false; // recursive ADT cycle — no resource found on this path
                    }

                    Dictionary<TypeParameterSymbol, TypeRef>? typeParamMap = null;
                    if (named.Symbol.TypeParameters.Count > 0 && named.TypeArgs.Count == named.Symbol.TypeParameters.Count)
                    {
                        typeParamMap = new Dictionary<TypeParameterSymbol, TypeRef>();
                        for (int i = 0; i < named.Symbol.TypeParameters.Count; i++)
                        {
                            typeParamMap[named.Symbol.TypeParameters[i]] = named.TypeArgs[i];
                        }
                    }

                    bool found = false;
                    foreach (var ctor in named.Symbol.Constructors)
                    {
                        for (int j = 0; j < ctor.Arity && !found; j++)
                        {
                            if (IsResourceBearing(ResolveFieldType(ctor.ParameterTypes[j], typeParamMap), visiting))
                            {
                                found = true;
                            }
                        }
                    }

                    visiting.Remove(key);
                    return found;
                }

            case TypeRef.TTuple tuple:
                return tuple.Elements.Any(e => IsResourceBearing(e, visiting));

            case TypeRef.TList list:
                return IsResourceBearing(list.Element, visiting);

            default:
                return false;
        }
    }

    /// <summary>
    /// When a resource (or resource-bearing) binding is stored by name into an aggregate
    /// (constructor field, tuple element, list cell), its ownership moves into that aggregate:
    /// mark it consumed so its own scope no longer drops it. Otherwise the resource would be closed
    /// twice (once directly, once via the aggregate's recursive Drop) or — if the aggregate
    /// escapes — closed before the aggregate's user reads it (an aggregate analog of the Gap B
    /// closure escape). The aggregate now carries the cleanup, via recursive Drop or by transferring
    /// to its own consumer.
    /// </summary>
    private void MarkResourceArgMoved(Expr arg)
    {
        if (arg is Expr.Var v
            && LookupOwnedValue(v.Name) is { IsDropped: false } info
            && (info.IsResource || info.IsResourceBearing))
        {
            info.ReleaseKind = ResourceReleaseKind.Moved;
        }
    }

    /// <summary>
    /// Resolves an ownership alias chain to the original owner name.
    /// If the name is not an alias, returns itself.
    /// </summary>
    private string ResolveOwnershipAlias(string name)
    {
        while (_ownershipAliases.TryGetValue(name, out var target))
        {
            name = target;
        }

        return name;
    }

    /// <summary>
    /// Registers an owned binding in the current ownership scope.
    /// Called when a let binding or pattern binding creates an owned-type variable.
    /// </summary>
    private void TrackOwnedValue(string name, int slot, string typeName, bool isResource, TextSpan? definitionSpan, TypeRef? type = null)
    {
        if (_ownershipScopes.Count > 0)
        {
            // A direct resource is not also "resource-bearing"; only aggregates that nest a resource
            // need the recursive walk at drop time.
            bool isResourceBearing = !isResource && type is not null && IsResourceBearing(type);
            _ownershipScopes.Peek()[name] = new OwnershipInfo(slot, typeName, isResource, definitionSpan, type is null ? null : Prune(type), isResourceBearing);
        }
    }

    /// <summary>
    /// Looks up an owned binding across all ownership scopes.
    /// Resolves ownership aliases so that accessing an alias (e.g. y when `let y = x`)
    /// returns the original owner's info.
    /// </summary>
    private OwnershipInfo? LookupOwnedValue(string name)
    {
        var resolved = ResolveOwnershipAlias(name);
        foreach (var scope in _ownershipScopes)
        {
            if (scope.TryGetValue(resolved, out var info))
            {
                return info;
            }
        }

        return null;
    }

    /// <summary>
    /// Marks an owned value as dropped (explicitly closed / released).
    /// Resolves aliases so that closing an alias marks the original owner as dropped.
    /// Returns true if the operation succeeded (value was alive and is now marked dropped)
    /// or if the name is not a tracked owned value (no-op — safe to call on any binding).
    /// Returns false if the value was already dropped (double-drop detected).
    /// </summary>
    private bool TryMarkDropped(string name)
    {
        var info = LookupOwnedValue(name); // already resolves aliases
        if (info is null)
        {
            return true; // not a tracked owned value — no action needed, returns true to indicate no error
        }

        if (info.IsDropped)
        {
            return false; // already dropped — double-drop
        }

        info.ReleaseKind = ResourceReleaseKind.Closed;
        return true;
    }

    /// <summary>
    /// If the scope's result is a closure that captures resources this scope owns, those resources
    /// escape with the closure. Mark them as already-handled so <see cref="EmitDropsForCurrentScope"/>
    /// does not close them here — closing a resource the escaping closure still references would be a
    /// use-after-close at scope exit — and attach a deterministic dropper (closure+24) that closes
    /// them when the closure itself dies.
    ///
    /// This handles the direct-result-closure case with deterministic close. Second-order escapes —
    /// a resource reached only through an aggregate holding a closure, or a chain of closures — are
    /// handled soundly (but without the deterministic dropper) by
    /// <see cref="EmitDropsForCurrentScope"/> via <c>OwnershipInfo.CapturedByClosure</c>: any
    /// closure-captured resource not recognised here has its close skipped and ownership transferred
    /// to the closure, so it is never closed underneath a value that still references it.
    /// </summary>
    private void SkipDropsForResourcesEscapingViaResult(int resultTemp)
    {
        if (resultTemp < 0
            || _ownershipScopes.Count == 0
            || !_closureResourceCaptures.TryGetValue(resultTemp, out var captures))
        {
            return;
        }

        var scope = _ownershipScopes.Peek();
        var escaping = new List<(int EnvOffset, TypeRef Type)>();
        foreach (var (envOffset, name, type) in captures)
        {
            if (scope.TryGetValue(name, out var info) && info.IsResource && !info.IsDropped)
            {
                // The resource is owned by this exiting scope and captured by its escaping result
                // closure, so after this scope it is reachable only through the closure: move it in.
                info.ReleaseKind = ResourceReleaseKind.Moved;
                escaping.Add((envOffset, type));
            }
        }

        if (escaping.Count == 0)
        {
            return;
        }

        // Attach a dropper to the escaping closure that closes the moved resources when the closure
        // is itself cleaned up (CleanupResource "Function" invokes closure+24). This gives the escaped resource
        // a deterministic close at the closure's death rather than leaking it to program exit.
        string dropperLabel = SynthesizeClosureResourceDropper(escaping);
        int dropperCodeTemp = NewTemp();
        Emit(new IrInst.LoadFuncAddr(dropperCodeTemp, dropperLabel));
        Emit(new IrInst.StoreMemOffset(resultTemp, 24, dropperCodeTemp));
    }

    /// <summary>
    /// Emits Drop instructions for every alive resource in the ownership scopes pushed since the
    /// TCO loop body started (those above <see cref="TcoContext.OwnershipDepthAtEntry"/>). Called at
    /// the tail-call back-edge so iteration-local resources are closed before the jump back, instead
    /// of leaking via the per-arm Drop that the jump turns into dead code. Resources moved into the
    /// next iteration (passed as a self-call argument) are marked dropped by the caller and skipped.
    /// Accumulators are loop parameters, not ownership-scope entries, so they are unaffected.
    /// </summary>
    private void EmitTcoBackEdgeResourceDrops(TcoContext tco)
    {
        if (tco.OwnershipDepthAtEntry < 0)
        {
            return;
        }

        int scopesAboveEntry = _ownershipScopes.Count - tco.OwnershipDepthAtEntry;
        int index = 0;
        foreach (var scope in _ownershipScopes) // top-to-bottom
        {
            if (index >= scopesAboveEntry)
            {
                break;
            }

            foreach (var (_, info) in scope)
            {
                // Resources/resource-bearing aggregates close their handles; closures ("Function")
                // may carry a resource dropper (closure+24) set when they captured-and-escaped a
                // resource (Gap B), so drop them too — it's a cheap no-op for ordinary closures.
                if ((info.IsResource || info.IsResourceBearing || string.Equals(info.TypeName, "Function", StringComparison.Ordinal)) && !info.IsDropped)
                {
                    EmitOwnedValueDrop(info);
                    info.ReleaseKind = ResourceReleaseKind.AutoDropped;
                }
            }

            index++;
        }
    }

    /// <summary>
    /// Emits the drop for a single owned value: a type-directed recursive walk closing nested
    /// resources for a resource-bearing aggregate (Result(_, FileHandle), tuples/lists of resources,
    /// …), <see cref="IrInst.CleanupResource"/> for a direct resource or possibly-resource-owning
    /// closure, or <see cref="IrInst.RcDrop"/> for an ordinary heap value reclaimed by the arena.
    /// </summary>
    private void EmitOwnedValueDrop(OwnershipInfo info)
    {
        int loadTemp = NewTemp();
        Emit(new IrInst.LoadLocal(loadTemp, info.Slot));
        if (info.IsResourceBearing && info.Type is not null)
        {
            EmitResourceBearingDrop(loadTemp, info.Type);
            // The recursive walk above handles deterministic cleanup of nested resources. The
            // aggregate cell also has an ordinary heap lifetime, represented separately so later
            // RC insertion can reclaim the container without conflating it with resource closing.
            Emit(new IrInst.RcDrop(loadTemp, info.TypeName, info.Slot));
        }
        else if (info.IsResource || string.Equals(info.TypeName, "Function", StringComparison.Ordinal))
        {
            Emit(new IrInst.CleanupResource(loadTemp, info.TypeName));
        }
        else
        {
            Emit(new IrInst.RcDrop(loadTemp, info.TypeName, info.Slot));
        }
    }

    /// <summary>
    /// Walks <paramref name="temp"/> (of <paramref name="type"/>) and closes every resource nested
    /// in it. Handles a direct resource (CleanupResource), an ADT (tag switch → clean up resource-bearing fields of
    /// the live constructor), a tuple (drop resource-bearing elements), and a list (loop, drop each
    /// resource-bearing element). A self-recursive resource-bearing ADT (one that nests both itself
    /// and a resource) is walked at runtime by a synthesized recursive dropper
    /// (<see cref="EmitRecursiveAdtResourceDrop"/>) rather than a static unfold. Only a mutual-recursion
    /// cycle between distinct ADT types still bottoms out on the inline cycle guard.
    /// </summary>
    private void EmitResourceBearingDrop(int temp, TypeRef type)
        => EmitResourceBearingDrop(temp, type, new HashSet<string>(StringComparer.Ordinal));

    private void EmitResourceBearingDrop(int temp, TypeRef type, HashSet<string> visiting)
    {
        var pruned = Prune(type);
        switch (pruned)
        {
            case TypeRef.TNamedType named when BuiltinRegistry.IsResourceTypeName(named.Symbol.Name):
                Emit(new IrInst.CleanupResource(temp, named.Symbol.Name));
                return;

            case TypeRef.TNamedType named when IsSelfRecursiveResourceBearingAdt(named):
                // A type that nests both itself and a resource (e.g. Bag = Mt | Put(FileHandle, Bag)):
                // a static unfold would not terminate, so walk it at runtime via a synthesized
                // recursive dropper instead of the visiting-guarded inline unfold (which leaked the
                // tail to program exit).
                EmitRecursiveAdtResourceDrop(named, temp);
                return;

            case TypeRef.TNamedType named:
                EmitAdtResourceDrop(temp, named, visiting);
                return;

            case TypeRef.TTuple tuple:
                for (int i = 0; i < tuple.Elements.Count; i++)
                {
                    if (IsResourceBearing(tuple.Elements[i]))
                    {
                        int elemTemp = NewTemp();
                        Emit(new IrInst.LoadMemOffset(elemTemp, temp, i * 8));
                        EmitResourceBearingDrop(elemTemp, tuple.Elements[i], visiting);
                    }
                }

                return;

            case TypeRef.TList list when IsResourceBearing(list.Element):
                EmitListResourceDrop(temp, list.Element, visiting);
                return;

            default:
                return;
        }
    }

    private void EmitAdtResourceDrop(int temp, TypeRef.TNamedType named, HashSet<string> visiting)
    {
        var key = Pretty(named);
        if (!visiting.Add(key))
        {
            // Reached only on the mutual-recursion fallback from EmitRecursiveAdtResourceDrop; a
            // self-recursive type is handled by the synthesized dropper before it gets here. Leaving
            // the deeper mutual tail to program exit (extremely rare) rather than unfolding forever.
            return;
        }

        var (cases, blocks) = CollectAdtResourceDropCases(named, out var typeParamMap);

        string endLabel = NewLabel("rdrop_end");
        if (cases.Count == 0)
        {
            visiting.Remove(key);
            return; // no constructor carries a resource (shouldn't happen for a resource-bearing ADT)
        }

        int tagTemp = NewTemp();
        Emit(new IrInst.GetAdtTag(tagTemp, temp));
        Emit(new IrInst.SwitchTag(tagTemp, cases, endLabel));
        foreach (var (label, ctor) in blocks)
        {
            Emit(new IrInst.Label(label));
            for (int j = 0; j < ctor.Arity; j++)
            {
                var fieldType = ResolveFieldType(ctor.ParameterTypes[j], typeParamMap);
                if (IsResourceBearing(fieldType))
                {
                    int fieldTemp = NewTemp();
                    Emit(new IrInst.GetAdtField(fieldTemp, temp, j));
                    EmitResourceBearingDrop(fieldTemp, fieldType, visiting);
                }
            }

            Emit(new IrInst.Jump(endLabel));
        }

        Emit(new IrInst.Label(endLabel));
        visiting.Remove(key);
    }

    /// <summary>
    /// Builds the constructor tag switch for an ADT resource drop: the type-parameter
    /// substitution map, plus a (tag, label) case and a labeled block for every
    /// constructor that carries a resource-bearing field.
    /// </summary>
    private (List<(long, string)> Cases, List<(string Label, ConstructorSymbol Ctor)> Blocks) CollectAdtResourceDropCases(
        TypeRef.TNamedType named,
        out Dictionary<TypeParameterSymbol, TypeRef>? typeParamMap)
    {
        typeParamMap = null;
        if (named.Symbol.TypeParameters.Count > 0 && named.TypeArgs.Count == named.Symbol.TypeParameters.Count)
        {
            typeParamMap = new Dictionary<TypeParameterSymbol, TypeRef>();
            for (int i = 0; i < named.Symbol.TypeParameters.Count; i++)
            {
                typeParamMap[named.Symbol.TypeParameters[i]] = named.TypeArgs[i];
            }
        }

        var cases = new List<(long, string)>();
        var blocks = new List<(string Label, ConstructorSymbol Ctor)>();
        foreach (var ctor in named.Symbol.Constructors)
        {
            bool hasResourceField = false;
            for (int j = 0; j < ctor.Arity; j++)
            {
                if (IsResourceBearing(ResolveFieldType(ctor.ParameterTypes[j], typeParamMap)))
                {
                    hasResourceField = true;
                    break;
                }
            }

            if (hasResourceField)
            {
                string label = NewLabel("rdrop_ctor");
                cases.Add((GetConstructorTag(ctor), label));
                blocks.Add((label, ctor));
            }
        }

        return (cases, blocks);
    }

    private readonly Dictionary<string, string> _adtDropperLabels = new(StringComparer.Ordinal);
    private readonly HashSet<string> _adtDropperInProgress = new(StringComparer.Ordinal);

    /// <summary>
    /// True if the ADT reaches its own type through a resource-bearing field (e.g.
    /// <c>type Bag = Mt | Put(FileHandle, Bag)</c>), so a static unfold of its resource-drop would
    /// not terminate. Such a type is dropped by a synthesized recursive function
    /// (<see cref="SynthesizeAdtResourceDropper"/>) instead of the inline unfold in
    /// <see cref="EmitAdtResourceDrop"/>.
    /// </summary>
    private bool IsSelfRecursiveResourceBearingAdt(TypeRef.TNamedType named)
        => ReachesResourceBearingSelf(named, Pretty(named), new HashSet<string>(StringComparer.Ordinal), isRoot: true);

    private bool ReachesResourceBearingSelf(TypeRef type, string rootKey, HashSet<string> visiting, bool isRoot)
    {
        var pruned = Prune(type);
        switch (pruned)
        {
            case TypeRef.TNamedType n when BuiltinRegistry.IsResourceTypeName(n.Symbol.Name):
                return false;

            case TypeRef.TNamedType n:
                var key = Pretty(n);
                if (!isRoot && string.Equals(key, rootKey, StringComparison.Ordinal))
                {
                    return true; // reached the root type again through a field: self-recursive
                }

                if (!visiting.Add(key))
                {
                    return false;
                }

                var (_, blocks) = CollectAdtResourceDropCases(n, out var typeParamMap);
                foreach (var (_, ctor) in blocks)
                {
                    for (int j = 0; j < ctor.Arity; j++)
                    {
                        var fieldType = ResolveFieldType(ctor.ParameterTypes[j], typeParamMap);
                        if (IsResourceBearing(fieldType)
                            && ReachesResourceBearingSelf(fieldType, rootKey, visiting, isRoot: false))
                        {
                            return true;
                        }
                    }
                }

                visiting.Remove(key);
                return false;

            case TypeRef.TTuple tuple:
                return tuple.Elements.Any(e =>
                    IsResourceBearing(e) && ReachesResourceBearingSelf(e, rootKey, visiting, isRoot: false));

            case TypeRef.TList list:
                return IsResourceBearing(list.Element)
                    && ReachesResourceBearingSelf(list.Element, rootKey, visiting, isRoot: false);

            default:
                return false;
        }
    }

    /// <summary>
    /// Drops a self-recursive resource-bearing ADT by building its synthesized recursive dropper
    /// closure at this site (env[0] = the closure itself, so the body recurses via env[0]) and calling
    /// it. Mirrors <see cref="EmitAdtDeepCopy"/>. Falls back to the inline unfold for a mutual-recursion
    /// cycle between distinct ADT types (extremely rare), which drops the outer levels and leaves the
    /// deeper mutual tail to program exit as before.
    /// </summary>
    private void EmitRecursiveAdtResourceDrop(TypeRef.TNamedType named, int temp)
    {
        var label = SynthesizeAdtResourceDropper(named);
        if (label is null)
        {
            EmitAdtResourceDrop(temp, named, new HashSet<string>(StringComparer.Ordinal));
            return;
        }

        int envPtr = NewTemp();
        Emit(new IrInst.Alloc(envPtr, 8));
        int dropper = NewTemp();
        Emit(new IrInst.MakeClosure(dropper, label, envPtr, 8));
        Emit(new IrInst.StoreMemOffset(envPtr, 0, dropper)); // tie the self-reference knot
        int result = NewTemp();
        Emit(new IrInst.CallClosure(result, dropper, temp));
    }

    /// <summary>
    /// Synthesizes (once per concrete type, cached) a recursive resource-drop function for a
    /// self-recursive resource-bearing ADT and returns its label. The function takes (env, value):
    /// it switches on the constructor tag and drops each resource-bearing field — same-type fields via
    /// the self-closure at env[0] (runtime recursion), other fields via
    /// <see cref="EmitResourceBearingDrop"/>. Returns null for a mutual-recursion cycle between
    /// distinct ADT types (caller falls back to the inline unfold). Tag-level, so it is independent of
    /// constructor scope at the call site.
    /// </summary>
    private string? SynthesizeAdtResourceDropper(TypeRef.TNamedType named)
    {
        var key = Pretty(named);
        if (_adtDropperLabels.TryGetValue(key, out var existing))
        {
            return existing;
        }

        if (!_adtDropperInProgress.Add(key))
        {
            return null; // mutual-recursion cycle between distinct ADT types
        }

        string label = $"__rdrop_{_nextLambdaId++}";
        _adtDropperLabels[key] = label; // register before the body so self-type fields resolve to it

        var saved = BeginSynthesizedBody();
        EmitAdtResourceDropperBody(named);
        _funcs.Add(new IrFunction(
            Label: label,
            Instructions: new List<IrInst>(_inst),
            LocalCount: _nextLocalSlot,
            TempCount: _nextTempSlot,
            HasEnvAndArgParams: true));
        RestoreEnclosingBodyState(saved);

        _adtDropperInProgress.Remove(key);
        return label;
    }

    /// <summary>
    /// Emits the body of a synthesized ADT resource-dropper: read the constructor tag, switch to the
    /// live constructor's block, and drop each resource-bearing field — same-type fields via the
    /// self-closure at env[0] (recursion), others via <see cref="EmitResourceBearingDrop"/>.
    /// Constructors that carry no resource fall through to the end and return 0.
    /// </summary>
    private void EmitAdtResourceDropperBody(TypeRef.TNamedType named)
    {
        var rootKey = Pretty(named);
        NewLocal(); // slot 0: env (implicit)
        int argSlot = NewLocal(); // slot 1: the value to drop (implicit)

        int argTemp = NewTemp();
        Emit(new IrInst.LoadLocal(argTemp, argSlot));
        int selfTemp = NewTemp();
        Emit(new IrInst.LoadEnv(selfTemp, 0));
        int tagTemp = NewTemp();
        Emit(new IrInst.GetAdtTag(tagTemp, argTemp));

        var (cases, blocks) = CollectAdtResourceDropCases(named, out var typeParamMap);
        string endLabel = NewLabel("rdrop_end");
        Emit(new IrInst.SwitchTag(tagTemp, cases, endLabel));
        foreach (var (label, ctor) in blocks)
        {
            Emit(new IrInst.Label(label));
            for (int j = 0; j < ctor.Arity; j++)
            {
                var fieldType = ResolveFieldType(ctor.ParameterTypes[j], typeParamMap);
                if (!IsResourceBearing(fieldType))
                {
                    continue;
                }

                int fieldTemp = NewTemp();
                Emit(new IrInst.GetAdtField(fieldTemp, argTemp, j));
                if (string.Equals(Pretty(Prune(fieldType)), rootKey, StringComparison.Ordinal))
                {
                    int r = NewTemp();
                    Emit(new IrInst.CallClosure(r, selfTemp, fieldTemp)); // recurse via self-closure
                }
                else
                {
                    EmitResourceBearingDrop(fieldTemp, fieldType);
                }
            }

            Emit(new IrInst.Jump(endLabel));
        }

        Emit(new IrInst.Label(endLabel));
        int ret = NewTemp();
        Emit(new IrInst.LoadConstInt(ret, 0));
        Emit(new IrInst.Return(ret));
    }

    private void EmitListResourceDrop(int listTemp, TypeRef elementType, HashSet<string> visiting)
    {
        int curSlot = NewLocal();
        Emit(new IrInst.StoreLocal(curSlot, listTemp));
        string headLabel = NewLabel("rdrop_list_head");
        string endLabel = NewLabel("rdrop_list_end");

        Emit(new IrInst.Label(headLabel));
        int curTemp = NewTemp();
        Emit(new IrInst.LoadLocal(curTemp, curSlot));
        int zeroTemp = NewTemp();
        Emit(new IrInst.LoadConstInt(zeroTemp, 0));
        int nonNilTemp = NewTemp();
        Emit(new IrInst.CmpIntNe(nonNilTemp, curTemp, zeroTemp));
        Emit(new IrInst.JumpIfFalse(nonNilTemp, endLabel));

        int headTemp = NewTemp();
        Emit(new IrInst.LoadMemOffset(headTemp, curTemp, 0));
        EmitResourceBearingDrop(headTemp, elementType, visiting);
        int tailTemp = NewTemp();
        Emit(new IrInst.LoadMemOffset(tailTemp, curTemp, 8));
        Emit(new IrInst.StoreLocal(curSlot, tailTemp));
        Emit(new IrInst.Jump(headLabel));

        Emit(new IrInst.Label(endLabel));
    }

    /// <summary>
    /// Emits Drop instructions for all alive (not yet dropped) owned values in the current scope.
    /// Called at scope exit.
    /// </summary>
    private void EmitDropsForCurrentScope()
    {
        if (_ownershipScopes.Count == 0)
        {
            return;
        }

        var scope = _ownershipScopes.Peek();
        foreach (var (_, info) in scope)
        {
            if (info.IsDropped)
            {
                continue;
            }

            if (info.CapturedByClosure && (info.IsResource || info.IsResourceBearing))
            {
                // A closure captured this resource, so it may be reachable from a value escaping this
                // scope through a route the type cannot show (a closure, an aggregate holding one, or a
                // chain of them). Closing it here would strand the escaped value on a closed resource.
                // Transfer ownership to the closure instead: the resource is released when that closure
                // (or its enclosing aggregate) is dropped, or at program exit. Sound — never a
                // use-after-close. The direct-result-closure case was already moved by
                // SkipDropsForResourcesEscapingViaResult above and does not reach here.
                info.ReleaseKind = ResourceReleaseKind.Moved;
                continue;
            }

            EmitOwnedValueDrop(info);
            info.ReleaseKind = ResourceReleaseKind.AutoDropped;
        }
    }

    /// <summary>
    /// Emits SaveArenaState to capture the current heap watermark.
    /// Must be called before any heap allocations that should be covered
    /// by the arena scope. The returned slot pair is pushed onto the
    /// arena watermarks stack and will be popped by <see cref="PopOwnershipScope"/>.
    /// </summary>
    private void EmitArenaWatermark()
    {
        int cursorSlot = NewLocal();
        int endSlot = NewLocal();
        _arenaWatermarks.Push((cursorSlot, endSlot));
        Emit(new IrInst.SaveArenaState(cursorSlot, endSlot));
    }

    /// <summary>
    /// Pushes a new ownership scope. Must be matched with PopOwnershipScope().
    /// Does not emit SaveArenaState — call <see cref="EmitArenaWatermark"/> at the
    /// desired IR position before or after this call. The arena watermark stack
    /// must have one entry per ownership scope for PopOwnershipScope to pair correctly.
    /// </summary>
    private void PushOwnershipScope()
    {
        _ownershipScopes.Push(new Dictionary<string, OwnershipInfo>(StringComparer.Ordinal));
    }

    /// <summary>
    /// Pops an ownership scope, emitting Drop instructions for any remaining alive owned values,
    /// and optionally emitting arena-reset and/or copy-out instructions.
    /// <list type="bullet">
    ///   <item>Copy-type result (Int, Float, Bool): emits RestoreArenaState; returns
    ///     <paramref name="resultTemp"/> unchanged.</item>
    ///   <item>Heap-type result that can be copy-outed (String, List with safe element,
    ///     Closure, ADT with copy-type fields) AND the scope contained alive owned values
    ///     (so there is heap memory worth reclaiming): emits RestoreArenaState followed
    ///     by the appropriate copy-out instruction (CopyOutArena, CopyOutList, or
    ///     CopyOutClosure); returns the new copy-destination temp.</item>
    ///   <item>All other heap types, or heap type with no alive owned values: no arena action;
    ///     returns <paramref name="resultTemp"/> unchanged.</item>
    /// </list>
    /// </summary>
    /// <param name="resultType">The scope result type, used to decide arena action.</param>
    /// <param name="resultTemp">The IR temp holding the scope result (pointer or value).
    ///   Pass -1 if the result temp is unavailable or irrelevant.</param>
    /// <returns>The IR temp to use as the scope result after cleanup.
    ///   For copy-out, this is a newly allocated temp that differs from
    ///   <paramref name="resultTemp"/>. Otherwise it equals <paramref name="resultTemp"/>.</returns>
    private int PopOwnershipScope(TypeRef? resultType = null, int resultTemp = -1)
    {
        SkipDropsForResourcesEscapingViaResult(resultTemp);
        bool hadAliveOwned = HasAliveOwnedValuesInCurrentScope();
        EmitDropsForCurrentScope();

        var (cursorSlot, endSlot) = _arenaWatermarks.Pop();

        if (resultType is not null)
        {
            int preRestoreEndSlot = NewLocal();

            if (CanArenaReset(resultType))
            {
                // Copy-type result: arena reset is always safe — unless a one-shot post pushed
                // during this scope still lives in its allocations.
                var scopeResetSkipLabel = BeginLivePostsGuard();
                Emit(new IrInst.RestoreArenaState(cursorSlot, endSlot, preRestoreEndSlot));
                Emit(new IrInst.ReclaimArenaChunks(endSlot, preRestoreEndSlot));
                EndLivePostsGuard(scopeResetSkipLabel);
            }
            else if (hadAliveOwned && resultTemp >= 0
                && TryEmitScopeCopyOut(resultType, resultTemp, cursorSlot, endSlot, preRestoreEndSlot, out int copiedResultTemp))
            {
                return copiedResultTemp;
            }
            // else: heap type that cannot be copy-outed, or no owned values to reclaim.
            // No arena action; the caller retains the original result pointer.
        }

        _ownershipScopes.Pop();
        return resultTemp;
    }

    /// <summary>
    /// Emits the copy-out arm of <see cref="PopOwnershipScope"/> for a heap-type scope result:
    /// arena restore, the kind-appropriate copy-out, chunk reclaim, and — with capabilities in
    /// the program — the live-posts guard. Pops the ownership scope and yields the temp holding
    /// the scope result. Returns false (emitting nothing, popping nothing) when the result type
    /// has no copy-out kind.
    /// </summary>
    private bool TryEmitScopeCopyOut(TypeRef resultType, int resultTemp, int cursorSlot, int endSlot, int preRestoreEndSlot, out int copiedResultTemp)
    {
        var copyOutKind = GetCopyOutKind(resultType, out int staticSizeBytes);
        if (copyOutKind == CopyOutKind.None)
        {
            copiedResultTemp = resultTemp;
            return false;
        }

        // With capabilities in the program the copy-out is conditional on no post being
        // pending; the result routes through a local so the skipped path keeps the
        // original pointer.
        int guardResultSlot = -1;
        string? copySkipLabel = null;
        if (CapabilityGlobalCount > 0)
        {
            guardResultSlot = NewLocal();
            Emit(new IrInst.StoreLocal(guardResultSlot, resultTemp));
            copySkipLabel = BeginLivePostsGuard();
        }

        Emit(new IrInst.RestoreArenaState(cursorSlot, endSlot, preRestoreEndSlot));
        int copyDest = NewTemp();
        switch (copyOutKind)
        {
            case CopyOutKind.Shallow:
                Emit(new IrInst.CopyOutArena(copyDest, resultTemp, staticSizeBytes));
                break;
            case CopyOutKind.List:
                Emit(new IrInst.CopyOutList(copyDest, resultTemp));
                break;
            case CopyOutKind.Closure:
                Emit(new IrInst.CopyOutClosure(copyDest, resultTemp));
                break;
        }
        Emit(new IrInst.ReclaimArenaChunks(endSlot, preRestoreEndSlot));
        _ownershipScopes.Pop();
        if (guardResultSlot >= 0)
        {
            Emit(new IrInst.StoreLocal(guardResultSlot, copyDest));
            EndLivePostsGuard(copySkipLabel);
            int guardedResultTemp = NewTemp();
            Emit(new IrInst.LoadLocal(guardedResultTemp, guardResultSlot));
            copiedResultTemp = guardedResultTemp;
            return true;
        }

        copiedResultTemp = copyDest;
        return true;
    }

    /// <summary>
    /// Returns true if the given type is a copy type safe for arena reset.
    /// Copy types (Int, Float, Bool) don't reference heap memory, so restoring
    /// the heap cursor after computing a copy-type result is always safe.
    /// </summary>
    private bool CanArenaReset(TypeRef type)
    {
        var pruned = Prune(type);
        return pruned is TypeRef.TInt or TypeRef.TUInt or TypeRef.TFloat or TypeRef.TBool;
    }

    /// <summary>
    /// True if the type is a resource handle (FileHandle, Socket, Process, …). These are represented
    /// as a scalar i64 fd/HANDLE with no heap payload, so a value of this type survives a TCO
    /// back-edge arena reset trivially (nothing to copy out) and the reset never Drops it — Drop
    /// happens only at scope exit / explicit close. This lets a read loop thread its handle without
    /// blocking the per-iteration reset (e.g. a single-loop file fold stays constant-memory).
    /// </summary>
    private bool IsResourceHandleType(TypeRef type) =>
        Prune(type) is TypeRef.TNamedType named && BuiltinRegistry.IsResourceTypeName(named.Symbol.Name);

    /// <summary>
    /// Determines whether the given type's heap representation can be safely copy-outed
    /// after a RestoreArenaState, and what kind of copy-out is needed.
    /// <para>
    /// Handles:
    /// <list type="bullet">
    ///   <item><b>String (TStr):</b> Shallow copy. Layout is {length:i64, bytes…}; all data
    ///     is inline, no internal pointers. <c>staticSizeBytes</c> is -1 (dynamic).</item>
    ///   <item><b>List (TList):</b> Deep cons-chain copy. Safe when element is a copy type
    ///     (Int, Float, Bool) or TStr. Walks tail pointers to copy entire chain.</item>
    ///   <item><b>Closure (TFun):</b> Closure + env copy. Copies the 24-byte closure struct
    ///     and the env block it references.</item>
    ///   <item><b>ADT (TNamedType):</b> Shallow copy of (1 + fieldCount) * 8 bytes. Safe
    ///     when all fields across all constructors are copy types.</item>
    /// </list>
    /// </para>
    /// </summary>
    private CopyOutKind GetCopyOutKind(TypeRef type, out int staticSizeBytes)
    {
        var pruned = Prune(type);
        switch (pruned)
        {
            case TypeRef.TStr:
                staticSizeBytes = -1; // dynamic: 8 (length word) + string.length
                return CopyOutKind.Shallow;

            case TypeRef.TBytes:
                staticSizeBytes = -1; // dynamic: 8 (length word) + bytes.length
                return CopyOutKind.Shallow;

            case TypeRef.TList list when IsCopyOutSafeElement(list.Element):
                staticSizeBytes = 0; // not used — deep copy at runtime
                return CopyOutKind.List;

            case TypeRef.TFun:
                staticSizeBytes = 0;
                return CopyOutKind.None;

            case TypeRef.TNamedType named:
                return CanCopyOutAdt(named, out staticSizeBytes)
                    ? CopyOutKind.Shallow
                    : CopyOutKind.None;

            default:
                staticSizeBytes = 0;
                return CopyOutKind.None;
        }
    }

    /// <summary>
    /// Legacy helper — returns true if the type can be copy-outed via shallow memcpy.
    /// </summary>
    private bool CanCopyOutArena(TypeRef type, out int staticSizeBytes)
    {
        var kind = GetCopyOutKind(type, out staticSizeBytes);
        return kind == CopyOutKind.Shallow;
    }

    /// <summary>
    /// Returns true if a list element type is safe for shallow cons-cell copy-out.
    /// Safe elements are copy types only. Pointer-carrying values such as TStr are
    /// not shallow-copy safe here because copying the cons cells alone would preserve
    /// element references into arena memory that may later be reclaimed.
    /// </summary>
    private bool IsCopyOutSafeElement(TypeRef elementType)
    {
        var pruned = Prune(elementType);
        return CanArenaReset(pruned);
    }

    /// <summary>
    /// Returns true if an ADT type can be safely shallow-copied for arena copy-out.
    /// Requires all constructors to have the same arity (for static-size copy) and
    /// all field types across all constructors to be copy types (inline values, no
    /// heap pointers). Type parameters are substituted with the concrete type arguments
    /// from the instantiated <paramref name="named"/> type.
    /// </summary>
    private bool CanCopyOutAdt(TypeRef.TNamedType named, out int staticSizeBytes)
    {
        staticSizeBytes = 0;
        var sym = named.Symbol;
        if (sym.Constructors.Count == 0)
        {
            return false;
        }

        // All constructors must have the same arity for static-size copy.
        int arity = sym.Constructors[0].Arity;
        for (int i = 1; i < sym.Constructors.Count; i++)
        {
            if (sym.Constructors[i].Arity != arity)
            {
                return false;
            }
        }

        // Build type parameter substitution map: TTypeParam → concrete TypeRef.
        // Constructor parameter types use TTypeParam placeholders (e.g. Box(T) stores
        // TTypeParam("T")), while the instantiated TNamedType has the concrete type
        // arguments (e.g. TNamedType(Box, [TInt])).
        Dictionary<TypeParameterSymbol, TypeRef>? typeParamMap = null;
        if (sym.TypeParameters.Count > 0 && named.TypeArgs.Count == sym.TypeParameters.Count)
        {
            typeParamMap = new Dictionary<TypeParameterSymbol, TypeRef>();
            for (int i = 0; i < sym.TypeParameters.Count; i++)
            {
                typeParamMap[sym.TypeParameters[i]] = named.TypeArgs[i];
            }
        }

        // Check all field types across all constructors are copy types.
        // Pointer-containing fields (TStr, TList, TFun, TNamedType) are not safe
        // because the pointed-to data may be within the freed arena region.
        foreach (var ctor in sym.Constructors)
        {
            foreach (var fieldType in ctor.ParameterTypes)
            {
                var resolved = ResolveFieldType(fieldType, typeParamMap);
                if (!CanArenaReset(resolved))
                {
                    return false;
                }
            }
        }

        staticSizeBytes = (1 + arity) * 8;
        return true;
    }

    /// <summary>
    /// Returns true if an ADT can be safely DEEP-copied out of the arena — a self-contained clone with
    /// no pointer back into the reclaimable region. Unlike <see cref="CanCopyOutAdt"/> (a flat memcpy,
    /// copy-type fields only) this permits pointer fields as long as every one is itself deep-copyable:
    /// a copy type, a String/Bytes, a List of deep-copyable-by-<see cref="EmitDeepCopy"/> elements, or
    /// another non-resource deep-copyable ADT (recursion guarded by <paramref name="visited"/>).
    /// Excludes closures (env may share/own resources), BigInt (no ADT-field deep-copy path), and
    /// resource-bearing types (an fd must never be duplicated). Used to let a TCO loop threading a
    /// fixed-shape pointer-bearing accumulator (e.g. fannkuch's <c>State(perm, count)</c>) reset: the
    /// deep clone breaks any tail-sharing with the previous accumulator, so it is fixed-watermark safe.
    /// </summary>
    private bool CanDeepCopyOutAdt(TypeRef.TNamedType named, HashSet<string>? path = null)
    {
        var sym = named.Symbol;
        if (sym.Constructors.Count == 0 || BuiltinRegistry.IsResourceTypeName(sym.Name) || IsResourceBearing(named))
        {
            return false;
        }

        // Decline SELF-RECURSIVE ADTs (trees like MapTree). Such an accumulator is unbounded, so a full
        // per-iteration deep copy would be O(size)/iteration, and these are exactly the shapes the in-
        // place reuse specialization owns — deep-copying one out from under it corrupts it. `path` holds
        // the ADT types on the current field chain (removed on the way back up, so a diamond of the same
        // non-recursive sub-ADT is still fine); a name already on the path is a cycle.
        path ??= new HashSet<string>(StringComparer.Ordinal);
        if (!path.Add(sym.Name))
        {
            return false;
        }

        Dictionary<TypeParameterSymbol, TypeRef>? typeParamMap = null;
        if (sym.TypeParameters.Count > 0 && named.TypeArgs.Count == sym.TypeParameters.Count)
        {
            typeParamMap = new Dictionary<TypeParameterSymbol, TypeRef>();
            for (int i = 0; i < sym.TypeParameters.Count; i++)
            {
                typeParamMap[sym.TypeParameters[i]] = named.TypeArgs[i];
            }
        }

        bool ok = true;
        foreach (var ctor in sym.Constructors)
        {
            foreach (var fieldType in ctor.ParameterTypes)
            {
                if (!IsDeepCopyOutSafeFieldType(ResolveFieldType(fieldType, typeParamMap), path))
                {
                    ok = false;
                    break;
                }
            }

            if (!ok)
            {
                break;
            }
        }

        path.Remove(sym.Name);
        return ok;
    }

    // True if a value of this type can be deep-copied into a self-contained clone (no pointer back into
    // the reclaimable arena) — the condition for carrying it across the reset at the fixed watermark.
    private bool IsDeepCopyOutSafeType(TypeRef type)
        => IsDeepCopyOutSafeFieldType(type, new HashSet<string>(StringComparer.Ordinal));

    private bool IsDeepCopyOutSafeFieldType(TypeRef type, HashSet<string> path)
    {
        var pruned = Prune(type);
        return pruned switch
        {
            _ when CanArenaReset(pruned) => true,
            TypeRef.TStr or TypeRef.TBytes => true,
            // Lists deep-copy element-by-element: copy-type/String/List-of-copy heads via CopyOutList,
            // any other deep-copyable element via the synthesized recursive list copier.
            TypeRef.TList list => IsDeepCopyOutSafeFieldType(Prune(list.Element), path),
            TypeRef.TTuple tup => tup.Elements.All(e => IsDeepCopyOutSafeFieldType(e, path)),
            TypeRef.TNamedType n => CanDeepCopyOutAdt(n, path),
            _ => false,
        };
    }

    /// <summary>
    /// Resolves a constructor field type by substituting type parameters with their
    /// concrete type arguments, then pruning any remaining type variables.
    /// </summary>
    private TypeRef ResolveFieldType(TypeRef fieldType, Dictionary<TypeParameterSymbol, TypeRef>? typeParamMap)
    {
        var pruned = Prune(fieldType);
        if (typeParamMap is null)
        {
            return pruned;
        }

        // Substitute recursively, not just at the top level: a recursive ADT field such as MapTree's
        // `MapTree(K, V)` subtrees must become MapTree(Str, Int) so a synthesized deep-copier recognises
        // it as the self type and deep-copies its key/value fields, rather than leaving them as
        // type-parameter passthroughs (which leaves a string key shallow-copied → dangling past a reset).
        switch (pruned)
        {
            case TypeRef.TTypeParam tp:
                return typeParamMap.TryGetValue(tp.Symbol, out var concrete) ? Prune(concrete) : pruned;
            case TypeRef.TNamedType named when named.TypeArgs.Count > 0:
                return named with { TypeArgs = named.TypeArgs.Select(a => ResolveFieldType(a, typeParamMap)).ToList() };
            case TypeRef.TList list:
                return new TypeRef.TList(ResolveFieldType(list.Element, typeParamMap));
            case TypeRef.TTuple tuple:
                return new TypeRef.TTuple(tuple.Elements.Select(e => ResolveFieldType(e, typeParamMap)).ToList());
            case TypeRef.TFun funType:
                return new TypeRef.TFun(ResolveFieldType(funType.Arg, typeParamMap), ResolveFieldType(funType.Ret, typeParamMap));
            default:
                return pruned;
        }
    }

    /// <summary>
    /// Returns true if the given type can be copy-outed safely after a TCO arena reset,
    /// and determines the appropriate copy-out kind and IR instruction parameters.
    /// <para>
    /// Safe types for TCO copy-out:
    /// <list type="bullet">
    ///   <item><b>String (TStr):</b> Shallow copy — self-contained, no internal heap pointers.</item>
    ///   <item><b>List with copy-type element (TList where element is Int/Float/Bool):</b>
    ///     Copy only the top cons cell (16 bytes) with inline head value; the tail remains in pre-watermark memory.</item>
    ///   <item><b>List with string element (TList(TStr)):</b>
    ///     Copy only the top cons cell; the string head value is also copied, while the tail remains in pre-watermark memory.</item>
    ///   <item><b>List with inner-list element (TList(TList(copy-type))):</b>
    ///     Copy only the top cons cell; the inner-list head value is deep-copied, while the tail remains in pre-watermark memory.</item>
    ///   <item><b>Closure (TFun):</b> Closure struct + env copy (24 bytes + env block).</item>
    ///   <item><b>ADT (TNamedType):</b> Shallow copy when all fields are copy types.</item>
    /// </list>
    /// </para>
    /// </summary>
    /// <summary>
    /// Emits the copy-out instruction for a single TCO back-edge heap argument,
    /// dispatching on the <see cref="CopyOutKind"/> classified by
    /// <see cref="GetTcoCopyOutKind"/>. Used by both phases of the two-pass
    /// back-edge copy-out (up to scratch above the cursor, then down to the
    /// watermark). <paramref name="kind"/> must not be <see cref="CopyOutKind.None"/>.
    /// </summary>
    private void EmitTcoCopyOut(CopyOutKind kind, int destTemp, int srcTemp, int staticSizeBytes, IrInst.ListHeadCopyKind listHeadCopy)
    {
        switch (kind)
        {
            case CopyOutKind.Shallow:
                Emit(new IrInst.CopyOutArena(destTemp, srcTemp, staticSizeBytes));
                break;
            case CopyOutKind.List:
                Emit(new IrInst.CopyOutList(destTemp, srcTemp, listHeadCopy));
                break;
            case CopyOutKind.Closure:
                Emit(new IrInst.CopyOutClosure(destTemp, srcTemp));
                break;
            case CopyOutKind.TcoListCell:
                Emit(new IrInst.CopyOutTcoListCell(destTemp, srcTemp, listHeadCopy));
                break;
            default:
                throw new System.InvalidOperationException($"EmitTcoCopyOut called with non-copyable kind {kind}.");
        }
    }

    /// <summary>
    /// Emits IR producing a DEEP copy of <paramref name="temp"/> (a value of
    /// <paramref name="type"/>) and returns the new temp. Scalars pass through; strings/bytes,
    /// supported lists and closures use the existing copy-out primitives; tuples are rebuilt with
    /// each element deep-copied. Recursive multi-constructor ADTs are step 1b (synthesized
    /// recursive copiers) — for now they fall back to a shallow reference, which is still a valid
    /// equal value for the (immutable) result and only matters once this is wired into an arena
    /// reset. Shared foundation for in-place reuse (#2 fallback) and parallel result copy-out (#5).
    /// </summary>
    private int EmitDeepCopy(int temp, TypeRef type)
    {
        var pruned = Prune(type);
        switch (pruned)
        {
            case TypeRef.TStr:
            case TypeRef.TBytes:
                {
                    int dest = NewTemp();
                    Emit(new IrInst.CopyOutArena(dest, temp, -1));
                    return dest;
                }

            case TypeRef.TTuple tup:
                {
                    int dest = NewTemp();
                    Emit(new IrInst.Alloc(dest, tup.Elements.Count * 8));
                    for (int i = 0; i < tup.Elements.Count; i++)
                    {
                        int field = NewTemp();
                        Emit(new IrInst.LoadMemOffset(field, temp, i * 8));
                        int copied = EmitDeepCopy(field, tup.Elements[i]);
                        Emit(new IrInst.StoreMemOffset(dest, i * 8, copied));
                    }

                    return dest;
                }

            case TypeRef.TList list:
                return EmitListDeepCopy(temp, list);

            case TypeRef.TFun:
                {
                    int dest = NewTemp();
                    Emit(new IrInst.CopyOutClosure(dest, temp));
                    return dest;
                }

            case TypeRef.TNamedType named when !BuiltinRegistry.IsResourceTypeName(named.Symbol.Name):
                return EmitAdtDeepCopy(temp, named);

            default:
                // Scalars (Int/Float/Bool/UInt) pass through inline; resource ADTs stay shallow.
                return temp;
        }
    }

    /// <summary>
    /// The list arm of <see cref="EmitDeepCopy"/>: copy-type/String/inner-list heads use the
    /// CopyOutList primitive; other deep-copyable elements go through the synthesized
    /// recursive list copier; unsupported element types stay shallow.
    /// </summary>
    private int EmitListDeepCopy(int temp, TypeRef.TList list)
    {
        var elemPruned = Prune(list.Element);
        if (CanArenaReset(elemPruned))
        {
            int dest = NewTemp();
            Emit(new IrInst.CopyOutList(dest, temp, IrInst.ListHeadCopyKind.Inline));
            return dest;
        }

        if (elemPruned is TypeRef.TStr)
        {
            int dest = NewTemp();
            Emit(new IrInst.CopyOutList(dest, temp, IrInst.ListHeadCopyKind.String));
            return dest;
        }

        if (elemPruned is TypeRef.TList inner && CanArenaReset(Prune(inner.Element)))
        {
            int dest = NewTemp();
            Emit(new IrInst.CopyOutList(dest, temp, IrInst.ListHeadCopyKind.InnerList));
            return dest;
        }

        if (IsDeepCopyOutSafeType(elemPruned))
        {
            // Deep-copyable element (fixed-shape ADT / tuple / nested list): a synthesized
            // recursive list copier clones every cell and deep-copies every head, so the
            // result shares nothing with the source (fixed-watermark safe).
            var listLabel = SynthesizeListDeepCopier(elemPruned);
            int listEnvPtr = NewTemp();
            Emit(new IrInst.Alloc(listEnvPtr, 8));
            int listCopier = NewTemp();
            Emit(new IrInst.MakeClosure(listCopier, listLabel, listEnvPtr, 8));
            Emit(new IrInst.StoreMemOffset(listEnvPtr, 0, listCopier));
            int listResult = NewTemp();
            Emit(new IrInst.CallClosure(listResult, listCopier, temp));
            return listResult;
        }

        return temp; // unsupported element type: shallow (step 1b)
    }

    /// <summary>
    /// The non-resource ADT arm of <see cref="EmitDeepCopy"/>.
    /// </summary>
    private int EmitAdtDeepCopy(int temp, TypeRef.TNamedType named)
    {
        // Recursive multi-constructor ADTs (e.g. MapTree) are deep-copied by a synthesized
        // per-type recursive copier closure. Resource types (Socket/FileHandle/...) are
        // never copied — duplicating an fd/handle is wrong — so they fall through to shallow.
        var label = TrySynthesizeAdtCopier(named);
        if (label is null)
        {
            return temp; // unsupported (empty type / mutual-recursion cycle): shallow
        }

        // Build the copier closure at this site and tie its self-reference knot:
        // env[0] = the closure itself, so the function body can recurse via LoadEnv(0).
        int envPtr = NewTemp();
        Emit(new IrInst.Alloc(envPtr, 8));
        int copier = NewTemp();
        Emit(new IrInst.MakeClosure(copier, label, envPtr, 8));
        Emit(new IrInst.StoreMemOffset(envPtr, 0, copier));
        int result = NewTemp();
        Emit(new IrInst.CallClosure(result, copier, temp));
        return result;
    }

    private readonly Dictionary<string, string> _adtCopierLabels = new(StringComparer.Ordinal);
    private readonly HashSet<string> _adtCopierInProgress = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _closureDropperLabels = new(StringComparer.Ordinal);

    /// <summary>
    /// Synthesizes (once per layout, cached) a function that closes the resources an escaping
    /// closure carries. It is stored at the closure's dropper slot (closure+24) and invoked when the
    /// closure is cleaned up (see CleanupResource "Function"). Called as <c>dropper(ownEnv, targetEnv)</c> — it
    /// ignores its own (empty) env and reads the closure's env (the arg), then closes each moved
    /// resource at its recorded offset. Returns the function label.
    /// </summary>
    private string SynthesizeClosureResourceDropper(List<(int EnvOffset, TypeRef Type)> resources)
    {
        var key = string.Join(";", resources.Select(r => $"{r.EnvOffset}:{Pretty(r.Type)}"));
        if (_closureDropperLabels.TryGetValue(key, out var existing))
        {
            return existing;
        }

        string label = $"__cdrop_{_nextLambdaId++}";
        _closureDropperLabels[key] = label;

        // Build the dropper body in isolation (mirrors TrySynthesizeAdtCopier's state save/restore).
        var saved = BeginSynthesizedBody();

        NewLocal(); // slot 0: own env (implicit, empty — ignored)
        int targetEnvSlot = NewLocal(); // slot 1: the dropped closure's env (the call argument)
        int targetEnvTemp = NewTemp();
        Emit(new IrInst.LoadLocal(targetEnvTemp, targetEnvSlot));
        foreach (var (envOffset, type) in resources)
        {
            int resTemp = NewTemp();
            Emit(new IrInst.LoadMemOffset(resTemp, targetEnvTemp, envOffset));
            EmitResourceBearingDrop(resTemp, type);
        }

        int retTemp = NewTemp();
        Emit(new IrInst.LoadConstInt(retTemp, 0));
        Emit(new IrInst.Return(retTemp));

        _funcs.Add(new IrFunction(
            Label: label,
            Instructions: new List<IrInst>(_inst),
            LocalCount: _nextLocalSlot,
            TempCount: _nextTempSlot,
            HasEnvAndArgParams: true));

        // Restore the enclosing function's state.
        RestoreEnclosingBodyState(saved);

        return label;
    }

    /// <summary>
    /// Snapshot of the enclosing function's build state, captured by
    /// <see cref="BeginSynthesizedBody"/> while a synthesized helper function's body is
    /// emitted in isolation and put back by <see cref="RestoreEnclosingBodyState"/>.
    /// </summary>
    private sealed record SynthesizedBodyState(
        List<IrInst> Instructions,
        int NextTempSlot,
        int NextLocalSlot,
        Dictionary<int, string> LocalNames,
        Dictionary<int, TypeRef> LocalTypes,
        Dictionary<int, Dictionary<int, (int Slot, int TotalRefs)>> ReuseTokenFieldBindings,
        Dictionary<int, int> ReuseBindingSeenBySlot,
        Dictionary<int, string> ReuseTrackedSlotNames);

    /// <summary>
    /// Saves the enclosing function's build state and resets it so a synthesized function
    /// body can be emitted in isolation.
    /// </summary>
    private SynthesizedBodyState BeginSynthesizedBody()
    {
        var saved = new SynthesizedBodyState(
            new List<IrInst>(_inst),
            _nextTempSlot,
            _nextLocalSlot,
            new Dictionary<int, string>(_localNames),
            new Dictionary<int, TypeRef>(_localTypes),
            new Dictionary<int, Dictionary<int, (int Slot, int TotalRefs)>>(_reuseTokenFieldBindings),
            new Dictionary<int, int>(_reuseBindingSeenBySlot),
            new Dictionary<int, string>(_reuseTrackedSlotNames));
        _inst.Clear();
        _nextTempSlot = 0;
        _reuseTokenFieldBindings.Clear();
        _reuseBindingSeenBySlot.Clear();
        _reuseTrackedSlotNames.Clear();
        _nextLocalSlot = 0;
        _localNames.Clear();
        _localTypes.Clear();
        return saved;
    }

    /// <summary>
    /// Restores the enclosing function's build state saved by <see cref="BeginSynthesizedBody"/>.
    /// </summary>
    private void RestoreEnclosingBodyState(SynthesizedBodyState saved)
    {
        _inst.Clear();
        _inst.AddRange(saved.Instructions);
        _nextTempSlot = saved.NextTempSlot;
        _reuseTokenFieldBindings.Clear();
        foreach (var kv in saved.ReuseTokenFieldBindings) _reuseTokenFieldBindings[kv.Key] = kv.Value;
        _reuseBindingSeenBySlot.Clear();
        foreach (var kv in saved.ReuseBindingSeenBySlot) _reuseBindingSeenBySlot[kv.Key] = kv.Value;
        _reuseTrackedSlotNames.Clear();
        foreach (var kv in saved.ReuseTrackedSlotNames) _reuseTrackedSlotNames[kv.Key] = kv.Value;
        _nextLocalSlot = saved.NextLocalSlot;
        _localNames.Clear();
        foreach (var kv in saved.LocalNames)
        {
            _localNames[kv.Key] = kv.Value;
        }

        _localTypes.Clear();
        foreach (var kv in saved.LocalTypes)
        {
            _localTypes[kv.Key] = kv.Value;
        }
    }

    /// <summary>
    /// Synthesizes (once per concrete type, cached) a recursive deep-copy function for an ADT and
    /// returns its label. The function takes (env, value): it reads the constructor tag, allocates a
    /// fresh node of the same constructor, and deep-copies each field — same-type fields via the
    /// self-closure in env[0] (recursion), other fields via <see cref="EmitDeepCopy"/>. Returns null
    /// for empty types or mutual-recursion cycles (caller falls back to a shallow reference).
    /// Tag-level (no constructor names), so it works regardless of constructor scope at the call site.
    /// </summary>
    private string? TrySynthesizeAdtCopier(TypeRef.TNamedType named)
    {
        var key = Pretty(named);
        if (_adtCopierLabels.TryGetValue(key, out var existing))
        {
            return existing;
        }

        if (_adtCopierInProgress.Contains(key))
        {
            return null; // mutual-recursion cycle between distinct ADT types — bail to shallow
        }

        var sym = named.Symbol;
        if (sym.Constructors.Count == 0)
        {
            return null;
        }

        Dictionary<TypeParameterSymbol, TypeRef>? typeParamMap = null;
        if (sym.TypeParameters.Count > 0 && named.TypeArgs.Count == sym.TypeParameters.Count)
        {
            typeParamMap = new Dictionary<TypeParameterSymbol, TypeRef>();
            for (int i = 0; i < sym.TypeParameters.Count; i++)
            {
                typeParamMap[sym.TypeParameters[i]] = named.TypeArgs[i];
            }
        }

        _adtCopierInProgress.Add(key);
        string label = $"__deepcopy_{_nextLambdaId++}";
        _adtCopierLabels[key] = label; // register before the body so self-type fields resolve to it

        // Build the copier body in isolation (mirrors LowerLambdaCore's state save/restore).
        var saved = BeginSynthesizedBody();

        EmitAdtCopierBody(label, named, typeParamMap);

        _funcs.Add(new IrFunction(
            Label: label,
            Instructions: new List<IrInst>(_inst),
            LocalCount: _nextLocalSlot,
            TempCount: _nextTempSlot,
            HasEnvAndArgParams: true));

        // Restore the enclosing function's state.
        RestoreEnclosingBodyState(saved);

        _adtCopierInProgress.Remove(key);
        return label;
    }

    /// <summary>
    /// Emits the instructions of a synthesized ADT deep-copier: read the constructor tag,
    /// switch to the live constructor's block, allocate a fresh node, and deep-copy each
    /// field (same-type fields via the self-closure in env[0], others via
    /// <see cref="EmitDeepCopy"/>).
    /// </summary>
    private void EmitAdtCopierBody(string label, TypeRef.TNamedType named, Dictionary<TypeParameterSymbol, TypeRef>? typeParamMap)
    {
        var sym = named.Symbol;

        NewLocal(); // slot 0: env (implicit)
        int argSlot = NewLocal(); // slot 1: the value to copy (implicit)

        int argTemp = NewTemp();
        Emit(new IrInst.LoadLocal(argTemp, argSlot));
        int selfTemp = NewTemp();
        Emit(new IrInst.LoadEnv(selfTemp, 0));
        int tagTemp = NewTemp();
        Emit(new IrInst.GetAdtTag(tagTemp, argTemp));

        var cases = new List<(long, string)>(sym.Constructors.Count);
        var ctorLabels = new string[sym.Constructors.Count];
        for (int i = 0; i < sym.Constructors.Count; i++)
        {
            ctorLabels[i] = $"{label}_c{i}";
            cases.Add((GetConstructorTag(sym.Constructors[i]), ctorLabels[i]));
        }

        string defaultLabel = $"{label}_default";
        Emit(new IrInst.SwitchTag(tagTemp, cases, defaultLabel));

        for (int i = 0; i < sym.Constructors.Count; i++)
        {
            Emit(new IrInst.Label(ctorLabels[i]));
            var ctor = sym.Constructors[i];
            int newTemp = NewTemp();
            Emit(new IrInst.AllocAdt(newTemp, GetConstructorTag(ctor), ctor.Arity));
            for (int j = 0; j < ctor.Arity; j++)
            {
                int fieldTemp = NewTemp();
                Emit(new IrInst.LoadMemOffset(fieldTemp, argTemp, (j + 1) * 8));
                var fieldType = ResolveFieldType(ctor.ParameterTypes[j], typeParamMap);
                int copied = CopyFieldInsideCopier(fieldTemp, fieldType, named, selfTemp);
                Emit(new IrInst.StoreMemOffset(newTemp, (j + 1) * 8, copied));
            }

            Emit(new IrInst.Return(newTemp));
        }

        Emit(new IrInst.Label(defaultLabel));
        Emit(new IrInst.Return(argTemp)); // unreachable fallback
    }

    private readonly Dictionary<string, string> _listCopierLabels = new(StringComparer.Ordinal);

    /// <summary>
    /// Synthesizes (once per element type, cached) a recursive function deep-copying a cons list:
    /// nil (0) passes through; otherwise the head is deep-copied (via <see cref="EmitDeepCopy"/> —
    /// e.g. through the element ADT's synthesized copier) and the tail recursed via the self-closure
    /// at env[0], rebuilding each 16-byte cell. The clone shares nothing with the source, so a
    /// <c>List(fixed-shape-ADT)</c> accumulator can cross a fixed-watermark arena reset. Returns the
    /// function label.
    /// </summary>
    private string SynthesizeListDeepCopier(TypeRef elementType)
    {
        var key = Pretty(elementType);
        if (_listCopierLabels.TryGetValue(key, out var existing))
        {
            return existing;
        }

        string label = $"__deepcopy_list_{_nextLambdaId++}";
        _listCopierLabels[key] = label;

        // Build the copier body in isolation (mirrors TrySynthesizeAdtCopier's state save/restore).
        var saved = BeginSynthesizedBody();

        EmitListDeepCopierBody(label, elementType);

        _funcs.Add(new IrFunction(
            Label: label,
            Instructions: new List<IrInst>(_inst),
            LocalCount: _nextLocalSlot,
            TempCount: _nextTempSlot,
            HasEnvAndArgParams: true));

        // Restore the enclosing function's state.
        RestoreEnclosingBodyState(saved);

        return label;
    }

    /// <summary>
    /// Emits the instructions of a synthesized recursive list deep-copier: nil passes
    /// through; otherwise the head is deep-copied, the tail recursed via the self-closure
    /// at env[0], and a fresh 16-byte cell rebuilt.
    /// </summary>
    private void EmitListDeepCopierBody(string label, TypeRef elementType)
    {
        NewLocal(); // slot 0: env (implicit)
        int argSlot = NewLocal(); // slot 1: the list to copy (implicit)

        int argTemp = NewTemp();
        Emit(new IrInst.LoadLocal(argTemp, argSlot));
        int selfTemp = NewTemp();
        Emit(new IrInst.LoadEnv(selfTemp, 0));

        // if (list != nil) goto copy; return 0
        string copyLabel = $"{label}_copy";
        int zeroTemp = NewTemp();
        Emit(new IrInst.LoadConstInt(zeroTemp, 0));
        int isNilTemp = NewTemp();
        Emit(new IrInst.CmpIntEq(isNilTemp, argTemp, zeroTemp));
        Emit(new IrInst.JumpIfFalse(isNilTemp, copyLabel));
        int nilResult = NewTemp();
        Emit(new IrInst.LoadConstInt(nilResult, 0));
        Emit(new IrInst.Return(nilResult));

        Emit(new IrInst.Label(copyLabel));
        int headTemp = NewTemp();
        Emit(new IrInst.LoadMemOffset(headTemp, argTemp, 0));
        int tailTemp = NewTemp();
        Emit(new IrInst.LoadMemOffset(tailTemp, argTemp, 8));
        int copiedHead = EmitDeepCopy(headTemp, elementType);
        int copiedTail = NewTemp();
        Emit(new IrInst.CallClosure(copiedTail, selfTemp, tailTemp));
        int cellTemp = NewTemp();
        Emit(new IrInst.Alloc(cellTemp, 16));
        Emit(new IrInst.StoreMemOffset(cellTemp, 0, copiedHead));
        Emit(new IrInst.StoreMemOffset(cellTemp, 8, copiedTail));
        Emit(new IrInst.Return(cellTemp));
    }

    /// <summary>
    /// Copies one field inside a synthesized ADT copier: a field of the same recursive type uses the
    /// self-closure (env[0]) for recursion; any other field type goes through <see cref="EmitDeepCopy"/>.
    /// </summary>
    private int CopyFieldInsideCopier(int fieldTemp, TypeRef fieldType, TypeRef.TNamedType selfType, int selfTemp)
    {
        var pruned = Prune(fieldType);
        if (pruned is TypeRef.TNamedType fieldNamed
            && string.Equals(Pretty(fieldNamed), Pretty(selfType), StringComparison.Ordinal))
        {
            int copied = NewTemp();
            Emit(new IrInst.CallClosure(copied, selfTemp, fieldTemp));
            return copied;
        }

        return EmitDeepCopy(fieldTemp, pruned);
    }

    private CopyOutKind GetTcoCopyOutKind(TypeRef type, out int staticSizeBytes, out IrInst.ListHeadCopyKind listHeadCopy)
    {
        var pruned = Prune(type);
        listHeadCopy = IrInst.ListHeadCopyKind.Inline;
        switch (pruned)
        {
            case TypeRef.TStr:
                staticSizeBytes = -1; // dynamic: 8 + length
                return CopyOutKind.Shallow;

            case TypeRef.TBigInt:
                // Self-contained { header, limb… } buffer, no internal pointers — copy the normalized
                // prefix (size from the header) so a threaded BigInt accumulator survives the reset and
                // the iteration's BigInt garbage is reclaimed.
                staticSizeBytes = IrInst.CopyOutArena.BigIntSize;
                return CopyOutKind.Shallow;

            case TypeRef.TList list:
                return GetTcoListCopyOutKind(list, out staticSizeBytes, ref listHeadCopy);

            case TypeRef.TFun:
                staticSizeBytes = 0;
                return CopyOutKind.Closure;

            case TypeRef.TTuple:
                // A tuple is a fixed-shape heap record; if every element is deep-copyable, EmitDeepCopy
                // rebuilds it as a self-contained clone, so a tuple accumulator (e.g. a threaded
                // `(seed, output)`) can reset. Same DeepAdt path as a fixed-shape ADT.
                staticSizeBytes = 0;
                return IsDeepCopyOutSafeType(pruned) ? CopyOutKind.DeepAdt : CopyOutKind.None;

            case TypeRef.TNamedType named:
                if (CanCopyOutAdt(named, out staticSizeBytes))
                {
                    return CopyOutKind.Shallow;
                }

                // A pointer-bearing ADT (list/string fields) can still be carried across the reset by a
                // recursive deep copy — a self-contained clone. Lets a fixed-shape ADT accumulator reset.
                staticSizeBytes = 0;
                return CanDeepCopyOutAdt(named) ? CopyOutKind.DeepAdt : CopyOutKind.None;

            default:
                staticSizeBytes = 0;
                return CopyOutKind.None;
        }
    }

    /// <summary>
    /// The list arm of <see cref="GetTcoCopyOutKind"/>: classifies the copy-out kind for a
    /// list accumulator by its element type.
    /// </summary>
    private CopyOutKind GetTcoListCopyOutKind(TypeRef.TList list, out int staticSizeBytes, ref IrInst.ListHeadCopyKind listHeadCopy)
    {
        var elemPruned = Prune(list.Element);
        if (CanArenaReset(elemPruned))
        {
            // Copy-type heads: inline values, single cell shallow copy (16 bytes).
            staticSizeBytes = 16;
            return CopyOutKind.Shallow;
        }
        if (elemPruned is TypeRef.TStr)
        {
            // String heads: copy one cell + copy the string head value.
            staticSizeBytes = 0;
            listHeadCopy = IrInst.ListHeadCopyKind.String;
            return CopyOutKind.TcoListCell;
        }
        if (elemPruned is TypeRef.TList inner && CanArenaReset(Prune(inner.Element)))
        {
            // Inner list with copy-type elements: copy one cell + deep-copy inner list head.
            staticSizeBytes = 0;
            listHeadCopy = IrInst.ListHeadCopyKind.InnerList;
            return CopyOutKind.TcoListCell;
        }

        // A list of deep-copyable elements (fixed-shape ADT / tuple) is cloned whole by
        // the synthesized list copier — a self-contained copy, fixed-watermark safe.
        // Lets n-body's List(Body) accumulator reset instead of growing O(N).
        staticSizeBytes = 0;
        return IsDeepCopyOutSafeType(elemPruned) ? CopyOutKind.DeepAdt : CopyOutKind.None;
    }

    /// <summary>
    /// Returns true if the current ownership scope contains any alive (not yet dropped) owned values.
    /// </summary>
    private bool HasAliveOwnedValuesInCurrentScope()
    {
        if (_ownershipScopes.Count == 0)
        {
            return false;
        }

        var scope = _ownershipScopes.Peek();
        foreach (var (_, info) in scope)
        {
            if (!info.IsDropped)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Tracks owned bindings created by pattern matching.
    /// Scans pattern bindings for owned types and registers them for tracking.
    /// </summary>
    private void TrackOwnedBindingsInPattern(IReadOnlyDictionary<string, TypeRef> patternBindings)
    {
        foreach (var (name, type) in patternBindings)
        {
            var prunedType = Prune(type);
            var ownedTypeName = GetOwnedTypeName(prunedType);
            if (ownedTypeName is not null)
            {
                // Look up the slot from the current scope
                if (Lookup(name) is Binding.Local local)
                {
                    var isResource = GetResourceTypeName(prunedType) is not null;
                    TrackOwnedValue(name, local.Slot, ownedTypeName, isResource, local.DefinitionSpan, prunedType);
                }
            }
        }
    }

    /// <summary>
    /// Checks if a resource expression refers to a dropped resource and reports use-after-drop.
    /// Only applies to resource types (Socket), not general owned types.
    /// </summary>
    private void CheckUseAfterDrop(Expr expr)
    {
        if (expr is Expr.Var v)
        {
            var info = LookupOwnedValue(v.Name);
            if (info is not null && info.IsResource && info.IsDropped)
            {
                if (info.ReleaseKind == ResourceReleaseKind.Moved)
                {
                    ReportDiagnostic(GetSpan(expr),
                        $"Resource '{v.Name}' has been moved and can no longer be used here. Passing a resource to a function or storing it in a data structure transfers ownership.",
                        DiagnosticCodes.UseAfterMove);
                }
                else
                {
                    ReportDiagnostic(GetSpan(expr),
                        $"Resource '{v.Name}' has already been closed. Using a resource after it has been closed is not allowed.",
                        DiagnosticCodes.UseAfterDrop);
                }
            }
        }
    }
}
