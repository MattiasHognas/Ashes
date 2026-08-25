using Ashes.Frontend;

namespace Ashes.Semantics;

public sealed partial class Lowering
{
    // A type with exactly one constructor never needs a tag word: every value is that constructor.
    // Such a cell is laid out per HeapLayouts.TaglessAdt ([field0, field1, ...]) and every IR
    // instruction that allocates, reads or writes it carries Tagless = true, so the backend needs no
    // type information to compute an offset. This predicate is the one place that decision is made;
    // every emission site consults it through the helpers below rather than restating the rule.
    //
    // Excluded on purpose: nullary single-constructor types (a zero-byte cell would let distinct
    // values share an address), compiler-provided types (Unit, Option, Result and the other
    // builtins the backend's own intrinsics construct with the tagged layout), zero-cost newtypes
    // (already erased to their payload), resource handles, and resource-bearing aggregates (their
    // deterministic-cleanup walkers are kept on the tagged layout).
    private bool IsTaglessAdt(TypeSymbol symbol)
    {
        if (symbol.Constructors.Count != 1
            || symbol.IsBuiltin
            || symbol.IsZeroCost
            || BuiltinRegistry.IsResourceTypeName(symbol.Name))
        {
            return false;
        }

        ConstructorSymbol constructor = symbol.Constructors[0];
        if (constructor.Arity == 0 || constructor.IsBuiltin)
        {
            return false;
        }

        foreach (TypeRef parameterType in constructor.ParameterTypes)
        {
            if (IsResourceBearing(parameterType))
            {
                return false;
            }
        }

        return true;
    }

    private bool IsTaglessAdt(TypeRef.TNamedType named) => IsTaglessAdt(named.Symbol);

    /// <summary>
    /// True when a planned tag switch has exactly one case and that case's constructor is the sole
    /// constructor of a tagless type — the only situation in which the switch has nothing to test.
    /// Decided from the constructor (resolved by name), never from the scrutinee's inferred type,
    /// which can still be an unresolved variable at this point.
    /// </summary>
    private bool IsSoleTaglessCase(IReadOnlyList<MatchCase> cases) =>
        cases.Count == 1
        && TryGetConstructorSymbol(cases[0].Pattern, out ConstructorSymbol? soleConstructor)
        && IsTaglessConstructor(soleConstructor);

    private bool IsTaglessConstructor(ConstructorSymbol constructor) =>
        _typeSymbols.TryGetValue(constructor.ParentType, out TypeSymbol? symbol) && IsTaglessAdt(symbol);

    private int AdtAllocationSizeBytes(ConstructorSymbol constructor) =>
        HeapLayouts.AdtLayout(IsTaglessConstructor(constructor)).AllocationSizeBytes(constructor.Arity);

    private int AdtFieldOffsetBytes(ConstructorSymbol constructor, int fieldIndex) =>
        HeapLayouts.AdtLayout(IsTaglessConstructor(constructor)).PayloadWordOffsetBytes(fieldIndex);

    /// <summary>
    /// Emits the constructor tag of the ADT cell in <paramref name="valueTemp"/> into a fresh temp: a
    /// real tag load for a tagged type, the constant tag for a tagless one (whose cell has no tag
    /// word and whose only constructor is statically known).
    /// </summary>
    private int EmitAdtTag(int valueTemp, TypeSymbol symbol)
    {
        int tagTemp = NewTemp();
        if (IsTaglessAdt(symbol))
        {
            Emit(new IrInst.LoadConstInt(tagTemp, GetConstructorTag(symbol.Constructors[0])));
        }
        else
        {
            Emit(new IrInst.GetAdtTag(tagTemp, valueTemp));
        }

        return tagTemp;
    }
}
