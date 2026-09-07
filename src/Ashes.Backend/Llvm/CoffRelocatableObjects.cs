namespace Ashes.Backend.Llvm;

/// <summary>
/// Merges several COFF relocatable objects into one, the way a relocatable link does: sections
/// with the same name are concatenated with their alignment, every symbol (with its auxiliary
/// records) is rebased into the merged section it lands in, relocations keep their type and move
/// with their section, and an undefined symbol one object references resolves to the definition
/// another object provides. The relocation types are opaque to the merge, so the same code serves
/// the x64 and ARM64 Windows targets.
/// </summary>
internal static class CoffRelocatableObjects
{
    /// <summary>Whether <see cref="Merge"/> is implemented.</summary>
    public static bool MergeSupported => false;

    public static byte[] Merge(IReadOnlyList<byte[]> objects)
    {
        if (objects.Count == 1)
        {
            return objects[0];
        }

        throw new NotSupportedException("Merging COFF relocatable objects is not implemented yet.");
    }
}
