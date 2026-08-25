namespace Ashes.Semantics;

public sealed partial class Lowering
{
    private readonly Dictionary<TypeRef, OrdinaryHeapLayoutCapability> _ordinaryHeapLayoutCapabilities =
        new(ConcreteTypeRefEqualityComparer.Instance);

    private sealed class ConcreteTypeRefEqualityComparer : IEqualityComparer<TypeRef>
    {
        public static ConcreteTypeRefEqualityComparer Instance { get; } = new();

        public bool Equals(TypeRef? left, TypeRef? right)
        {
            if (ReferenceEquals(left, right))
            {
                return true;
            }

            if (left is null || right is null || left.GetType() != right.GetType())
            {
                return false;
            }

            return (left, right) switch
            {
                (TypeRef.TUInt a, TypeRef.TUInt b) => a.Bits == b.Bits,
                (TypeRef.TList a, TypeRef.TList b) => Equals(a.Element, b.Element),
                (TypeRef.TTuple a, TypeRef.TTuple b) => EqualTypes(a.Elements, b.Elements),
                (TypeRef.TFun a, TypeRef.TFun b) =>
                    Equals(a.Arg, b.Arg) && Equals(a.Ret, b.Ret) && Equals(a.Row, b.Row),
                (TypeRef.TVar a, TypeRef.TVar b) => a.Id == b.Id,
                (TypeRef.TCapability a, TypeRef.TCapability b) =>
                    ReferenceEquals(a.Symbol, b.Symbol) && EqualTypes(a.Args, b.Args),
                (TypeRef.TRow a, TypeRef.TRow b) =>
                    EqualCapabilities(a.Capabilities, b.Capabilities) && Equals(a.Tail, b.Tail),
                (TypeRef.TNamedType a, TypeRef.TNamedType b) =>
                    ReferenceEquals(a.Symbol, b.Symbol) && EqualTypes(a.TypeArgs, b.TypeArgs),
                (TypeRef.TTypeParam a, TypeRef.TTypeParam b) => ReferenceEquals(a.Symbol, b.Symbol),
                (TypeRef.TOpaque a, TypeRef.TOpaque b) =>
                    string.Equals(a.Name, b.Name, StringComparison.Ordinal),
                (TypeRef.TPtr a, TypeRef.TPtr b) => Equals(a.Pointee, b.Pointee),
                _ => true,
            };
        }

        public int GetHashCode(TypeRef type)
        {
            HashCode hash = new();
            hash.Add(type.GetType());
            AddTypeHash(ref hash, type);
            return hash.ToHashCode();
        }

