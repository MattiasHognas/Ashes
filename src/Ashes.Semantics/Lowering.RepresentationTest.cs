using Ashes.Frontend;

namespace Ashes.Semantics;

// A consumer that must own a reference-counted value (an aggregate storing a child, a loop taking
// its next parameter) is often handed one whose representation it cannot see: a parameter, a field
// of one, a pattern binding, a call result. It used to copy the whole value, because the value
// might be an arena one. In a state-threading pass that is a copy of everything left in the list
// at every step, for a value that was reference-counted all along.
//
// The runtime keeps reference-counted blocks inside one reserved address region, so the question has an
// answer at run time, and it costs two compares: a value inside the region is
// reference-counted and the consumer takes a reference; anything else is copied as before. A
// reference-counted aggregate's children are reference-counted by construction, so the reference
// is as good as the copy it replaces.
public sealed partial class Lowering
{
    // The instruction lists (one per function being lowered) that hold a representation test. A
    // value built there may share a child instead of owning a copy of it.
    private readonly HashSet<List<IrInst>> _representationTestedBodies = new(ReferenceEqualityComparer.Instance);

    private bool FunctionMaySharePossiblyReferenceCountedChildren => _representationTestedBodies.Contains(_inst);

    // A worker of a structured-parallelism construct frees its own reference-counted heap when it
    // exits, so a value of its is copied out, never shared.
    private bool CanTestRepresentation => AllowsParallelWorkerIndependentRcPlacement;

    // TEMPORARY bisect switch: a bitmask of guard sites to enable (1 deep copy, 2 list copy, 4 loop
    // entry, 8 back edge).
    private static readonly int GuardSites = int.TryParse(
        Environment.GetEnvironmentVariable("ASHES_RC_GUARDS"), System.Globalization.NumberStyles.Integer,
        System.Globalization.CultureInfo.InvariantCulture, out int sites) ? sites : 15;

    // TEMPORARY bisect switch: only the first GuardLimit guards are emitted; the last one names itself.
    private static readonly int GuardLimit = int.TryParse(
        Environment.GetEnvironmentVariable("ASHES_RC_GUARD_LIMIT"), System.Globalization.NumberStyles.Integer,
        System.Globalization.CultureInfo.InvariantCulture, out int limit) ? limit : int.MaxValue;

    private int _guardsEmitted;

    private bool GuardSiteEnabled(int site)
    {
        if (!CanTestRepresentation || (GuardSites & site) == 0 || _guardsEmitted >= GuardLimit)
        {
            return false;
        }

        _guardsEmitted++;
        if (_guardsEmitted == GuardLimit)
        {
            Console.Error.WriteLine($"[rc-guard] #{_guardsEmitted} site={site} in {_activeFunctionOrigin}");
        }

        return true;
    }

    /// <summary>
    /// An owned reference to <paramref name="sourceTemp"/>: a retain when the value lies in the
    /// reference-counted region, <paramref name="emitCopy"/> otherwise.
    /// </summary>
    private int EmitReferenceOrCopy(int sourceTemp, Func<int> emitCopy)
    {
        int referenceCountedTemp = NewTemp();
        Emit(new IrInst.IsReferenceCounted(referenceCountedTemp, sourceTemp));

        int resultSlot = NewLocal();
        string copyLabel = NewLabel("rc_representation_copy");
        string doneLabel = NewLabel("rc_representation_done");
        Emit(new IrInst.JumpIfFalse(referenceCountedTemp, copyLabel));
        int retainedTemp = NewTemp();
        Emit(new IrInst.RcDup(retainedTemp, sourceTemp, RuntimeManaged: true));
        Emit(new IrInst.StoreLocal(resultSlot, retainedTemp));
        Emit(new IrInst.Jump(doneLabel));
        Emit(new IrInst.Label(copyLabel));
        int copiedTemp = emitCopy();
        Emit(new IrInst.StoreLocal(resultSlot, copiedTemp));
        Emit(new IrInst.Label(doneLabel));
        int resultTemp = NewTemp();
        Emit(new IrInst.LoadLocal(resultTemp, resultSlot));

        // Either branch leaves an owned reference-counted value when the copy does, so the joined
        // value carries the fact the copy alone used to.
        if (IsRuntimeManagedResultTemp(copiedTemp))
        {
            MarkRuntimeManagedTemp(resultTemp);
        }

        RecordPossiblySharedChild();
        return resultTemp;
    }

