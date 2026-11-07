namespace Ashes.Semantics;

// A tuple literal built in the arena as the head of a reference-counted cell (`(fn.label, n) :: acc`
// over a reference-counted accumulator) holds the duplicates its pattern owners took for its
// elements, and the cell does not store it: the cell normalizes it into a reference-counted copy
// that takes references of its own to the elements. The arena tuple is garbage from then on, so
// the duplicates it holds are released once the copy is made, as a reference-counted tuple
// releases the duplicate of an element it normalizes itself. A back edge that copies the successor
// late would release them after its copy; a cell already reference-counted is never copied there,
// so the release here takes them over.
public sealed partial class Lowering
{
    // Per function body: the pattern-owner duplicates each arena tuple literal of the body holds.
    private readonly Dictionary<List<IrInst>, Dictionary<int, List<(int Temp, TypeRef Type)>>> _arenaTupleOwnerDuplicatesByBody =
        new(ReferenceEqualityComparer.Instance);

    private static bool NormalizedHeadTupleDuplicateReleaseEnabled
        => Environment.GetEnvironmentVariable("GRC_NO_NORMALIZEDHEADTUPLEDUP") is null;

    private void RecordArenaTupleOwnerDuplicates(int tupleTemp, List<(int Temp, TypeRef Type)> duplicates, bool runtimeManaged)
    {
        if (runtimeManaged || duplicates.Count == 0 || !NormalizedHeadTupleDuplicateReleaseEnabled)
        {
            return;
        }

        if (!_arenaTupleOwnerDuplicatesByBody.TryGetValue(_inst, out Dictionary<int, List<(int Temp, TypeRef Type)>>? byTuple))
        {
            byTuple = [];
            _arenaTupleOwnerDuplicatesByBody[_inst] = byTuple;
        }

        byTuple[tupleTemp] = new List<(int Temp, TypeRef Type)>(duplicates);
    }

    // Releases the duplicates an arena tuple literal held, now that a reference-counted copy of it
    // holds references of its own. Each release is a marker promoted together with its duplicate,
    // and a late release the back edge would have made of the same duplicate is withdrawn.
    private void ReleaseNormalizedHeadTupleDuplicates(int tupleTemp)
    {
        if (!_arenaTupleOwnerDuplicatesByBody.TryGetValue(_inst, out Dictionary<int, List<(int Temp, TypeRef Type)>>? byTuple)
            || !byTuple.Remove(tupleTemp, out List<(int Temp, TypeRef Type)>? duplicates)
            || !IsArenaCellConstruction(tupleTemp))
        {
            return;
        }

        foreach ((int temp, TypeRef type) in duplicates)
        {
            ReleaseNormalizedPatternOwnerDuplicate(temp, type);
            foreach (LateCopiedHeadTupleDuplicate late in _lateCopiedHeadTupleDuplicates)
            {
                if (late.Temp == temp && (_tcoCtx is null || ReferenceEquals(late.Loop, _tcoCtx)))
                {
                    late.Taken = true;
                }
            }
        }
    }
}
