using Ashes.Backend.Llvm.Interop;

namespace Ashes.Backend.Llvm;

// Reference-counted blocks live inside one address region the program reserves when it starts, so
// compiled code can tell a reference-counted value from an arena one by its address alone: a
// consumer that needs an owned reference to a value of unknown representation takes a reference
// when the value is already reference-counted, and copies it only when it is not.
//
// The region is reserved (mapped inaccessible, never committed) when the program starts, and
// the memory it hands out is committed inside it, so nothing the runtime did not put there can ever
// have an address in it. Its bounds are run-time values: a program that cannot reserve any of the
// sizes it tries (an address-space limit, a small virtual address width) keeps an empty region,
// every test answers "not reference-counted", and every consumer copies exactly as it did before
// the region existed. Address space handed out is never handed out again; a program that runs
// through the whole reservation falls back to ordinary mappings, which test as "not
// reference-counted" as well.
internal static partial class LlvmCodegen
{
    private const string ReferenceCountedHeapGrowHelperName = "__ashes_rc_heap_grow";
    private const string ReferenceCountedRegionBaseName = "__ashes_rc_region_base";
    private const string ReferenceCountedRegionEndName = "__ashes_rc_region_end";
    private const string ReferenceCountedRegionNextName = "__ashes_rc_region_next";
    private const string ReferenceCountedRegionAllocateHelperName = "__ashes_rc_region_allocate";

    // The most address space the region asks for, how many sizes it tries, and how much smaller
    // each next one is.
    private const ulong ReferenceCountedRegionLargestBytes = 1UL << 42;
    private const int ReferenceCountedRegionAttempts = 3;
    private const int ReferenceCountedRegionRetryShift = 4;

    // Where the reservation is asked for; the kernel may place it anywhere, the bounds are whatever
    // it answers.
    private const ulong ReferenceCountedRegionHint = 1UL << 44;

    // Every page size a supported kernel uses divides this.
    private const ulong ReferenceCountedRegionGranule = 1UL << 16;

    private const long MmapProtNone = 0x0;
    private const long MmapProtReadWrite = 0x1 | 0x2;
    private const long MmapPrivateAnonymous = 0x02 | 0x20;
    private const long MmapFixed = 0x10;
    private const long MmapNoReserve = 0x4000;

    private static void AddReferenceCountedRegionGlobals(LlvmTargetContext target, LlvmTypeHandle i64)
    {
        AddZeroInitializedI64Global(target, i64, ReferenceCountedRegionBaseName);
        AddZeroInitializedI64Global(target, i64, ReferenceCountedRegionEndName);
        AddZeroInitializedI64Global(target, i64, ReferenceCountedRegionNextName);
    }

    private static LlvmValueHandle ReferenceCountedRegionGlobal(LlvmCodegenState state, string name)
        => LlvmApi.GetNamedGlobal(state.Target.Module, name);

