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
    private void TrackOwnedValue(string name, int slot, string typeName, bool isResource, TextSpan? definitionSpan)
    {
        if (_ownershipScopes.Count > 0)
        {
            _ownershipScopes.Peek()[name] = new OwnershipInfo(slot, typeName, isResource, definitionSpan);
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

        info.IsDropped = true;
        return true;
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
            if (!info.IsDropped)
            {
                int loadTemp = NewTemp();
                Emit(new IrInst.LoadLocal(loadTemp, info.Slot));
                Emit(new IrInst.Drop(loadTemp, info.TypeName));
                info.IsDropped = true;
            }
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
        bool hadAliveOwned = HasAliveOwnedValuesInCurrentScope();
        EmitDropsForCurrentScope();

        var (cursorSlot, endSlot) = _arenaWatermarks.Pop();

        if (resultType is not null)
        {
            int preRestoreEndSlot = NewLocal();

            if (CanArenaReset(resultType))
            {
                // Copy-type result: arena reset is always safe. No heap values escape.
                Emit(new IrInst.RestoreArenaState(cursorSlot, endSlot, preRestoreEndSlot));
                Emit(new IrInst.ReclaimArenaChunks(endSlot, preRestoreEndSlot));
            }
            else if (hadAliveOwned && resultTemp >= 0)
            {
                var copyOutKind = GetCopyOutKind(resultType, out int staticSizeBytes);
                if (copyOutKind != CopyOutKind.None)
                {
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
                    return copyDest;
                }
            }
            // else: heap type that cannot be copy-outed, or no owned values to reclaim.
            // No arena action; the caller retains the original result pointer.
        }

        _ownershipScopes.Pop();
        return resultTemp;
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
    /// Resolves a constructor field type by substituting type parameters with their
    /// concrete type arguments, then pruning any remaining type variables.
    /// </summary>
    private TypeRef ResolveFieldType(TypeRef fieldType, Dictionary<TypeParameterSymbol, TypeRef>? typeParamMap)
    {
        var pruned = Prune(fieldType);
        if (pruned is TypeRef.TTypeParam tp && typeParamMap is not null
            && typeParamMap.TryGetValue(tp.Symbol, out var concrete))
        {
            return Prune(concrete);
        }
        return pruned;
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
    private CopyOutKind GetTcoCopyOutKind(TypeRef type, out int staticSizeBytes, out IrInst.ListHeadCopyKind listHeadCopy)
    {
        var pruned = Prune(type);
        listHeadCopy = IrInst.ListHeadCopyKind.Inline;
        switch (pruned)
        {
            case TypeRef.TStr:
                staticSizeBytes = -1; // dynamic: 8 + length
                return CopyOutKind.Shallow;

            case TypeRef.TList list:
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
                    staticSizeBytes = 0;
                    return CopyOutKind.None;
                }

            case TypeRef.TFun:
                staticSizeBytes = 0;
                return CopyOutKind.Closure;

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
                    TrackOwnedValue(name, local.Slot, ownedTypeName, isResource, local.DefinitionSpan);
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
                ReportDiagnostic(GetSpan(expr),
                    $"Resource '{v.Name}' has already been closed. Using a resource after it has been closed is not allowed.",
                    DiagnosticCodes.UseAfterDrop);
            }
        }
    }
}