        private static bool EqualTypes(IReadOnlyList<TypeRef> left, IReadOnlyList<TypeRef> right)
        {
            if (left.Count != right.Count)
            {
                return false;
            }

            for (int index = 0; index < left.Count; index++)
            {
                if (!Instance.Equals(left[index], right[index]))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool EqualCapabilities(
            IReadOnlyList<TypeRef.TCapability> left,
            IReadOnlyList<TypeRef.TCapability> right)
        {
            if (left.Count != right.Count)
            {
                return false;
            }

            for (int index = 0; index < left.Count; index++)
            {
                if (!Instance.Equals(left[index], right[index]))
                {
                    return false;
                }
            }

            return true;
        }

        private static void AddTypeHash(ref HashCode hash, TypeRef type)
        {
            switch (type)
            {
                case TypeRef.TUInt unsigned:
                    hash.Add(unsigned.Bits);
                    break;
                case TypeRef.TList list:
                    hash.Add(Instance.GetHashCode(list.Element));
                    break;
                case TypeRef.TTuple tuple:
                    AddTypesHash(ref hash, tuple.Elements);
                    break;
                case TypeRef.TFun function:
                    hash.Add(Instance.GetHashCode(function.Arg));
                    hash.Add(Instance.GetHashCode(function.Ret));
                    hash.Add(function.Row is null ? 0 : Instance.GetHashCode(function.Row));
                    break;
                case TypeRef.TVar variable:
                    hash.Add(variable.Id);
                    break;
                case TypeRef.TCapability capability:
                    hash.Add(capability.Symbol, ReferenceEqualityComparer.Instance);
                    AddTypesHash(ref hash, capability.Args);
                    break;
                case TypeRef.TRow row:
                    AddCapabilitiesHash(ref hash, row.Capabilities);
                    hash.Add(row.Tail is null ? 0 : Instance.GetHashCode(row.Tail));
                    break;
                case TypeRef.TNamedType named:
                    hash.Add(named.Symbol, ReferenceEqualityComparer.Instance);
                    AddTypesHash(ref hash, named.TypeArgs);
                    break;
                case TypeRef.TTypeParam parameter:
                    hash.Add(parameter.Symbol, ReferenceEqualityComparer.Instance);
                    break;
                case TypeRef.TOpaque opaque:
                    hash.Add(opaque.Name, StringComparer.Ordinal);
                    break;
                case TypeRef.TPtr pointer:
                    hash.Add(Instance.GetHashCode(pointer.Pointee));
                    break;
            }
        }

        private static void AddTypesHash(ref HashCode hash, IReadOnlyList<TypeRef> types)
        {
            hash.Add(types.Count);
            foreach (TypeRef type in types)
            {
                hash.Add(Instance.GetHashCode(type));
            }
        }

        private static void AddCapabilitiesHash(
            ref HashCode hash,
            IReadOnlyList<TypeRef.TCapability> capabilities)
        {
            hash.Add(capabilities.Count);
            foreach (TypeRef.TCapability capability in capabilities)
            {
                hash.Add(Instance.GetHashCode(capability));
            }
        }
    }

    private OrdinaryHeapLayoutCapability GetOrdinaryHeapLayoutCapability(TypeRef type)
    {
        TypeRef resolved = EraseZeroCostTypeRepresentation(type);
        bool remainsAbstract = ValueTypeRemainsAbstract(resolved);
        bool cacheable = !remainsAbstract;
        if (cacheable
            && _ordinaryHeapLayoutCapabilities.TryGetValue(
                resolved,
                out OrdinaryHeapLayoutCapability? cached))
        {
            return cached;
        }

        OrdinaryHeapLayoutCapability capability =
            ComputeOrdinaryHeapLayoutCapability(resolved, remainsAbstract);
        if (cacheable)
        {
            _ordinaryHeapLayoutCapabilities[resolved] = capability;
        }

        return capability;
    }

    private OrdinaryHeapLayoutCapability ComputeOrdinaryHeapLayoutCapability(
        TypeRef resolved,
        bool remainsAbstract)
    {
        OrdinaryHeapStructuralCopyKind structuralCopy = GetStructuralCopyKind(
            resolved,
            out int? staticCopySizeBytes);
        bool arenaDeepCopySupported = IsArenaDeepCopyLayout(
            resolved,
            new HashSet<string>(StringComparer.Ordinal));
        bool ownedChildrenDroppable = CanDropOrdinaryValueGraph(
            resolved,
            new HashSet<TypeSymbol>());
        bool containsResourceOrBorrowedView = IsResourceBearing(resolved);
        bool unresolved = remainsAbstract
            && ContainsUnresolvedLayoutType(
                resolved,
                new HashSet<TypeSymbol>());

        GetRuntimeAdtLayoutSupport(
            resolved,
            out bool runtimeCopyAdtSupported,
            out bool runtimeRecordAdtSupported,
            out bool runtimeOwnedChildAdtSupported,
            out bool runtimeTcoOwnedChildAdtSupported,
            out bool runtimeRecursiveAdtSupported);
        bool runtimeOuterCellReuseSupported = resolved is TypeRef.TNamedType
            && (runtimeCopyAdtSupported
                || runtimeRecordAdtSupported
                || runtimeOwnedChildAdtSupported
                || runtimeRecursiveAdtSupported);
        bool runtimeTcoListElementSupported = IsRuntimeTcoListElementLayout(
            resolved,
            new HashSet<TypeSymbol>());
        List<OrdinaryHeapLayoutChild> children = DescribeOrdinaryHeapChildren(resolved);
        bool containsOwnedChild = children.Any(child =>
            child.DropKind != OrdinaryHeapChildDropKind.None);
        OrdinaryHeapLayoutRejection rejections = BuildLayoutRejections(
            containsResourceOrBorrowedView,
            ownedChildrenDroppable,
            unresolved,
            runtimeOuterCellReuseSupported);

        return new OrdinaryHeapLayoutCapability(
            LayoutForType(resolved),
            structuralCopy,
            arenaDeepCopySupported,
            staticCopySizeBytes,
            ownedChildrenDroppable,
            containsOwnedChild,
            containsResourceOrBorrowedView,
            runtimeOuterCellReuseSupported,
            runtimeCopyAdtSupported,
            runtimeRecordAdtSupported,
            runtimeOwnedChildAdtSupported,
            runtimeTcoOwnedChildAdtSupported,
            runtimeTcoListElementSupported,
            children.AsReadOnly(),
            rejections);
    }

    private void GetRuntimeAdtLayoutSupport(
        TypeRef type,
        out bool copy,
        out bool record,
        out bool ownedChild,
        out bool tcoOwnedChild,
        out bool recursive)
    {
        copy = type is TypeRef.TNamedType copyAdt
            && IsRuntimeCopyAdtLayout(copyAdt);
        record = type is TypeRef.TNamedType recordAdt
            && IsRuntimeRecordAdtLayout(recordAdt, new HashSet<TypeSymbol>());
        ownedChild = type is TypeRef.TNamedType ownedChildAdt
            && IsRuntimeOwnedChildAdtLayout(ownedChildAdt, new HashSet<TypeSymbol>());
        tcoOwnedChild = type is TypeRef.TNamedType tcoAdt
            && IsRuntimeTcoOwnedChildAdtLayout(tcoAdt, new HashSet<TypeSymbol>());
        recursive = type is TypeRef.TNamedType recursiveAdt
            && CanRuntimeManageRecursiveCopyAdt(recursiveAdt);
    }

    private static OrdinaryHeapLayoutRejection BuildLayoutRejections(
        bool containsResourceOrBorrowedView,
        bool ownedChildrenDroppable,
        bool unresolved,
        bool runtimeOuterCellReuseSupported)
    {
        OrdinaryHeapLayoutRejection rejections = containsResourceOrBorrowedView
            ? OrdinaryHeapLayoutRejection.ResourceOrBorrowedViewContainment
            : OrdinaryHeapLayoutRejection.None;
        if (!ownedChildrenDroppable)
        {
            rejections |= OrdinaryHeapLayoutRejection.UnsupportedChildDropLayout;
        }

        if (unresolved)
        {
            rejections |= OrdinaryHeapLayoutRejection.UnresolvedType;
        }

        if (!runtimeOuterCellReuseSupported)
        {
            rejections |= OrdinaryHeapLayoutRejection.UnsupportedOuterCellReuse;
        }

        return rejections;
    }

    private OrdinaryHeapLayoutCapability? GetOrdinaryHeapLayoutCapability(
        TypeRef? type,
        LoweredTempRepresentation representation)
    {
        if (type is null)
        {
            return null;
        }

        OrdinaryHeapLayoutCapability capability = GetOrdinaryHeapLayoutCapability(type);
        return representation == LoweredTempRepresentation.BorrowedView
            ? capability.AsBorrowedView()
            : capability;
    }

    private List<OrdinaryHeapLayoutChild> GetOwnedOrdinaryHeapChildren(
        TypeRef type,
        ConstructorSymbol? constructor = null)
    {
        IReadOnlyList<OrdinaryHeapLayoutChild> described =
            GetOrdinaryHeapLayoutCapability(type).Children;
        var owned = new List<OrdinaryHeapLayoutChild>();
        foreach (OrdinaryHeapLayoutChild child in described)
        {
            if (child.DropKind != OrdinaryHeapChildDropKind.None
                && (constructor is null
                    || string.Equals(
                        child.ConstructorName,
                        constructor.Name,
                        StringComparison.Ordinal)))
            {
                owned.Add(child);
            }
        }

        return owned;
    }

    private OrdinaryHeapStructuralCopyKind GetStructuralCopyKind(
        TypeRef type,
        out int? staticCopySizeBytes)
    {
        TypeRef valueType = Prune(type);
        if (CanArenaReset(valueType))
        {
            staticCopySizeBytes = HeapLayouts.WordSizeBytes;
            return OrdinaryHeapStructuralCopyKind.Inline;
        }

        switch (valueType)
        {
            case TypeRef.TStr or TypeRef.TBytes:
                staticCopySizeBytes = null;
                return OrdinaryHeapStructuralCopyKind.Shallow;
            case TypeRef.TBigInt:
                staticCopySizeBytes = IrInst.CopyOutArena.BigIntSize;
                return OrdinaryHeapStructuralCopyKind.Shallow;
            case TypeRef.TList list:
                staticCopySizeBytes = null;
                return IsArenaDeepCopyLayout(
                    list.Element,
                    new HashSet<string>(StringComparer.Ordinal))
                        ? OrdinaryHeapStructuralCopyKind.Deep
                        : OrdinaryHeapStructuralCopyKind.None;
            case TypeRef.TTuple tuple:
                staticCopySizeBytes = checked(tuple.Elements.Count * HeapLayouts.WordSizeBytes);
                return IsArenaDeepCopyLayout(
                    tuple,
                    new HashSet<string>(StringComparer.Ordinal))
                        ? OrdinaryHeapStructuralCopyKind.Deep
                        : OrdinaryHeapStructuralCopyKind.None;
            case TypeRef.TNamedType named:
                return GetAdtStructuralCopyKind(named, out staticCopySizeBytes);
            default:
                staticCopySizeBytes = null;
                return OrdinaryHeapStructuralCopyKind.None;
        }
    }

    private OrdinaryHeapStructuralCopyKind GetAdtStructuralCopyKind(
        TypeRef.TNamedType named,
        out int? staticCopySizeBytes)
    {
        TypeSymbol symbol = named.Symbol;
        if (symbol.Constructors.Count == 0
            || IsResourceBearing(named))
        {
            staticCopySizeBytes = null;
            return OrdinaryHeapStructuralCopyKind.None;
        }

        int arity = symbol.Constructors[0].Arity;
        bool shallow = symbol.Constructors.All(constructor => constructor.Arity == arity);
        foreach (ConstructorSymbol constructor in symbol.Constructors)
        {
            for (int index = 0; index < constructor.Arity; index++)
            {
                TypeRef fieldType = Prune(
                    InstantiateConstructorParameterType(constructor, index, named));
                shallow &= CanArenaReset(fieldType);
            }
        }

        if (shallow)
        {
            staticCopySizeBytes = HeapLayouts.AdtLayout(IsTaglessAdt(symbol)).AllocationSizeBytes(arity);
            return OrdinaryHeapStructuralCopyKind.Shallow;
        }

        staticCopySizeBytes = null;
        return IsArenaDeepCopyAdtLayout(
            named,
            new HashSet<string>(StringComparer.Ordinal))
            ? OrdinaryHeapStructuralCopyKind.Deep
            : OrdinaryHeapStructuralCopyKind.None;
    }

    private bool IsArenaDeepCopyLayout(TypeRef type, HashSet<string> path)
    {
        TypeRef valueType = Prune(type);
        return valueType switch
        {
            _ when CanArenaReset(valueType) => true,
            TypeRef.TStr or TypeRef.TBytes => true,
            TypeRef.TList list => IsArenaDeepCopyLayout(list.Element, path),
            TypeRef.TTuple tuple => tuple.Elements.All(element =>
                IsArenaDeepCopyLayout(element, path)),
            TypeRef.TNamedType named => IsArenaDeepCopyAdtLayout(named, path),
            _ => false,
        };
    }

    private bool IsArenaDeepCopyAdtLayout(
        TypeRef.TNamedType named,
        HashSet<string> path)
    {
        TypeSymbol symbol = named.Symbol;
        if (symbol.Constructors.Count == 0
            || BuiltinRegistry.IsResourceTypeName(symbol.Name)
            || IsResourceBearing(named)
            || !path.Add(symbol.Name))
        {
            return false;
        }

        Dictionary<TypeParameterSymbol, TypeRef>? typeParamMap = null;
        if (symbol.TypeParameters.Count > 0
            && named.TypeArgs.Count == symbol.TypeParameters.Count)
        {
            typeParamMap = new Dictionary<TypeParameterSymbol, TypeRef>();
            for (int index = 0; index < symbol.TypeParameters.Count; index++)
            {
                typeParamMap[symbol.TypeParameters[index]] = named.TypeArgs[index];
            }
        }

        bool supported = true;
        foreach (ConstructorSymbol constructor in symbol.Constructors)
        {
            foreach (TypeRef fieldType in constructor.ParameterTypes)
            {
                TypeRef resolved = ResolveFieldType(fieldType, typeParamMap);
                if (!IsArenaDeepCopyLayout(resolved, path))
                {
                    supported = false;
                    break;
                }
            }

            if (!supported)
            {
                break;
            }
        }

        path.Remove(symbol.Name);
        return supported;
    }

    private bool CanDropOrdinaryValueGraph(TypeRef type, HashSet<TypeSymbol> path)
    {
        TypeRef valueType = Prune(type);
        if (CanArenaReset(valueType))
        {
            return true;
        }

        return valueType switch
        {
            TypeRef.TStr or TypeRef.TBytes or TypeRef.TBigInt => true,
            TypeRef.TList list => CanArenaReset(Prune(list.Element)),
            TypeRef.TTuple tuple => tuple.Elements.All(element =>
                CanDropOwnedTupleElement(element, path)),
            TypeRef.TNamedType named => CanDropAdtGraph(named, path),
            _ => false,
        };
    }

    private bool CanDropOwnedTupleElement(TypeRef type, HashSet<TypeSymbol> path)
    {
        TypeRef valueType = Prune(type);
        return CanArenaReset(valueType) || valueType switch
        {
            TypeRef.TStr or TypeRef.TBytes or TypeRef.TBigInt => true,
            TypeRef.TList list => CanArenaReset(Prune(list.Element)),
            TypeRef.TTuple tuple => tuple.Elements.All(element =>
                CanDropOwnedTupleElement(element, path)),
            _ => false,
        };
    }

    private bool CanDropAdtGraph(TypeRef.TNamedType named, HashSet<TypeSymbol> path)
    {
        if (IsResourceBearing(named))
        {
            return false;
        }

        if (!path.Add(named.Symbol))
        {
            return true;
        }

        foreach (ConstructorSymbol constructor in named.Symbol.Constructors)
        {
            for (int index = 0; index < constructor.Arity; index++)
            {
                TypeRef fieldType = Prune(
                    InstantiateConstructorParameterType(constructor, index, named));
                bool supported = CanArenaReset(fieldType) || fieldType switch
                {
                    TypeRef.TStr or TypeRef.TBytes or TypeRef.TBigInt => true,
                    TypeRef.TList list => CanArenaReset(Prune(list.Element)),
                    TypeRef.TTuple tuple => tuple.Elements.All(element =>
                        CanDropOwnedTupleElement(element, path)),
                    TypeRef.TNamedType child => CanDropAdtGraph(child, path),
                    _ => false,
                };
                if (!supported)
                {
                    path.Remove(named.Symbol);
                    return false;
                }
            }
        }

        path.Remove(named.Symbol);
        return true;
    }

    private bool IsRuntimeCopyAdtLayout(TypeRef.TNamedType named)
    {
        TypeSymbol symbol = named.Symbol;
        if (symbol.Constructors.Count == 0
            || BuiltinRegistry.IsResourceTypeName(symbol.Name)
            || IsResourceBearing(named))
        {
            return false;
        }

        return symbol.Constructors.All(constructor =>
            Enumerable.Range(0, constructor.Arity).All(index =>
                CanArenaReset(Prune(
                    InstantiateConstructorParameterType(constructor, index, named)))));
    }

    private bool IsRuntimeRecordAdtLayout(
        TypeRef.TNamedType named,
        HashSet<TypeSymbol> path)
    {
        TypeSymbol symbol = named.Symbol;
        if (symbol.Constructors.Count != 1
            || symbol.Constructors[0].DeclaringSyntax.FieldNames.Count == 0
            || BuiltinRegistry.IsResourceTypeName(symbol.Name)
            || IsResourceBearing(named)
            || !path.Add(symbol))
        {
            return false;
        }

        ConstructorSymbol constructor = symbol.Constructors[0];
        for (int index = 0; index < constructor.Arity; index++)
        {
            TypeRef fieldType = Prune(
                InstantiateConstructorParameterType(constructor, index, named));
            bool supported = CanArenaReset(fieldType) || fieldType switch
            {
                TypeRef.TStr or TypeRef.TBytes or TypeRef.TBigInt => true,
                TypeRef.TList list => CanArenaReset(Prune(list.Element)),
                TypeRef.TTuple tuple => tuple.Elements.All(element =>
                    CanDropOwnedTupleElement(element, path)),
                TypeRef.TNamedType child => IsRuntimeRecordAdtLayout(child, path),
                _ => false,
            };
            if (!supported)
            {
                path.Remove(symbol);
                return false;
            }
        }

        path.Remove(symbol);
        return true;
    }

    private bool IsRuntimeOwnedChildAdtLayout(
        TypeRef.TNamedType named,
        HashSet<TypeSymbol> path)
    {
        TypeSymbol symbol = named.Symbol;
        if (symbol.IsBuiltin
            || symbol.TypeParameters.Count > 0
            || symbol.Constructors.Count < 2
            || BuiltinRegistry.IsResourceTypeName(symbol.Name)
            || IsResourceBearing(named)
            || !path.Add(symbol))
        {
            return false;
        }

        bool hasOwnedChild = false;
        foreach (ConstructorSymbol constructor in symbol.Constructors)
        {
            for (int index = 0; index < constructor.Arity; index++)
            {
                TypeRef fieldType = Prune(
                    InstantiateConstructorParameterType(constructor, index, named));
                if (CanArenaReset(fieldType))
                {
                    continue;
                }

                bool supported = fieldType switch
                {
                    TypeRef.TStr or TypeRef.TBytes or TypeRef.TBigInt => true,
                    TypeRef.TList list => CanArenaReset(Prune(list.Element)),
                    TypeRef.TTuple tuple => tuple.Elements.All(element =>
                        CanDropOwnedTupleElement(element, path)),
                    TypeRef.TNamedType child =>
                        IsRuntimeRecordAdtLayout(child, new HashSet<TypeSymbol>())
                        || IsRuntimeTcoOwnedChildAdtLayout(child, path),
                    _ => false,
                };
                if (!supported)
                {
                    path.Remove(symbol);
                    return false;
                }

                hasOwnedChild = true;
            }
        }

        path.Remove(symbol);
        return hasOwnedChild;
    }

    private bool IsRuntimeTcoOwnedChildAdtLayout(
        TypeRef.TNamedType named,
        HashSet<TypeSymbol> path)
    {
        TypeSymbol symbol = named.Symbol;
        if (symbol.Constructors.Count != 1
            || symbol.Constructors[0].DeclaringSyntax.FieldNames.Count > 0)
        {
            return IsRuntimeOwnedChildAdtLayout(named, path);
        }

        if (symbol.IsBuiltin
            || symbol.TypeParameters.Count > 0
            || BuiltinRegistry.IsResourceTypeName(symbol.Name)
            || IsResourceBearing(named))
        {
            return false;
        }

        bool hasOwnedChild = false;
        ConstructorSymbol constructor = symbol.Constructors[0];
        for (int index = 0; index < constructor.Arity; index++)
        {
            TypeRef fieldType = Prune(
                InstantiateConstructorParameterType(constructor, index, named));
            if (CanArenaReset(fieldType))
            {
                continue;
            }

            if (fieldType is not TypeRef.TList list
                || !CanArenaReset(Prune(list.Element)))
            {
                return false;
            }

            hasOwnedChild = true;
        }

        return hasOwnedChild;
    }

    private bool IsRuntimeTcoListElementLayout(
        TypeRef type,
        HashSet<TypeSymbol> path)
    {
        TypeRef valueType = Prune(type);
        if (CanArenaReset(valueType))
        {
            return true;
        }

        return valueType switch
        {
            TypeRef.TStr or TypeRef.TBytes or TypeRef.TBigInt => true,
            TypeRef.TList list => IsRuntimeTcoListElementLayout(list.Element, path),
            TypeRef.TTuple tuple => tuple.Elements.All(element =>
                IsRuntimeTcoListElementLayout(element, path)),
            TypeRef.TNamedType named => IsShallowCopyAdtLayout(named)
                || IsRuntimeRecordAdtLayout(named, new HashSet<TypeSymbol>())
                || IsRuntimeOwnedChildAdtLayout(named, path),
            _ => false,
        };
    }

    private bool IsShallowCopyAdtLayout(TypeRef.TNamedType named)
    {
        return GetAdtStructuralCopyKind(
            named,
            out _) == OrdinaryHeapStructuralCopyKind.Shallow;
    }

    private bool ContainsBytesLayout(TypeRef type, HashSet<TypeSymbol> path)
    {
        TypeRef valueType = Prune(type);
        return valueType switch
        {
            TypeRef.TBytes => true,
            TypeRef.TList list => ContainsBytesLayout(list.Element, path),
            TypeRef.TTuple tuple => tuple.Elements.Any(element =>
                ContainsBytesLayout(element, path)),
            TypeRef.TNamedType named => ContainsBytesAdtLayout(named, path),
            _ => false,
        };
    }

    private bool ContainsBytesAdtLayout(
        TypeRef.TNamedType named,
        HashSet<TypeSymbol> path)
    {
        if (!path.Add(named.Symbol))
        {
            return false;
        }

        bool containsBytes = named.Symbol.Constructors.Any(constructor =>
            Enumerable.Range(0, constructor.Arity).Any(index =>
                ContainsBytesLayout(
                    InstantiateConstructorParameterType(constructor, index, named),
                    path)));
        path.Remove(named.Symbol);
        return containsBytes;
    }

    private bool ContainsUnresolvedLayoutType(
        TypeRef type,
        HashSet<TypeSymbol> path)
    {
        TypeRef valueType = Prune(type);
        switch (valueType)
        {
            case TypeRef.TVar or TypeRef.TTypeParam:
                return true;
            case TypeRef.TList list:
                return ContainsUnresolvedLayoutType(list.Element, path);
            case TypeRef.TTuple tuple:
                return tuple.Elements.Any(element =>
                    ContainsUnresolvedLayoutType(element, path));
            case TypeRef.TNamedType named:
                if (!path.Add(named.Symbol))
                {
                    return false;
                }

                bool unresolved = named.TypeArgs.Any(argument =>
                        ContainsUnresolvedLayoutType(argument, path))
                    || named.Symbol.Constructors.Any(constructor =>
                        Enumerable.Range(0, constructor.Arity).Any(index =>
                            ContainsUnresolvedLayoutType(
                                InstantiateConstructorParameterType(constructor, index, named),
                                path)));
                path.Remove(named.Symbol);
                return unresolved;
            default:
                return false;
        }
    }

    private List<OrdinaryHeapLayoutChild> DescribeOrdinaryHeapChildren(TypeRef type)
    {
        TypeRef valueType = Prune(type);
        var children = new List<OrdinaryHeapLayoutChild>();
        switch (valueType)
        {
            case TypeRef.TList list:
                children.Add(new OrdinaryHeapLayoutChild(
                    ConstructorName: null,
                    HeapLayouts.ListHeadIndex,
                    HeapLayouts.List.PayloadWordOffsetBytes(HeapLayouts.ListHeadIndex),
                    Prune(list.Element),
                    DropKindForType(list.Element)));
                children.Add(new OrdinaryHeapLayoutChild(
                    ConstructorName: null,
                    HeapLayouts.ListTailIndex,
                    HeapLayouts.List.PayloadWordOffsetBytes(HeapLayouts.ListTailIndex),
                    valueType,
                    OrdinaryHeapChildDropKind.List));
                break;
            case TypeRef.TTuple tuple:
                for (int index = 0; index < tuple.Elements.Count; index++)
                {
                    TypeRef element = Prune(tuple.Elements[index]);
                    children.Add(new OrdinaryHeapLayoutChild(
                        ConstructorName: null,
                        index,
                        checked(index * HeapLayouts.WordSizeBytes),
                        element,
                        DropKindForType(element)));
                }

                break;
            case TypeRef.TNamedType named:
                foreach (ConstructorSymbol constructor in named.Symbol.Constructors)
                {
                    for (int index = 0; index < constructor.Arity; index++)
                    {
                        TypeRef fieldType = Prune(
                            InstantiateConstructorParameterType(constructor, index, named));
                        children.Add(new OrdinaryHeapLayoutChild(
                            constructor.Name,
                            index,
                            AdtFieldOffsetBytes(constructor, index),
                            fieldType,
                            DropKindForType(fieldType)));
                    }
                }

                break;
        }

        return children;
    }

    private OrdinaryHeapChildDropKind DropKindForType(TypeRef type)
    {
        TypeRef valueType = Prune(type);
        if (CanArenaReset(valueType))
        {
            return OrdinaryHeapChildDropKind.None;
        }

        return valueType switch
        {
            TypeRef.TStr => OrdinaryHeapChildDropKind.String,
            TypeRef.TBytes => OrdinaryHeapChildDropKind.Bytes,
            TypeRef.TBigInt => OrdinaryHeapChildDropKind.BigInt,
            TypeRef.TList => OrdinaryHeapChildDropKind.List,
            TypeRef.TTuple => OrdinaryHeapChildDropKind.Tuple,
            TypeRef.TNamedType => OrdinaryHeapChildDropKind.Adt,
            _ => OrdinaryHeapChildDropKind.Unsupported,
        };
    }
}