    /// <summary>
    /// Reserves the region on the main thread before any other thread exists. A reservation counts
    /// against the address-space limit even though it commits nothing, so the first size asked for
    /// is a quarter of that limit, capped at <see cref="ReferenceCountedRegionLargestBytes"/>;
    /// smaller sizes follow for a kernel that refuses it anyway. With none granted the bounds stay
    /// zero, which is the empty region.
    /// </summary>
    private static void EmitReserveReferenceCountedRegion(LlvmCodegenState state)
    {
        if (!IsLinuxFlavor(state.Flavor))
        {
            return;
        }

        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle size = EmitReferenceCountedRegionFirstSize(state);
        LlvmBasicBlockHandle doneBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "rc_region_reserved");
        for (int attempt = 0; attempt < ReferenceCountedRegionAttempts; attempt++)
        {
            LlvmValueHandle mapped = EmitLinuxSyscall6(state, SyscallMmap,
                LlvmApi.ConstInt(state.I64, ReferenceCountedRegionHint, 0),
                size,
                LlvmApi.ConstInt(state.I64, (ulong)MmapProtNone, 0),
                LlvmApi.ConstInt(state.I64, (ulong)(MmapPrivateAnonymous | MmapNoReserve), 0),
                LlvmApi.ConstInt(state.I64, unchecked((ulong)(-1L)), 1),
                LlvmApi.ConstInt(state.I64, 0, 0),
                $"rc_region_reserve_{attempt}");
            // A failed mmap answers -errno, the last page of the address space.
            LlvmValueHandle failed = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ugt, mapped,
                LlvmApi.ConstInt(state.I64, unchecked((ulong)(-4096L)), 1), $"rc_region_failed_{attempt}");
            LlvmBasicBlockHandle grantedBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, $"rc_region_granted_{attempt}");
            LlvmBasicBlockHandle nextBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, $"rc_region_retry_{attempt}");
            LlvmApi.BuildCondBr(builder, failed, nextBlock, grantedBlock);

            LlvmApi.PositionBuilderAtEnd(builder, grantedBlock);
            LlvmApi.BuildStore(builder, mapped, ReferenceCountedRegionGlobal(state, ReferenceCountedRegionBaseName));
            LlvmApi.BuildStore(builder, mapped, ReferenceCountedRegionGlobal(state, ReferenceCountedRegionNextName));
            LlvmApi.BuildStore(builder, LlvmApi.BuildAdd(builder, mapped, size, $"rc_region_end_{attempt}"),
                ReferenceCountedRegionGlobal(state, ReferenceCountedRegionEndName));
            LlvmApi.BuildBr(builder, doneBlock);

            LlvmApi.PositionBuilderAtEnd(builder, nextBlock);
            size = LlvmApi.BuildLShr(builder, size, LlvmApi.ConstInt(state.I64, ReferenceCountedRegionRetryShift, 0), $"rc_region_smaller_{attempt}");
        }

        LlvmApi.BuildBr(builder, doneBlock);
        LlvmApi.PositionBuilderAtEnd(builder, doneBlock);
    }

    private static LlvmValueHandle EmitReferenceCountedRegionFirstSize(LlvmCodegenState state)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        // struct rlimit { rlim_cur; rlim_max }, read as "unlimited" when the query itself fails.
        LlvmValueHandle limit = LlvmApi.BuildAlloca(builder, LlvmApi.ArrayType2(state.I64, 2), "rc_region_limit");
        LlvmValueHandle limitAddress = LlvmApi.BuildPtrToInt(builder, limit, state.I64, "rc_region_limit_address");
        StoreMemory(state, limitAddress, 0, LlvmApi.ConstInt(state.I64, ulong.MaxValue, 0), "rc_region_limit_unlimited");
        const long rlimitAddressSpace = 9;
        EmitLinuxSyscall4(state, SyscallPrlimit64,
            LlvmApi.ConstInt(state.I64, 0, 0),
            LlvmApi.ConstInt(state.I64, (ulong)rlimitAddressSpace, 0),
            LlvmApi.ConstInt(state.I64, 0, 0),
            limitAddress,
            "rc_region_prlimit");
        LlvmValueHandle quarter = LlvmApi.BuildLShr(builder,
            LoadMemory(state, limitAddress, 0, "rc_region_limit_current"),
            LlvmApi.ConstInt(state.I64, 2, 0), "rc_region_limit_quarter");
        LlvmValueHandle largest = LlvmApi.ConstInt(state.I64, ReferenceCountedRegionLargestBytes, 0);
        LlvmValueHandle capped = LlvmApi.BuildSelect(builder,
            LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ult, quarter, largest, "rc_region_limit_binds"),
            quarter, largest, "rc_region_first_size");
        return LlvmApi.BuildAnd(builder, capped,
            LlvmApi.ConstInt(state.I64, ~(ReferenceCountedRegionGranule - 1), 0), "rc_region_first_size_aligned");
    }

    /// <summary>
    /// Memory for the reference-counted heap: the next stretch of the region, committed, or an
    /// ordinary mapping once the region is spent or was never granted. Answers what
    /// <see cref="EmitAllocateOsMemory"/> answers, failure included.
    /// </summary>
    private static LlvmValueHandle EmitAllocateReferenceCountedOsMemory(
        LlvmCodegenState state,
        LlvmValueHandle sizeBytes,
        string prefix)
    {
        if (!IsLinuxFlavor(state.Flavor))
        {
            return EmitAllocateOsMemory(state, sizeBytes, prefix);
        }

        LlvmTypeHandle helperType = LlvmApi.FunctionType(state.I64, [state.I64]);
        return LlvmApi.BuildCall2(state.Target.Builder, helperType,
            GetOrEmitReferenceCountedRegionAllocateHelper(state, helperType), [sizeBytes], prefix + "_region");
    }

    private static LlvmValueHandle GetOrEmitReferenceCountedRegionAllocateHelper(LlvmCodegenState state, LlvmTypeHandle helperType)
    {
        LlvmValueHandle existing = LlvmApi.GetNamedFunction(state.Target.Module, ReferenceCountedRegionAllocateHelperName);
        if (existing.Ptr != 0)
        {
            return existing;
        }

        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmBasicBlockHandle savedBlock = LlvmApi.GetInsertBlock(builder);
        LlvmValueHandle fn = LlvmApi.AddFunction(state.Target.Module, ReferenceCountedRegionAllocateHelperName, helperType);
        LlvmApi.SetLinkage(fn, LlvmLinkage.Internal);
        LlvmApi.AddAttributeAtIndex(fn, LlvmApi.AttributeIndexFunction,
            LlvmApi.CreateEnumAttribute(state.Target.Context, LlvmApi.GetEnumAttributeKindForName("noinline"), 0));
        LlvmApi.AddAttributeAtIndex(fn, LlvmApi.AttributeIndexFunction,
            LlvmApi.CreateEnumAttribute(state.Target.Context, LlvmApi.GetEnumAttributeKindForName("nounwind"), 0));
        LlvmCodegenState helperState = state with
        {
            Function = fn,
            TempSlots = [],
            LocalSlots = [],
            LabelBlocks = new Dictionary<string, LlvmBasicBlockHandle>(StringComparer.Ordinal),
            FallthroughBlocks = [],
            IsEntry = false,
        };
        LlvmApi.PositionBuilderAtEnd(builder, LlvmApi.AppendBasicBlockInContext(state.Target.Context, fn, "entry"));
        EmitReferenceCountedRegionAllocateBody(helperState, LlvmApi.GetParam(fn, 0));
        LlvmApi.PositionBuilderAtEnd(builder, savedBlock);
        return fn;
    }

    private static void EmitReferenceCountedRegionAllocateBody(LlvmCodegenState state, LlvmValueHandle sizeBytes)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle granule = LlvmApi.ConstInt(state.I64, ReferenceCountedRegionGranule - 1, 0);
        LlvmValueHandle rounded = LlvmApi.BuildAnd(builder,
            LlvmApi.BuildAdd(builder, sizeBytes, granule, "rc_region_size_up"),
            LlvmApi.ConstInt(state.I64, ~(ReferenceCountedRegionGranule - 1), 0), "rc_region_size");
        // Threads grow their own reference-counted heaps, so the stretch is claimed atomically.
        LlvmValueHandle nextAddress = LlvmApi.BuildPtrToInt(builder,
            ReferenceCountedRegionGlobal(state, ReferenceCountedRegionNextName), state.I64, "rc_region_next_address");
        LlvmValueHandle claimed = EmitAtomicFetchAdd(state, nextAddress, rounded, "rc_region_claim");
        LlvmValueHandle regionEnd = LlvmApi.BuildLoad2(builder, state.I64,
            ReferenceCountedRegionGlobal(state, ReferenceCountedRegionEndName), "rc_region_end");
        LlvmValueHandle claimedEnd = LlvmApi.BuildAdd(builder, claimed, rounded, "rc_region_claim_end");
        LlvmValueHandle fits = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ule, claimedEnd, regionEnd, "rc_region_fits");
        LlvmBasicBlockHandle insideBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "rc_region_inside");
        LlvmBasicBlockHandle outsideBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "rc_region_outside");
        LlvmApi.BuildCondBr(builder, fits, insideBlock, outsideBlock);

        LlvmApi.PositionBuilderAtEnd(builder, insideBlock);
        LlvmApi.BuildRet(builder, EmitLinuxSyscall6(state, SyscallMmap,
            claimed,
            rounded,
            LlvmApi.ConstInt(state.I64, (ulong)MmapProtReadWrite, 0),
            LlvmApi.ConstInt(state.I64, (ulong)(MmapPrivateAnonymous | MmapFixed), 0),
            LlvmApi.ConstInt(state.I64, unchecked((ulong)(-1L)), 1),
            LlvmApi.ConstInt(state.I64, 0, 0),
            "rc_region_commit"));

        LlvmApi.PositionBuilderAtEnd(builder, outsideBlock);
        LlvmApi.BuildRet(builder, EmitAllocateOsMemory(state, sizeBytes, "rc_region_fallback"));
    }

    /// <summary>1 when <paramref name="value"/> addresses memory inside the reserved region.</summary>
    private static LlvmValueHandle EmitIsReferenceCounted(LlvmCodegenState state, LlvmValueHandle value, string name)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle address = NormalizeToI64(state, value);
        LlvmValueHandle regionBase = LlvmApi.BuildLoad2(builder, state.I64,
            ReferenceCountedRegionGlobal(state, ReferenceCountedRegionBaseName), name + "_base");
        LlvmValueHandle regionEnd = LlvmApi.BuildLoad2(builder, state.I64,
            ReferenceCountedRegionGlobal(state, ReferenceCountedRegionEndName), name + "_end");
        LlvmValueHandle inside = LlvmApi.BuildAnd(builder,
            LlvmApi.BuildICmp(builder, LlvmIntPredicate.Uge, address, regionBase, name + "_above"),
            LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ult, address, regionEnd, name + "_below"),
            name + "_inside");
        return LlvmApi.BuildZExt(builder, inside, state.I64, name);
    }

    /// <summary>
    /// Gives a mapping back to the kernel. One inside the region is decommitted and stays reserved:
    /// unmapping it would leave a hole the kernel could fill with memory that is not
    /// reference-counted.
    /// </summary>
    private static void EmitReleaseLinuxMapping(LlvmCodegenState state, LlvmValueHandle basePtr, LlvmValueHandle sizeBytes, string prefix)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle inside = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne,
            EmitIsReferenceCounted(state, basePtr, prefix + "_in_region"),
            LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_in_region_flag");
        LlvmBasicBlockHandle keepBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_decommit");
        LlvmBasicBlockHandle unmapBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_unmap");
        LlvmBasicBlockHandle doneBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_released");
        LlvmApi.BuildCondBr(builder, inside, keepBlock, unmapBlock);

        LlvmApi.PositionBuilderAtEnd(builder, keepBlock);
        LlvmValueHandle granule = LlvmApi.ConstInt(state.I64, ReferenceCountedRegionGranule - 1, 0);
        LlvmValueHandle rounded = LlvmApi.BuildAnd(builder,
            LlvmApi.BuildAdd(builder, sizeBytes, granule, prefix + "_size_up"),
            LlvmApi.ConstInt(state.I64, ~(ReferenceCountedRegionGranule - 1), 0), prefix + "_size");
        EmitLinuxSyscall6(state, SyscallMmap,
            basePtr,
            rounded,
            LlvmApi.ConstInt(state.I64, (ulong)MmapProtNone, 0),
            LlvmApi.ConstInt(state.I64, (ulong)(MmapPrivateAnonymous | MmapFixed | MmapNoReserve), 0),
            LlvmApi.ConstInt(state.I64, unchecked((ulong)(-1L)), 1),
            LlvmApi.ConstInt(state.I64, 0, 0),
            prefix + "_decommit_mmap");
        LlvmApi.BuildBr(builder, doneBlock);

        LlvmApi.PositionBuilderAtEnd(builder, unmapBlock);
        EmitLinuxSyscall(state, SyscallMunmap, basePtr, sizeBytes, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_munmap");
        LlvmApi.BuildBr(builder, doneBlock);

        LlvmApi.PositionBuilderAtEnd(builder, doneBlock);
    }
}