    /// <summary>
    /// The tail a reference-counted cell takes over from the accumulator parameter it extends. The
    /// parameter's first value is whatever the caller passed, so a tail outside the reference-counted
    /// region is copied into it; one inside is moved into the cell exactly as it was before, with
    /// no reference taken, because the cell replaces the parameter as its holder.
    /// </summary>
    private int EmitReferenceCountedListTail(int tailTemp, TypeRef tailType)
    {
        if (!CanTestRepresentation || Prune(tailType) is not TypeRef.TList list)
        {
            return tailTemp;
        }

        int referenceCountedTemp = NewTemp();
        Emit(new IrInst.IsReferenceCounted(referenceCountedTemp, tailTemp));
        int resultSlot = NewLocal();
        string doneLabel = NewLabel("rc_tail_done");
        Emit(new IrInst.StoreLocal(resultSlot, tailTemp));
        // The empty list is the null pointer: nothing to copy and nothing that points anywhere.
        int zeroTemp = NewTemp();
        Emit(new IrInst.LoadConstInt(zeroTemp, 0));
        int emptyTemp = NewTemp();
        Emit(new IrInst.CmpIntEq(emptyTemp, tailTemp, zeroTemp));
        int keepTemp = NewTemp();
        Emit(new IrInst.OrInt(keepTemp, referenceCountedTemp, emptyTemp));
        string copyLabel = NewLabel("rc_tail_copy");
        Emit(new IrInst.JumpIfFalse(keepTemp, copyLabel));
        Emit(new IrInst.Jump(doneLabel));
        Emit(new IrInst.Label(copyLabel));
        Emit(new IrInst.StoreLocal(resultSlot, EmitRuntimeManagedTcoParamCopyByCopy(tailTemp, list)));
        Emit(new IrInst.Label(doneLabel));
        int resultTemp = NewTemp();
        Emit(new IrInst.LoadLocal(resultTemp, resultSlot));
        return resultTemp;
    }

    /// <summary>
    /// The child a reference-counted parent stores for a string bound out of a pattern. Such a
    /// string is whatever the matched value's strings are: retaining an arena one retains nothing,
    /// and the parent would keep a pointer into the arena. The parent takes a reference to a
    /// reference-counted string and a copy of any other, which stands in for the pattern-owner
    /// duplicate.
    /// </summary>
    private bool TryOwnPatternBoundStringChild(Expr argument, TypeRef fieldType, int sourceTemp, out int ownedTemp)
    {
        ownedTemp = sourceTemp;
        if (!CanTestRepresentation
            || Prune(fieldType) is not TypeRef.TStr
            || !IsPerceusPatternOwnerRead(argument)
            || _patternOwnerNormalizedTemps.Contains(sourceTemp))
        {
            return false;
        }

        ownedTemp = EmitReferenceOrCopy(sourceTemp, () =>
        {
            int copiedTemp = NewTemp();
            Emit(new IrInst.CopyOutArena(
                copiedTemp,
                sourceTemp,
                TcoRuntimeManagedCopySize(fieldType),
                RuntimeManaged: true,
                IrInst.CopyOutPurpose.RcNormalization));
            return copiedTemp;
        });
        MarkRuntimeManagedTemp(ownedTemp);
        _patternOwnerNormalizedTemps.Add(ownedTemp);
        return true;
    }

    // A retain in place of a copy leaves the value it was read from sharing that child, so no owner
    // live here may still be released as though it alone held everything beneath it.
    private void RecordPossiblySharedChild()
    {
        _representationTestedBodies.Add(_inst);
        foreach (Dictionary<string, OwnershipInfo> scope in _ownershipScopes)
        {
            foreach (OwnershipInfo owner in scope.Values)
            {
                owner.RuntimeDeepUnique = false;
            }
        }
    }
}
