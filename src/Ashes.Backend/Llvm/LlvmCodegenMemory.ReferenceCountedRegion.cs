using Ashes.Backend.Llvm.Interop;

namespace Ashes.Backend.Llvm;

// Reference-counted blocks are asked for inside one address region, so that compiled code can tell
// a reference-counted value from an arena one by its address alone: a consumer that needs an owned
// reference to a value of unknown representation takes a reference when the value is already
// reference-counted, and copies it only when it is not.
//
// The region is a request, never a guarantee. The mapping is made with an address hint and without
// MAP_FIXED, so the kernel places it elsewhere whenever the hinted range is taken, and a block
// outside the region reads as "not reference-counted" and is copied exactly as before. The opposite
// mistake, an arena block inside the region, would need an unhinted mapping to land sixteen
// terabytes below where the kernel starts looking, which takes exhausting the address space above
// it first.
internal static partial class LlvmCodegen
{
    private const string ReferenceCountedHeapGrowHelperName = "__ashes_rc_heap_grow";

    // [2^44, 2^45): below every address the kernel hands out unasked, above the image and the brk heap.
    private const int ReferenceCountedRegionShift = 44;
    private const ulong ReferenceCountedRegionBase = 1UL << ReferenceCountedRegionShift;
    private const ulong ReferenceCountedChunkAlignment = 1UL << 21;

    /// <summary>
    /// Maps a chunk for the reference-counted heap right after <paramref name="previousEnd"/>, the
    /// end of the chunk it extends, or at the region's base for a heap that has no chunk there
    /// yet. Targets without a placed mapping get an ordinary one.
    /// </summary>
    private static LlvmValueHandle EmitAllocateReferenceCountedOsMemory(
        LlvmCodegenState state,
        LlvmValueHandle sizeBytes,
        LlvmValueHandle previousEnd,
        string prefix)
    {
        if (!IsLinuxFlavor(state.Flavor))
        {
            return EmitAllocateOsMemory(state, sizeBytes, prefix);
        }

        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle regionIndex = LlvmApi.BuildLShr(builder, previousEnd,
            LlvmApi.ConstInt(state.I64, ReferenceCountedRegionShift, 0), prefix + "_prev_region");
        LlvmValueHandle extendsRegion = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, regionIndex,
            LlvmApi.ConstInt(state.I64, 1, 0), prefix + "_extends_region");
        LlvmValueHandle alignedEnd = LlvmApi.BuildAnd(builder,
            LlvmApi.BuildAdd(builder, previousEnd,
                LlvmApi.ConstInt(state.I64, ReferenceCountedChunkAlignment - 1, 0), prefix + "_prev_end_up"),
            LlvmApi.ConstInt(state.I64, ~(ReferenceCountedChunkAlignment - 1), 0), prefix + "_prev_end_aligned");
        LlvmValueHandle hint = LlvmApi.BuildSelect(builder, extendsRegion, alignedEnd,
            LlvmApi.ConstInt(state.I64, ReferenceCountedRegionBase, 0), prefix + "_hint");

        const long protReadWrite = 0x1 | 0x2;
        const long mapPrivateAnon = 0x02 | 0x20;
        return EmitLinuxSyscall6(state, SyscallMmap,
            hint,
            sizeBytes,
            LlvmApi.ConstInt(state.I64, (ulong)protReadWrite, 0),
            LlvmApi.ConstInt(state.I64, (ulong)mapPrivateAnon, 0),
            LlvmApi.ConstInt(state.I64, unchecked((ulong)(-1L)), 1),
            LlvmApi.ConstInt(state.I64, 0, 0),
            prefix + "_mmap");
    }

    // TEMPORARY diagnostic: a block in the reference-counted region must never come to point at
    // arena or stack memory. With ASHES_RC_VERIFY=1 every lowered store checks that and raises
    // SIGABRT at the offending store.
    private static readonly bool VerifyReferenceCountedStores =
        string.Equals(Environment.GetEnvironmentVariable("ASHES_RC_VERIFY"), "1", StringComparison.Ordinal);

    private static bool StoreLoweredMemory(LlvmCodegenState state, LlvmValueHandle baseAddress, int offsetBytes, LlvmValueHandle value, string name)
    {
        if (VerifyReferenceCountedStores && state.Flavor == LlvmCodegenFlavor.LinuxX64)
        {
            EmitVerifyReferenceCountedStore(state, baseAddress, NormalizeToI64(state, value), name);
        }

        return StoreMemory(state, baseAddress, offsetBytes, value, name);
    }

    private static void EmitVerifyReferenceCountedStore(LlvmCodegenState state, LlvmValueHandle baseAddress, LlvmValueHandle value, string name)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle baseInRegion = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq,
            LlvmApi.BuildLShr(builder, NormalizeToI64(state, baseAddress), LlvmApi.ConstInt(state.I64, ReferenceCountedRegionShift, 0), name + "_verify_base_region"),
            LlvmApi.ConstInt(state.I64, 1, 0), name + "_verify_base_rc");
        LlvmValueHandle valueOutside = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq,
            LlvmApi.BuildLShr(builder, value, LlvmApi.ConstInt(state.I64, 40, 0), name + "_verify_value_high"),
            LlvmApi.ConstInt(state.I64, 0x7f, 0), name + "_verify_value_outside");
        LlvmValueHandle violates = LlvmApi.BuildAnd(builder, baseInRegion, valueOutside, name + "_verify_violates");
        LlvmBasicBlockHandle abortBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, name + "_verify_abort");
        LlvmBasicBlockHandle okBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, name + "_verify_ok");
        LlvmApi.BuildCondBr(builder, violates, abortBlock, okBlock);
        LlvmApi.PositionBuilderAtEnd(builder, abortBlock);
        LlvmValueHandle zero = LlvmApi.ConstInt(state.I64, 0, 0);
        LlvmValueHandle pid = EmitLinuxSyscall(state, 39, zero, zero, zero, name + "_verify_getpid");
        EmitLinuxSyscall(state, 62, pid, LlvmApi.ConstInt(state.I64, 6, 0), zero, name + "_verify_kill");
        LlvmApi.BuildBr(builder, okBlock);
        LlvmApi.PositionBuilderAtEnd(builder, okBlock);
    }
}
