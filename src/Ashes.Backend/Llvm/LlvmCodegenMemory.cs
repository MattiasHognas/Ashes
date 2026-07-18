using Ashes.Backend.Llvm.Interop;
using Ashes.Semantics;
using static Ashes.Semantics.IrInst;

namespace Ashes.Backend.Llvm;

internal static partial class LlvmCodegen
{
    /// <summary>
    /// Creates a module-level global constant byte array and returns a pointer
    /// to its first element. The global is marked internal linkage, constant,
    /// and unnamed_addr so LLVM can merge duplicates and place it in .rodata.
    /// </summary>
    private static LlvmValueHandle CreateGlobalConstantBytes(LlvmCodegenState state, IReadOnlyList<byte> bytes, string prefix)
    {
        int id = state.Target.NextGlobalConstantId();
        LlvmTypeHandle arrayType = LlvmApi.ArrayType2(state.I8, (ulong)bytes.Count);

        // Build constant initializer: [N x i8] c"..."
        var elements = new LlvmValueHandle[bytes.Count];
        for (int i = 0; i < bytes.Count; i++)
        {
            elements[i] = LlvmApi.ConstInt(state.I8, bytes[i], 0);
        }

        LlvmValueHandle constArray = LlvmApi.ConstArray2(state.I8, elements);

        // Create a global variable with the constant data
        LlvmValueHandle global = LlvmApi.AddGlobal(state.Target.Module, arrayType, $".{prefix}_{id}");
        LlvmApi.SetInitializer(global, constArray);
        LlvmApi.SetLinkage(global, LlvmLinkage.Internal);
        LlvmApi.SetGlobalConstant(global, 1);
        LlvmApi.SetUnnamedAddr(global, 1); // LocalUnnamedAddr

        // Return pointer to the first byte
        return LlvmApi.BuildGEP2(state.Target.Builder,
            arrayType,
            global,
            [LlvmApi.ConstInt(state.I64, 0, 0), LlvmApi.ConstInt(state.I64, 0, 0)],
            prefix + "_ptr");
    }

    private static LlvmValueHandle EmitAlloc(LlvmCodegenState state, int sizeBytes)
        => EmitAlloc(state, sizeBytes, state.HeapCursorSlot, state.HeapEndSlot);

    /// <summary>
    /// Returns the address (as i64) of a zero-initialized, process-lifetime BSS buffer of
    /// <paramref name="sizeBytes"/>, uniquely identified by <paramref name="name"/>. Unlike
    /// <see cref="EmitAlloc(LlvmCodegenState,int)"/>, this storage lives outside any arena, so the
    /// async loop's per-iteration (TCO back-edge) arena reset never reclaims it. Use for true
    /// process singletons — the TLS runtime context, server config, and certified key — which are
    /// built once, cached in globals, and referenced again on later connections after the accept
    /// loop has reset the main arena. Zero init matches the mbedtls_*_init contract.
    /// </summary>
    private static LlvmValueHandle EmitTlsSingletonStorage(LlvmCodegenState state, string name, int sizeBytes)
    {
        LlvmTargetContext target = state.Target;
        LlvmTypeHandle bufferType = LlvmApi.ArrayType2(state.I8, (ulong)AlignRuntimeSize(sizeBytes));
        LlvmValueHandle global = target.GetOrAddNamedGlobal(name, () =>
        {
            LlvmValueHandle g = LlvmApi.AddGlobal(target.Module, bufferType, name);
            LlvmApi.SetInitializer(g, LlvmApi.ConstNull(bufferType));
            LlvmApi.SetLinkage(g, LlvmLinkage.Internal);
            return g;
        });
        return LlvmApi.BuildPtrToInt(target.Builder, global, state.I64, name + "_addr");
    }

    /// <summary>
    /// Bump-allocates from the arena identified by <paramref name="cursorSlot"/>/<paramref name="endSlot"/>
    /// — the main arena by default, or the persistent to-space (see <see cref="IrInst.AllocAdtToSpace"/>).
    /// Both arenas share the chunk format and grow logic; they differ only in which cursor/end pair is
    /// bumped and (for to-space) that they are never reset by the TCO back-edge.
    /// </summary>
    private static LlvmValueHandle EmitAlloc(LlvmCodegenState state, int sizeBytes, LlvmValueHandle cursorSlot, LlvmValueHandle endSlot)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle sizeConst = LlvmApi.ConstInt(state.I64, (ulong)AlignRuntimeSize(sizeBytes), 0);
        EmitHeapEnsureSpace(state, sizeConst, cursorSlot, endSlot);
        // After EnsureSpace the cursor points to valid space in the current chunk.
        LlvmValueHandle cursor = LlvmApi.BuildLoad2(builder, state.I64, cursorSlot, "heap_cursor_value");
        LlvmValueHandle nextCursor = LlvmApi.BuildAdd(builder, cursor, sizeConst, "heap_cursor_next");
        LlvmApi.BuildStore(builder, nextCursor, cursorSlot);
        return cursor;
    }

    private static LlvmValueHandle EmitAllocAdtToSpace(LlvmCodegenState state, int tag, int fieldCount)
    {
        LlvmValueHandle ptr = EmitAlloc(state, (1 + fieldCount) * 8, state.ToSpaceCursorSlot, state.ToSpaceEndSlot);
        StoreMemory(state, ptr, 0, LlvmApi.ConstInt(state.I64, (ulong)tag, 0), $"tospace_adt_tag_{tag}");
        return ptr;
    }

    private static LlvmValueHandle EmitStackAlloc(LlvmCodegenState state, int sizeBytes, string name)
    {
        int alignedSizeBytes = AlignRuntimeSize(sizeBytes);
        LlvmTypeHandle bufferType = LlvmApi.ArrayType2(state.I64, (ulong)(alignedSizeBytes / 8));
        LlvmValueHandle bufferPtr = LlvmApi.BuildAlloca(state.Target.Builder, bufferType, name);
        LlvmValueHandle bytePtr = LlvmApi.BuildBitCast(state.Target.Builder, bufferPtr, state.I8Ptr, name + "_i8");
        return LlvmApi.BuildPtrToInt(state.Target.Builder, bytePtr, state.I64, name + "_addr");
    }

    private static int AlignRuntimeSize(int sizeBytes)
    {
        return (sizeBytes + 7) & ~7;
    }

    private static LlvmValueHandle EmitAllocAdt(LlvmCodegenState state, int tag, int fieldCount)
    {
        LlvmValueHandle ptr = EmitAlloc(state, (1 + fieldCount) * 8);
        StoreMemory(state, ptr, 0, LlvmApi.ConstInt(state.I64, (ulong)tag, 0), $"adt_tag_{tag}");
        return ptr;
    }

    private static LlvmValueHandle EmitStackAllocAdt(LlvmCodegenState state, int tag, int fieldCount)
    {
        LlvmValueHandle ptr = EmitStackAlloc(state, (1 + fieldCount) * 8, $"adt_stack_{tag}");
        StoreMemory(state, ptr, 0, LlvmApi.ConstInt(state.I64, (ulong)tag, 0), $"adt_stack_tag_{tag}");
        return ptr;
    }

    /// <summary>
    /// In-place reuse: overwrites the dead cell at <paramref name="tokenPtr"/> with the new
    /// constructor tag and returns it as the new ADT, with no bump allocation. The token is a
    /// uniquely-owned cell of the same size that was just deconstructed; its fields are rewritten by
    /// the following StoreMemOffset/SetAdtField. See <see cref="IrInst.AllocReusing"/>.
    /// </summary>
    private static LlvmValueHandle EmitAllocReusing(LlvmCodegenState state, LlvmValueHandle tokenPtr, int tag)
    {
        StoreMemory(state, tokenPtr, 0, LlvmApi.ConstInt(state.I64, (ulong)tag, 0), $"adt_reuse_tag_{tag}");
        return tokenPtr;
    }

    private static bool StoreMemory(LlvmCodegenState state, LlvmValueHandle baseAddress, int offsetBytes, LlvmValueHandle value, string name)
    {
        LlvmValueHandle ptr = GetMemoryPointer(state, baseAddress, offsetBytes, name + "_ptr");
        LlvmApi.BuildStore(state.Target.Builder, NormalizeToI64(state, value), ptr);
        return false;
    }

    private static LlvmValueHandle LoadMemory(LlvmCodegenState state, LlvmValueHandle baseAddress, int offsetBytes, string name)
    {
        LlvmValueHandle ptr = GetMemoryPointer(state, baseAddress, offsetBytes, name + "_ptr");
        return LlvmApi.BuildLoad2(state.Target.Builder, state.I64, ptr, name);
    }

    private static LlvmValueHandle GetMemoryPointer(LlvmCodegenState state, LlvmValueHandle baseAddress, int offsetBytes, string name)
    {
        LlvmValueHandle basePtr = LlvmApi.BuildIntToPtr(state.Target.Builder, baseAddress, state.I8Ptr, name + "_base");
        LlvmValueHandle bytePtr = LlvmApi.BuildGEP2(state.Target.Builder,
            state.I8,
            basePtr,
            [
                LlvmApi.ConstInt(state.I64, (ulong)offsetBytes, 0)
            ],
            name + "_byte");
        return LlvmApi.BuildBitCast(state.Target.Builder, bytePtr, state.I64Ptr, name);
    }

    private static LlvmValueHandle EmitStringComparison(LlvmCodegenState state, LlvmValueHandle leftRef, LlvmValueHandle rightRef)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle resultSlot = LlvmApi.BuildAlloca(builder, state.I64, "str_cmp_result");
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), resultSlot);

        LlvmValueHandle leftLen = LoadStringLength(state, leftRef, "str_cmp_left_len");
        LlvmValueHandle rightLen = LoadStringLength(state, rightRef, "str_cmp_right_len");
        LlvmValueHandle leftBytes = GetStringBytesPointer(state, leftRef, "str_cmp_left_bytes");
        LlvmValueHandle rightBytes = GetStringBytesPointer(state, rightRef, "str_cmp_right_bytes");

        var lenEqBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "str_cmp_len_eq");
        var notEqBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "str_cmp_not_eq");
        var eqBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "str_cmp_eq");
        var continueBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "str_cmp_continue");

        // If lengths differ → not equal
        LlvmValueHandle lenEq = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, leftLen, rightLen, "str_cmp_len_match");
        LlvmApi.BuildCondBr(builder, lenEq, lenEqBlock, notEqBlock);

        LlvmApi.PositionBuilderAtEnd(builder, notEqBlock);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), resultSlot);
        LlvmApi.BuildBr(builder, continueBlock);

        // Lengths equal → call memcmp to compare byte data
        LlvmApi.PositionBuilderAtEnd(builder, lenEqBlock);
        LlvmTypeHandle i32 = LlvmApi.Int32TypeInContext(state.Target.Context);
        LlvmTypeHandle memcmpType = LlvmApi.FunctionType(i32, [state.I8Ptr, state.I8Ptr, state.I64]);
        LlvmValueHandle memcmpFn = LlvmApi.GetNamedFunction(state.Target.Module, "memcmp");
        LlvmValueHandle cmpResult = LlvmApi.BuildCall2(builder, memcmpType, memcmpFn,
            [leftBytes, rightBytes, leftLen], "str_cmp_memcmp");
        LlvmValueHandle isZero = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, cmpResult, LlvmApi.ConstInt(i32, 0, 0), "str_cmp_is_eq");
        LlvmApi.BuildCondBr(builder, isZero, eqBlock, notEqBlock);

        LlvmApi.PositionBuilderAtEnd(builder, eqBlock);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 1, 0), resultSlot);
        LlvmApi.BuildBr(builder, continueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, continueBlock);
        return LlvmApi.BuildLoad2(builder, state.I64, resultSlot, "str_cmp_result_value");
    }

    private static LlvmValueHandle EmitStringConcat(LlvmCodegenState state, LlvmValueHandle leftRef, LlvmValueHandle rightRef)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle leftLen = LoadStringLength(state, leftRef, "str_cat_left_len");
        LlvmValueHandle rightLen = LoadStringLength(state, rightRef, "str_cat_right_len");
        LlvmValueHandle totalLen = LlvmApi.BuildAdd(builder, leftLen, rightLen, "str_cat_total_len");
        LlvmValueHandle totalBytes = LlvmApi.BuildAdd(builder, totalLen, LlvmApi.ConstInt(state.I64, 8, 0), "str_cat_total_bytes");
        LlvmValueHandle destRef = EmitAllocDynamic(state, totalBytes);
        StoreMemory(state, destRef, 0, totalLen, "str_cat_len");

        LlvmValueHandle destBytes = GetStringBytesPointer(state, destRef, "str_cat_dest_bytes");
        LlvmValueHandle leftBytes = GetStringBytesPointer(state, leftRef, "str_cat_left_bytes");
        LlvmValueHandle rightBytes = GetStringBytesPointer(state, rightRef, "str_cat_right_bytes");
        EmitCopyBytes(state, destBytes, leftBytes, leftLen, "str_cat_copy_left");
        LlvmValueHandle rightDest = LlvmApi.BuildGEP2(builder, state.I8, destBytes, [leftLen], "str_cat_right_dest");
        EmitCopyBytes(state, rightDest, rightBytes, rightLen, "str_cat_copy_right");
        return destRef;
    }

    private static void EmitCopyBytes(LlvmCodegenState state, LlvmValueHandle destBytes, LlvmValueHandle sourceBytes, LlvmValueHandle length, string prefix)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        // Use llvm.memcpy intrinsic — LLVM will lower this to vectorized
        // copies (rep movsb / movaps) instead of a byte-by-byte loop.
        LlvmApi.BuildMemCpy(builder, destBytes, 1, sourceBytes, 1, length);
    }

    // String header word: bits [0..62] = byte length, bit 63 = "view" flag. An owned string is
    // {len, inline bytes…}; a view is {len|VIEW, backing-bytes-pointer} pointing into another
    // string's bytes (no copy), used by uncons/substring. Views are materialized to owned strings by
    // the copy-out / deep-copy paths, so a value that crosses an arena reset is never a dangling view.
    private const ulong StringViewFlag = 1UL << 63;

    private static LlvmValueHandle LoadStringLength(LlvmCodegenState state, LlvmValueHandle stringRef, string name)
    {
        LlvmValueHandle raw = LoadMemory(state, stringRef, 0, name + "_raw");
        return LlvmApi.BuildAnd(state.Target.Builder, raw, LlvmApi.ConstInt(state.I64, ~StringViewFlag, 0), name);
    }

    private static LlvmValueHandle GetStringBytesPointer(LlvmCodegenState state, LlvmValueHandle stringRef, string name)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        // Branchless: owned → bytes inline at ref+8; view → backing pointer stored at ref+8.
        LlvmValueHandle raw = LoadMemory(state, stringRef, 0, name + "_hdr");
        LlvmValueHandle isView = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne,
            LlvmApi.BuildAnd(builder, raw, LlvmApi.ConstInt(state.I64, StringViewFlag, 0), name + "_view_bit"),
            LlvmApi.ConstInt(state.I64, 0, 0), name + "_is_view");
        LlvmValueHandle inlineAddr = LlvmApi.BuildAdd(builder, stringRef, LlvmApi.ConstInt(state.I64, 8, 0), name + "_inline_addr");
        LlvmValueHandle viewPtr = LoadMemory(state, stringRef, 8, name + "_view_ptr");
        LlvmValueHandle byteAddress = LlvmApi.BuildSelect(builder, isView, viewPtr, inlineAddr, name + "_addr");
        return LlvmApi.BuildIntToPtr(builder, byteAddress, state.I8Ptr, name);
    }

    // Builds a view string {len|VIEW, backingBytesPtr} — O(1), no byte copy. The backing must outlive
    // the view (the copy-out/deep-copy paths materialize views before a value crosses an arena reset).
    private static LlvmValueHandle EmitStringView(LlvmCodegenState state, LlvmValueHandle bytesPtr, LlvmValueHandle len, string prefix)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle normalizedLen = NormalizeToI64(state, len);
        LlvmValueHandle viewRef = EmitAlloc(state, 16);
        LlvmValueHandle tagged = LlvmApi.BuildOr(builder, normalizedLen, LlvmApi.ConstInt(state.I64, StringViewFlag, 0), prefix + "_tagged_len");
        StoreMemory(state, viewRef, 0, tagged, prefix + "_len");
        LlvmValueHandle bytesAsInt = LlvmApi.BuildPtrToInt(builder, bytesPtr, state.I64, prefix + "_bytes_int");
        StoreMemory(state, viewRef, 8, bytesAsInt, prefix + "_ptr");
        return viewRef;
    }

    private static LlvmValueHandle EmitAllocDynamic(LlvmCodegenState state, LlvmValueHandle sizeBytes)
        => EmitAllocDynamic(state, sizeBytes, state.HeapCursorSlot, state.HeapEndSlot);

    private static LlvmValueHandle EmitAllocDynamic(LlvmCodegenState state, LlvmValueHandle sizeBytes, LlvmValueHandle cursorSlot, LlvmValueHandle endSlot)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle normalizedSize = AlignRuntimeSize(state, sizeBytes, "heap_alloc_size");
        EmitHeapEnsureSpace(state, normalizedSize, cursorSlot, endSlot);
        LlvmValueHandle cursor = LlvmApi.BuildLoad2(builder, state.I64, cursorSlot, "heap_cursor_value_dyn");
        LlvmValueHandle nextCursor = LlvmApi.BuildAdd(builder, cursor, normalizedSize, "heap_cursor_next_dyn");
        LlvmApi.BuildStore(builder, nextCursor, cursorSlot);
        return cursor;
    }

    private static LlvmValueHandle AlignRuntimeSize(LlvmCodegenState state, LlvmValueHandle sizeBytes, string name)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle normalizedSize = NormalizeToI64(state, sizeBytes);
        LlvmValueHandle plusSeven = LlvmApi.BuildAdd(builder, normalizedSize, LlvmApi.ConstInt(state.I64, 7, 0), name + "_plus_7");
        return LlvmApi.BuildAnd(builder, plusSeven, LlvmApi.ConstInt(state.I64, 0xFFFFFFFFFFFFFFF8UL, 0), name + "_aligned");
    }

    /// <summary>
    /// Allocates the initial heap chunk at program entry via mmap (Linux) or VirtualAlloc (Windows).
    /// Sets __ashes_heap_cursor and __ashes_heap_end globals.
    /// The first 8 bytes of each chunk store the base address of the previous chunk (0 for the
    /// first chunk). This linked-list header enables RestoreArenaState to walk back and reclaim
    /// OS chunks that are no longer reachable after an arena reset.
    /// </summary>
    /// <summary>
    /// Points the GS segment base at the main thread's control block (TCB) and writes the
    /// TCB self-pointer at offset 0, then returns the TCB base as an i64. Emitted once in the
    /// entry prologue on linux-x64. GS (not FS) is used because the dynamically linked glibc
    /// runtime owns the FS base for its own thread-local storage; GS is free for application
    /// use on x86-64 Linux. The TCB is a zero-initialised BSS global (can't fail, no syscall
    /// to allocate it); worker threads instead get a freshly mmap'd TCB at spawn time.
    /// Lifted functions recover this same base via <see cref="EmitReadTcbBaseFromGs"/>.
    /// </summary>
    private static LlvmValueHandle EmitMainThreadTlsInit(LlvmCodegenState state)
    {
        LlvmTypeHandle tcbType = LlvmApi.ArrayType2(state.I8, (ulong)MainTcbSizeBytes);
        LlvmValueHandle tcb = LlvmApi.AddGlobal(state.Target.Module, tcbType, "__ashes_main_tcb");
        LlvmApi.SetLinkage(tcb, LlvmLinkage.Internal);
        LlvmApi.SetInitializer(tcb, LlvmApi.ConstNull(tcbType));
        LlvmValueHandle tcbAddr = LlvmApi.BuildPtrToInt(state.Target.Builder, tcb, state.I64, "main_tcb_addr");
        // Point GS base at the TCB.
        EmitLinuxSyscall(state, SyscallArchPrctl,
            LlvmApi.ConstInt(state.I64, (ulong)ArchSetGs, 0),
            tcbAddr,
            LlvmApi.ConstInt(state.I64, 0, 0),
            "arch_set_gs");
        // Store the TCB self-pointer at offset 0 so lifted functions can recover the base
        // through `movq %gs:0` (see EmitReadTcbBaseFromGs).
        StoreMemory(state, tcbAddr, (int)TcbSelfOffset, tcbAddr, "tcb_self_ptr");
        return tcbAddr;
    }

    /// <summary>
    /// Reads the current thread's TCB base from the GS self-pointer using a single opaque
    /// inline-asm <c>movq %gs:0, $0</c>. Going through inline asm (rather than an
    /// address-space-256 pointer) keeps the loaded value free of segment provenance, which
    /// the O0 FastISel path otherwise mis-propagates into subsequent ordinary stores.
    /// </summary>
    private static LlvmValueHandle EmitReadTcbBaseFromGs(LlvmCodegenState state)
    {
        LlvmTypeHandle readType = LlvmApi.FunctionType(state.I64, []);
        // No side effects / no memory clobber: the self-pointer is invariant for the thread's
        // lifetime, so LLVM may hoist and CSE the read (e.g. out of allocation loops).
        LlvmValueHandle read = LlvmApi.GetInlineAsm(readType, "movq %gs:0, $0", "=r", false, false);
        return LlvmApi.BuildCall2(state.Target.Builder, readType, read, [], "tcb_base");
    }

    // win-x64 per-thread arena: the TCB base pointer lives in the TEB's ArbitraryUserPointer field
    // (TEB+0x28, reached via the GS-based TEB), a slot Windows never uses and that is genuinely
    // per-thread. This mirrors the linux GS:0 self-pointer approach — no TlsAlloc, no collision with a
    // loaded runtime's TLS indices, and a cheap inline read that LLVM can hoist/CSE.
    private const int WindowsTebArenaSlotOffset = 0x28;

    private static LlvmValueHandle EmitReadTcbBaseFromTeb(LlvmCodegenState state)
    {
        LlvmTypeHandle readType = LlvmApi.FunctionType(state.I64, []);
        LlvmValueHandle read = LlvmApi.GetInlineAsm(readType, $"movq %gs:{WindowsTebArenaSlotOffset}, $0", "=r", false, false);
        return LlvmApi.BuildCall2(state.Target.Builder, readType, read, [], "teb_tcb_base");
    }

    private static void EmitWriteTcbBaseToTeb(LlvmCodegenState state, LlvmValueHandle tcbAddr)
    {
        LlvmTypeHandle writeType = LlvmApi.FunctionType(LlvmApi.VoidTypeInContext(state.Target.Context), [state.I64]);
        LlvmValueHandle write = LlvmApi.GetInlineAsm(writeType, $"movq $0, %gs:{WindowsTebArenaSlotOffset}", "r,~{memory}", true, false);
        LlvmApi.BuildCall2(state.Target.Builder, writeType, write, [tcbAddr], "");
    }

    // arm64 main-thread arena setup, safe for both static and dynamic executables. The per-thread
    // arena cursors are local-exec TLS (TPREL = 16-byte TCB reserve + their block offset). Who owns
    // TPIDR_EL0 differs by link kind:
    //   * Static executable (pure compute / parallelism): no loader runs, so the kernel leaves
    //     TPIDR_EL0 zero. We point it at a zeroed BSS block that backs the arena — MainTcbSizeBytes
    //     (512) easily covers the 16-byte reserve + the six i64 cursors.
    //   * Dynamic executable (networking's libc imports; user externals): the loader already set up
    //     TPIDR_EL0 and the static-TLS block (reserving this image's PT_TLS at the same TPREL the
    //     linker baked in), and its DTV backs libc's dynamic TLS. We must NOT clobber
    //     it — doing so breaks libc thread-local access.
    // Rather than predict the link kind at codegen time, branch on TPIDR_EL0 at runtime: a loader
    // always leaves it non-zero, an unloaded static image leaves it zero. This makes the same entry
    // prologue correct for both, so networking (dynamic) and parallelism (which needs the TLS arena
    // to give each `both` worker its own arena) coexist on arm64.
    private static void EmitArm64MainThreadTlsSetup(LlvmCodegenState state)
    {
        LlvmTypeHandle blockType = LlvmApi.ArrayType2(state.I8, (ulong)MainTcbSizeBytes);
        LlvmValueHandle block = state.Target.GetOrAddNamedGlobal("__ashes_arm64_tls_block", () =>
        {
            LlvmValueHandle g = LlvmApi.AddGlobal(state.Target.Module, blockType, "__ashes_arm64_tls_block");
            LlvmApi.SetInitializer(g, LlvmApi.ConstNull(blockType));
            LlvmApi.SetLinkage(g, LlvmLinkage.Internal);
            return g;
        });
        LlvmValueHandle blockAddr = LlvmApi.BuildPtrToInt(state.Target.Builder, block, state.I64, "arm64_tls_block_addr");
        // if (TPIDR_EL0 == 0) TPIDR_EL0 = blockAddr;  — keep a loader-provided thread pointer intact.
        LlvmTypeHandle fnType = LlvmApi.FunctionType(LlvmApi.VoidTypeInContext(state.Target.Context), [state.I64]);
        LlvmValueHandle asm = LlvmApi.GetInlineAsm(fnType,
            "mrs x9, tpidr_el0\n\tcbnz x9, 1f\n\tmsr tpidr_el0, $0\n\t1:",
            "r,~{x9}", true, false);
        LlvmApi.BuildCall2(state.Target.Builder, fnType, asm, [blockAddr], "");
    }

    // Sets TPIDR_EL0 (the aarch64 thread pointer) to the given block address. Used by the main entry
    // (static executable) and by each parallel worker to point its thread-local arena at its own block.
    private static void EmitArm64SetThreadPointer(LlvmCodegenState state, LlvmValueHandle blockAddr)
    {
        LlvmTypeHandle fnType = LlvmApi.FunctionType(LlvmApi.VoidTypeInContext(state.Target.Context), [state.I64]);
        LlvmValueHandle asm = LlvmApi.GetInlineAsm(fnType, "msr tpidr_el0, $0", "r", true, false);
        LlvmApi.BuildCall2(state.Target.Builder, fnType, asm, [blockAddr], "");
    }

    // Repoints a bare runtime state's six arena cursor slots at the arm64 thread-local globals, so
    // reads/writes go through TP+tprel (this thread's TLS block). Used by the parallel worker after it
    // sets TPIDR_EL0 to its own block.
    private static LlvmCodegenState WithArm64ThreadLocalArenaSlots(LlvmCodegenState state) => state with
    {
        HeapCursorSlot = LlvmApi.GetNamedGlobal(state.Target.Module, "__ashes_heap_cursor"),
        HeapEndSlot = LlvmApi.GetNamedGlobal(state.Target.Module, "__ashes_heap_end"),
        ToSpaceCursorSlot = LlvmApi.GetNamedGlobal(state.Target.Module, "__ashes_tospace_cursor"),
        ToSpaceEndSlot = LlvmApi.GetNamedGlobal(state.Target.Module, "__ashes_tospace_end"),
        BlobCursorSlot = LlvmApi.GetNamedGlobal(state.Target.Module, "__ashes_blob_cursor"),
        BlobEndSlot = LlvmApi.GetNamedGlobal(state.Target.Module, "__ashes_blob_end"),
    };

    // win-x64 analog of EmitMainThreadTlsInit: publish the main-thread TCB pointer into TEB+0x28.
    // No arch_prctl — GS already addresses the OS-provided TEB on Windows x64.
    private static LlvmValueHandle EmitMainThreadTlsInitWindows(LlvmCodegenState state)
    {
        LlvmTypeHandle tcbType = LlvmApi.ArrayType2(state.I8, (ulong)MainTcbSizeBytes);
        LlvmValueHandle tcb = LlvmApi.AddGlobal(state.Target.Module, tcbType, "__ashes_main_tcb");
        LlvmApi.SetLinkage(tcb, LlvmLinkage.Internal);
        LlvmApi.SetInitializer(tcb, LlvmApi.ConstNull(tcbType));
        LlvmValueHandle tcbAddr = LlvmApi.BuildPtrToInt(state.Target.Builder, tcb, state.I64, "win_main_tcb_addr");
        StoreMemory(state, tcbAddr, (int)TcbSelfOffset, tcbAddr, "win_tcb_self_ptr");
        EmitWriteTcbBaseToTeb(state, tcbAddr);
        return tcbAddr;
    }

    /// <summary>
    /// Returns <paramref name="state"/> with its arena cursor/end slots repointed at the
    /// current thread's TCB on linux-x64; a no-op on other flavors (which keep module-global
    /// arena slots). Intended for non-entry functions (runtime ABI helpers, lifted closures):
    /// the builder must be positioned in the function's entry block before any allocation.
    /// </summary>
    private static LlvmCodegenState WithLinuxThreadArena(LlvmCodegenState state)
    {
        LlvmValueHandle tcbBase;
        if (state.Flavor == LlvmCodegenFlavor.LinuxX64)
        {
            tcbBase = EmitReadTcbBaseFromGs(state);
        }
        else if (state.Flavor == LlvmCodegenFlavor.WindowsX64)
        {
            tcbBase = EmitReadTcbBaseFromTeb(state);
        }
        else
        {
            return state;
        }

        (LlvmValueHandle cursor, LlvmValueHandle end) = BuildLinuxArenaSlots(state, tcbBase);
        return state with { HeapCursorSlot = cursor, HeapEndSlot = end };
    }

    /// <summary>
    /// Builds this thread's arena cursor and end pointers (ordinary address-space-0 pointers)
    /// from the TCB base, at <see cref="TcbHeapCursorOffset"/> / <see cref="TcbHeapEndOffset"/>.
    /// </summary>
    private static (LlvmValueHandle Cursor, LlvmValueHandle End) BuildLinuxArenaSlots(LlvmCodegenState state, LlvmValueHandle tcbBase)
        => BuildLinuxTcbSlots(state, tcbBase, TcbHeapCursorOffset, TcbHeapEndOffset);

    /// <summary>
    /// Materializes a pair of i64 slot pointers into this thread's TCB at the given byte offsets
    /// (e.g. the main arena cursor/end, or the to-space cursor/end). Used to address per-thread arena
    /// state as ordinary pointers after the TCB base has been recovered.
    /// </summary>
    private static (LlvmValueHandle Cursor, LlvmValueHandle End) BuildLinuxTcbSlots(LlvmCodegenState state, LlvmValueHandle tcbBase, ulong cursorOffset, ulong endOffset)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle cursorAddr = LlvmApi.BuildAdd(builder, tcbBase,
            LlvmApi.ConstInt(state.I64, cursorOffset, 0), "tcb_cursor_addr");
        LlvmValueHandle endAddr = LlvmApi.BuildAdd(builder, tcbBase,
            LlvmApi.ConstInt(state.I64, endOffset, 0), "tcb_end_addr");
        return (
            LlvmApi.BuildIntToPtr(builder, cursorAddr, state.I64Ptr, "tcb_cursor_ptr"),
            LlvmApi.BuildIntToPtr(builder, endAddr, state.I64Ptr, "tcb_end_ptr"));
    }

    // Each OS chunk carries an 8-byte header (at its base) and an 8-byte footer (at its usable end):
    //   header [base + 0]              = the previous chunk's stored end value (0 for the first chunk),
    //                                    forming a linked list walked backwards by EmitReclaimArenaChunks.
    //   footer [base + size - 8]       = the chunk's own base address, so the reclaim walk can recover a
    //                                    chunk's base from its end pointer WITHOUT assuming a fixed size.
    // Usable allocations occupy [base + 8, base + size - 8); the end slot holds base + size - 8. Chunks
    // are normally HeapChunkBytes, but a single allocation larger than that grows the chunk to fit
    // (see EmitHeapGrow) — the footer is what lets variable-sized chunks still be reclaimed.
    private const int ChunkHeaderBytes = 8;
    private const int ChunkFooterBytes = 8;
    private const int ChunkOverheadBytes = ChunkHeaderBytes + ChunkFooterBytes;

    /// <summary>
    /// Initializes a freshly OS-allocated chunk: writes the header (previous chunk's end) and the
    /// footer (self base), then points the given cursor/end slots at the usable region.
    /// </summary>
    private static void EmitHeapChunkSetup(LlvmCodegenState state, LlvmValueHandle chunkBase, LlvmValueHandle chunkSize, LlvmValueHandle prevEnd, LlvmValueHandle cursorSlot, LlvmValueHandle endSlot, string prefix)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        // Header: link to the previous chunk's end (0 for the first chunk).
        StoreMemory(state, chunkBase, 0, prevEnd, prefix + "_prev_end");
        // Usable end = base + size - footer; allocations run from base + header up to (not into) it.
        LlvmValueHandle chunkEnd = LlvmApi.BuildAdd(builder, chunkBase,
            LlvmApi.BuildSub(builder, chunkSize, LlvmApi.ConstInt(state.I64, ChunkFooterBytes, 0), prefix + "_usable_span"),
            prefix + "_end");
        // Footer at the usable end records this chunk's own base, so reclaim can go end -> base.
        StoreMemory(state, chunkEnd, 0, chunkBase, prefix + "_self_base");
        LlvmValueHandle cursorStart = LlvmApi.BuildAdd(builder, chunkBase,
            LlvmApi.ConstInt(state.I64, ChunkHeaderBytes, 0), prefix + "_cursor_start");
        LlvmApi.BuildStore(builder, cursorStart, cursorSlot);
        LlvmApi.BuildStore(builder, chunkEnd, endSlot);
    }

    private static void EmitHeapChunkInit(LlvmCodegenState state)
    {
        LlvmValueHandle chunkBase = EmitAllocateOsMemory(state, LlvmApi.ConstInt(state.I64, HeapChunkBytes, 0), "init_heap");
        EmitHeapChunkInitCheck(state, chunkBase);
        // First chunk has no predecessor (prev end = 0).
        EmitHeapChunkSetup(state, chunkBase, LlvmApi.ConstInt(state.I64, HeapChunkBytes, 0),
            LlvmApi.ConstInt(state.I64, 0, 0), state.HeapCursorSlot, state.HeapEndSlot, "init_heap");
    }

    /// <summary>
    /// Ensures the current heap chunk has enough space for sizeBytes.
    /// If cursor + size would exceed the chunk end, allocates new chunk(s) from the OS
    /// until the request fits in the current chunk.
    /// </summary>
    private static void EmitHeapEnsureSpace(LlvmCodegenState state, LlvmValueHandle sizeBytes)
        => EmitHeapEnsureSpace(state, sizeBytes, state.HeapCursorSlot, state.HeapEndSlot);

    private static void EmitHeapEnsureSpace(LlvmCodegenState state, LlvmValueHandle sizeBytes, LlvmValueHandle cursorSlot, LlvmValueHandle endSlot)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        var checkBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "heap_check");
        var growBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "heap_grow");
        var continueBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "heap_ok");

        LlvmApi.BuildBr(builder, checkBlock);

        LlvmApi.PositionBuilderAtEnd(builder, checkBlock);
        LlvmValueHandle cursor = LlvmApi.BuildLoad2(builder, state.I64, cursorSlot, "heap_check_cursor");
        LlvmValueHandle needed = LlvmApi.BuildAdd(builder, cursor, sizeBytes, "heap_check_needed");
        LlvmValueHandle heapEnd = LlvmApi.BuildLoad2(builder, state.I64, endSlot, "heap_end");
        LlvmValueHandle overflow = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ugt, needed, heapEnd, "heap_overflow");
        LlvmApi.BuildCondBr(builder, overflow, growBlock, continueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, growBlock);
        EmitHeapGrow(state, cursorSlot, endSlot, sizeBytes);
        LlvmApi.BuildBr(builder, checkBlock);

        LlvmApi.PositionBuilderAtEnd(builder, continueBlock);
    }

    private static readonly byte[] HeapAllocFailedMessage =
        "Runtime error: failed to allocate heap memory from OS\n"u8.ToArray();

    /// <summary>
    /// Allocates a new heap chunk from the OS and updates cursor/end globals.
    /// Writes the current chunk's base address into the new chunk's header so that
    /// <see cref="EmitRestoreArenaState"/> can walk back and reclaim abandoned chunks.
    /// </summary>
    private static void EmitHeapGrow(LlvmCodegenState state)
        => EmitHeapGrow(state, state.HeapCursorSlot, state.HeapEndSlot, LlvmApi.ConstInt(state.I64, 0, 0));

    /// <summary>
    /// Allocates a new heap chunk large enough to satisfy an allocation of <paramref name="neededBytes"/>
    /// and links it after the current chunk. The chunk is normally <see cref="HeapChunkBytes"/>, but a
    /// single request larger than a standard chunk (e.g. a multi-megabyte regex substitution buffer)
    /// grows the chunk to <c>neededBytes + overhead</c> so the request fits — without this, the
    /// ensure-space loop would allocate one fixed chunk per iteration forever and exhaust memory.
    /// </summary>
    private static void EmitHeapGrow(LlvmCodegenState state, LlvmValueHandle cursorSlot, LlvmValueHandle endSlot, LlvmValueHandle neededBytes)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        // Header link = the current chunk's end (0 on the to-space's first grow — harmless, to-space is
        // never reclaimed so the chain is never walked).
        LlvmValueHandle prevEnd = LlvmApi.BuildLoad2(builder, state.I64, endSlot, "grow_heap_prev_end");
        // chunkSize = max(HeapChunkBytes, 2*neededBytes + header + footer). The doubling headroom
        // lets a growing accumulator larger than a standard chunk keep extending in place
        // (ConcatStrTip) inside its chunk instead of forcing a new chunk per append — geometric
        // growth, amortized-linear copies. The extra half is virtual address space only: Linux and
        // Windows fault pages in lazily, so untouched headroom costs nothing.
        LlvmValueHandle fitSize = LlvmApi.BuildAdd(builder,
            LlvmApi.BuildMul(builder, neededBytes, LlvmApi.ConstInt(state.I64, 2, 0), "grow_heap_need2"),
            LlvmApi.ConstInt(state.I64, ChunkOverheadBytes, 0), "grow_heap_fit_size");
        LlvmValueHandle standard = LlvmApi.ConstInt(state.I64, HeapChunkBytes, 0);
        LlvmValueHandle fitsStandard = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ule, fitSize, standard, "grow_heap_fits_standard");
        LlvmValueHandle chunkSize = LlvmApi.BuildSelect(builder, fitsStandard, standard, fitSize, "grow_heap_chunk_size");
        LlvmValueHandle chunkBase = EmitAllocateOsMemory(state, chunkSize, "grow_heap");
        EmitHeapChunkInitCheck(state, chunkBase);
        EmitHeapChunkSetup(state, chunkBase, chunkSize, prevEnd, cursorSlot, endSlot, "grow_heap");
    }

    /// <summary>
    /// Saves the current heap cursor and end pointers into local slots.
    /// Used at ownership scope entry for arena-based deallocation.
    /// A <paramref name="coroutineLoop"/> save (an async loop's per-iteration watermark) is a no-op
    /// under the legacy task driver — the matching restore/reclaim are no-ops there too.
    /// </summary>
    private static bool EmitSaveArenaState(LlvmCodegenState state, int cursorLocalSlot, int endLocalSlot, bool coroutineLoop = false)
    {
        if (coroutineLoop && !state.UseRunQueueScheduler)
        {
            return false;
        }

        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle cursor = LlvmApi.BuildLoad2(builder, state.I64, state.HeapCursorSlot, "arena_save_cursor");
        LlvmApi.BuildStore(builder, cursor, state.LocalSlots[cursorLocalSlot]);
        LlvmValueHandle end = LlvmApi.BuildLoad2(builder, state.I64, state.HeapEndSlot, "arena_save_end");
        LlvmApi.BuildStore(builder, end, state.LocalSlots[endLocalSlot]);
        return false;
    }

    /// <summary>
    /// Emits the runtime gate for a coroutine-loop arena reset: branches to a fresh "do it" block
    /// only when the coroutine's task (local slot 0 is the state struct) still has its
    /// <c>LoopResetOk</c> header flag set — the scheduler clears it when a composite ancestor shares
    /// the arena, where resetting to a stale watermark could free a sibling's live allocations.
    /// Returns the merge block; the caller emits the gated work and must branch to the merge block.
    /// </summary>
    private static LlvmBasicBlockHandle EmitLoopResetGate(LlvmCodegenState state, string prefix, out LlvmBasicBlockHandle doBlock)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        doBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_do");
        LlvmBasicBlockHandle mergeBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_merge");
        LlvmValueHandle taskPtr = LlvmApi.BuildLoad2(builder, state.I64, state.LocalSlots[0], prefix + "_task");
        LlvmValueHandle resetOk = LoadMemory(state, taskPtr, TaskStructLayout.LoopResetOk, prefix + "_ok");
        LlvmApi.BuildCondBr(builder,
            LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, resetOk, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_ok_cmp"),
            doBlock, mergeBlock);
        LlvmApi.PositionBuilderAtEnd(builder, doBlock);
        return mergeBlock;
    }

    /// <summary>
    /// Restores the heap cursor and end pointers from local slots previously saved
    /// by <see cref="EmitSaveArenaState"/>. This resets the bump allocator to the
    /// scope-entry watermark, effectively freeing all heap memory allocated since
    /// the matching SaveArenaState.
    ///
    /// <para>
    /// Before resetting, the current heap end is saved to <paramref name="preRestoreEndSlot"/>
    /// so that a subsequent <see cref="EmitReclaimArenaChunks"/> can determine which
    /// OS chunks to free. This instruction does NOT free OS chunks itself — that is
    /// deferred to <see cref="EmitReclaimArenaChunks"/> so that any intervening
    /// <see cref="EmitCopyOutArena"/> can safely read from the not-yet-freed chunks.
    /// </para>
    /// </summary>
    private static bool EmitRestoreArenaState(LlvmCodegenState state, int cursorLocalSlot, int endLocalSlot, int preRestoreEndSlot, bool coroutineLoop = false)
    {
        if (coroutineLoop && !state.UseRunQueueScheduler)
        {
            return false;
        }

        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmBasicBlockHandle mergeBlock = default;
        if (coroutineLoop)
        {
            mergeBlock = EmitLoopResetGate(state, "loop_reset_restore", out _);
        }

        // Save the current heap end before resetting — needed by ReclaimArenaChunks.
        LlvmValueHandle currentEnd = LlvmApi.BuildLoad2(builder, state.I64, state.HeapEndSlot, "arena_pre_restore_end");
        LlvmApi.BuildStore(builder, currentEnd, state.LocalSlots[preRestoreEndSlot]);

        // Reset cursor and end globals to the saved watermark.
        LlvmValueHandle savedCursor = LlvmApi.BuildLoad2(builder, state.I64, state.LocalSlots[cursorLocalSlot], "arena_restore_cursor");
        LlvmValueHandle savedEnd = LlvmApi.BuildLoad2(builder, state.I64, state.LocalSlots[endLocalSlot], "arena_restore_end");
        LlvmApi.BuildStore(builder, savedCursor, state.HeapCursorSlot);
        LlvmApi.BuildStore(builder, savedEnd, state.HeapEndSlot);

        if (coroutineLoop)
        {
            LlvmApi.BuildBr(builder, mergeBlock);
            LlvmApi.PositionBuilderAtEnd(builder, mergeBlock);
        }

        return false;
    }

    /// <summary>
    /// Frees OS chunks that were allocated between the saved watermark and the
    /// pre-restore heap state. Called AFTER <see cref="EmitRestoreArenaState"/> and
    /// any <see cref="EmitCopyOutArena"/> instructions.
    ///
    /// <para>
    /// When the pre-restore end matches the saved end (same chunk — fast path),
    /// no chunks need to be freed. When they differ (slow path), walks the chunk
    /// linked list from the pre-restore chunk back to the saved chunk, calling
    /// <c>munmap</c> (Linux) or <c>VirtualFree</c> (Windows) on each abandoned chunk.
    /// </para>
    /// </summary>
    private static bool EmitReclaimArenaChunks(LlvmCodegenState state, int savedEndSlot, int preRestoreEndSlot, bool coroutineLoop = false)
    {
        if (coroutineLoop && !state.UseRunQueueScheduler)
        {
            return false;
        }

        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmBasicBlockHandle loopMergeBlock = default;
        if (coroutineLoop)
        {
            // Same gate as the restore: if the reset was vetoed, the pre-restore slot holds garbage
            // and nothing above the watermark was abandoned — skip the walk entirely.
            loopMergeBlock = EmitLoopResetGate(state, "loop_reset_reclaim", out _);
        }

        LlvmValueHandle savedEnd = LlvmApi.BuildLoad2(builder, state.I64, state.LocalSlots[savedEndSlot], "reclaim_saved_end");
        LlvmValueHandle preRestoreEnd = LlvmApi.BuildLoad2(builder, state.I64, state.LocalSlots[preRestoreEndSlot], "reclaim_pre_restore_end");

        // Fast path: same chunk — nothing to free.
        LlvmValueHandle sameChunk = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, preRestoreEnd, savedEnd, "reclaim_same_chunk");

        var freeChunksBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "reclaim_free_chunks");
        var reclaimDoneBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "reclaim_done");
        LlvmApi.BuildCondBr(builder, sameChunk, reclaimDoneBlock, freeChunksBlock);

        // Slow path: abandoned chunks exist — walk the linked list and free them.
        LlvmApi.PositionBuilderAtEnd(builder, freeChunksBlock);
        LlvmValueHandle curEndSlot = LlvmApi.BuildAlloca(builder, state.I64, "reclaim_cur_end_slot");
        LlvmApi.BuildStore(builder, preRestoreEnd, curEndSlot);
        var loopBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "reclaim_free_loop");
        LlvmApi.BuildBr(builder, loopBlock);

        LlvmApi.PositionBuilderAtEnd(builder, loopBlock);
        LlvmValueHandle curEnd = LlvmApi.BuildLoad2(builder, state.I64, curEndSlot, "reclaim_loop_cur_end");
        // curEnd points at the chunk's footer, which records the chunk's own base (chunks are
        // variable-sized, so the base cannot be reconstructed from a fixed size). The header at that
        // base links to the previous chunk's end. size = (curEnd + footer) - base.
        LlvmValueHandle curBase = LoadMemory(state, curEnd, 0, "reclaim_loop_cur_base");
        LlvmValueHandle prevEnd = LoadMemory(state, curBase, 0, "reclaim_loop_prev_end");
        LlvmValueHandle curSize = LlvmApi.BuildSub(builder,
            LlvmApi.BuildAdd(builder, curEnd, LlvmApi.ConstInt(state.I64, ChunkFooterBytes, 0), "reclaim_loop_cur_top"),
            curBase, "reclaim_loop_cur_size");
        EmitFreeOsMemory(state, curBase, curSize, "reclaim_free_chunk");
        LlvmValueHandle nextEnd = prevEnd;
        LlvmApi.BuildStore(builder, nextEnd, curEndSlot);
        LlvmValueHandle doneFreeing = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, nextEnd, savedEnd, "reclaim_loop_done");
        LlvmApi.BuildCondBr(builder, doneFreeing, reclaimDoneBlock, loopBlock);

        // Merge point.
        LlvmApi.PositionBuilderAtEnd(builder, reclaimDoneBlock);

        if (coroutineLoop)
        {
            LlvmApi.BuildBr(builder, loopMergeBlock);
            LlvmApi.PositionBuilderAtEnd(builder, loopMergeBlock);
        }

        return false;
    }

    /// <summary>
    /// Copies a heap object to a fresh allocation at the current arena cursor.
    /// Used by the two-pass TCO back-edge copy-out (see the curried self-call path
    /// in <c>Lowering.cs</c>): in the up-pass it runs before <see cref="EmitRestoreArenaState"/>
    /// with the cursor above all sources; in the down-pass it runs after the reset
    /// (cursor at the watermark W) but before <see cref="EmitReclaimArenaChunks"/>,
    /// so OS chunks are not yet freed and the source bytes at <paramref name="srcTemp"/>
    /// remain physically readable. The two-pass caller guarantees the destination
    /// block never overlaps any not-yet-read source, so the forward memcpy is always
    /// safe. (A single round of down-copies is NOT safe with two or more fresh heap
    /// args: compacting the first arg to W can clobber a later arg's source.)
    ///
    /// <para>
    /// For strings (<paramref name="staticSizeBytes"/> == -1): reads the length field
    /// from the source object and allocates <c>8 + length</c> bytes.
    /// For fixed-size objects (<paramref name="staticSizeBytes"/> &gt; 0): allocates
    /// exactly <paramref name="staticSizeBytes"/> bytes (e.g. 16 for a cons cell).
    /// A nil (0) source pointer is passed through unchanged — this handles empty
    /// lists in TCO copy-out where the tail pointer is nil.
    /// </para>
    /// </summary>
    private static LlvmValueHandle EmitCopyOutArena(LlvmCodegenState state, int srcTemp, int staticSizeBytes)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle srcPtr = LoadTemp(state, srcTemp);

        // BigInt: size read from the header's limb count. Source is always a valid pointer (zero is a
        // {header:0} heap object, never null), like strings — no nil guard needed.
        if (staticSizeBytes == IrInst.CopyOutArena.BigIntSize)
        {
            return EmitCopyOutBigIntValue(state, srcPtr);
        }

        // Fixed-size objects (e.g. cons cells) may have a nil source pointer —
        // for example, an empty list tail in a TCO iteration. Guard against
        // copying from address 0 by branching around the alloc+memcpy.
        if (staticSizeBytes > 0)
        {
            LlvmValueHandle isNil = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq,
                srcPtr, LlvmApi.ConstInt(state.I64, 0, 0), "copy_out_nil_check");

            // Alloca to hold the result across both paths — no phi node binding
            // available; mem2reg promotes this to a phi automatically.
            LlvmValueHandle resultSlot = LlvmApi.BuildAlloca(builder, state.I64, "copy_out_result_slot");
            LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), resultSlot);

            var copyBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "copy_out_do");
            var mergeBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "copy_out_merge");
            LlvmApi.BuildCondBr(builder, isNil, mergeBlock, copyBlock);

            // Non-nil path: allocate and copy.
            LlvmApi.PositionBuilderAtEnd(builder, copyBlock);
            LlvmValueHandle sizeBytes = LlvmApi.ConstInt(state.I64, (ulong)staticSizeBytes, 0);
            LlvmValueHandle destPtr = EmitAllocDynamic(state, sizeBytes);
            LlvmValueHandle srcPtrBytes = LlvmApi.BuildIntToPtr(builder, srcPtr, state.I8Ptr, "copy_out_src_ptr");
            LlvmValueHandle destPtrBytes = LlvmApi.BuildIntToPtr(builder, destPtr, state.I8Ptr, "copy_out_dest_ptr");
            EmitCopyBytes(state, destPtrBytes, srcPtrBytes, sizeBytes, "copy_out");
            LlvmApi.BuildStore(builder, destPtr, resultSlot);
            LlvmApi.BuildBr(builder, mergeBlock);

            // Merge: load result (0 for nil path, destPtr for copy path).
            LlvmApi.PositionBuilderAtEnd(builder, mergeBlock);
            return LlvmApi.BuildLoad2(builder, state.I64, resultSlot, "copy_out_result");
        }

        // Dynamic size (strings): source is never nil — Ashes strings are always
        // heap-allocated {length, bytes} structs; the type system has no nullable
        // string representation, so every TStr value is a valid non-zero pointer.
        return EmitCopyOutStringValue(state, srcPtr);
    }

    /// <summary>
    /// Copies a string value ({length:i64, bytes…}) from the arena to a fresh allocation.
    /// Reads the length field to determine total size (8 + length), allocates that many bytes,
    /// and memcpy's the entire string. The source pointer must be non-nil.
    /// </summary>
    private static LlvmValueHandle EmitCopyOutStringValue(LlvmCodegenState state, LlvmValueHandle srcPtr)
        => EmitCopyOutStringValue(state, srcPtr, state.HeapCursorSlot, state.HeapEndSlot);

    private static LlvmValueHandle EmitCopyOutStringValue(LlvmCodegenState state, LlvmValueHandle srcPtr, LlvmValueHandle cursorSlot, LlvmValueHandle endSlot)
    {
        // Materialize: read length and bytes via the accessors (which handle views), then build a
        // fresh OWNED string. This both copies an owned string out of the reclaimable region and
        // collapses a view into a self-contained owned string so it never dangles past a reset.
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle length = LoadStringLength(state, srcPtr, "copy_out_str_len");
        LlvmValueHandle srcBytes = GetStringBytesPointer(state, srcPtr, "copy_out_src");
        LlvmValueHandle dynSize = LlvmApi.BuildAdd(builder, length, LlvmApi.ConstInt(state.I64, 8, 0), "copy_out_str_total");
        LlvmValueHandle dynDest = EmitAllocDynamic(state, dynSize, cursorSlot, endSlot);
        StoreMemory(state, dynDest, 0, length, "copy_out_str_dest_len");
        LlvmValueHandle dynDestBytes = GetStringBytesPointer(state, dynDest, "copy_out_dest");
        EmitCopyBytes(state, dynDestBytes, srcBytes, length, "copy_out");
        return dynDest;
    }

    /// <summary>
    /// Copies a BigInt value out of the arena to a fresh allocation. A BigInt is
    /// <c>{ i64 header = (negFlag&lt;&lt;32)|limbCount, i64 limb… }</c> in sign-magnitude, base 2^64 —
    /// a self-contained buffer with no internal pointers. The total size is <c>8 + limbCount * 8</c>
    /// (the normalized prefix), read from the header, and the whole thing is memcpy'd. The source
    /// pointer is always non-nil (BigInt zero is a header-0 heap object).
    /// </summary>
    private static LlvmValueHandle EmitCopyOutBigIntValue(LlvmCodegenState state, LlvmValueHandle srcPtr)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle headerPtr = LlvmApi.BuildIntToPtr(builder, srcPtr, state.I64Ptr, "copy_out_bigint_hdr_ptr");
        LlvmValueHandle header = LlvmApi.BuildLoad2(builder, state.I64, headerPtr, "copy_out_bigint_hdr");
        LlvmValueHandle limbCount = LlvmApi.BuildAnd(builder, header, LlvmApi.ConstInt(state.I64, 0xFFFFFFFF, 0), "copy_out_bigint_limbs");
        LlvmValueHandle limbBytes = LlvmApi.BuildMul(builder, limbCount, LlvmApi.ConstInt(state.I64, 8, 0), "copy_out_bigint_limb_bytes");
        LlvmValueHandle size = LlvmApi.BuildAdd(builder, limbBytes, LlvmApi.ConstInt(state.I64, 8, 0), "copy_out_bigint_size");
        LlvmValueHandle dest = EmitAllocDynamic(state, size);
        LlvmValueHandle srcBytes = LlvmApi.BuildIntToPtr(builder, srcPtr, state.I8Ptr, "copy_out_bigint_src");
        LlvmValueHandle destBytes = LlvmApi.BuildIntToPtr(builder, dest, state.I8Ptr, "copy_out_bigint_dest");
        EmitCopyBytes(state, destBytes, srcBytes, size, "copy_out_bigint");
        return dest;
    }

    /// <summary>
    /// Like <see cref="EmitCopyOutArena"/> but allocates the copy in the persistent blob region — a bump
    /// arena kept SEPARATE from the to-space NODE arena. Materialized heap leaf fields (Map keys/values)
    /// must persist past the per-iteration arena reset, but must not be interleaved with the fixed-size
    /// reuse nodes in to-space: interleaving variable-size blobs between nodes corrupts the in-place reuse
    /// rebuild (a node child pointer ends up addressing blob/scratch bytes). A nodes-only to-space matches
    /// the proven layout of copy-type (e.g. Int-keyed) maps. Only the dynamic-size (string) path is needed
    /// today (in-place-reuse key/value materialization). See <see cref="IrInst.CopyOutArenaToSpace"/>.
    /// </summary>
    // memcpy SizeBytes from *srcTemp into the existing cell at *destTemp (in-place value-cell reuse).
    private static bool EmitCopyFixedInto(LlvmCodegenState state, int destTemp, int srcTemp, int sizeBytes)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle destBytes = LlvmApi.BuildIntToPtr(builder, LoadTemp(state, destTemp), state.I8Ptr, "copy_into_dest");
        LlvmValueHandle srcBytes = LlvmApi.BuildIntToPtr(builder, LoadTemp(state, srcTemp), state.I8Ptr, "copy_into_src");
        EmitCopyBytes(state, destBytes, srcBytes, LlvmApi.ConstInt(state.I64, (ulong)sizeBytes, 0), "copy_into");
        return false;
    }

    private static LlvmValueHandle EmitCopyOutArenaToSpace(LlvmCodegenState state, int srcTemp, int staticSizeBytes)
    {
        LlvmValueHandle srcPtr = LoadTemp(state, srcTemp);
        if (staticSizeBytes > 0)
        {
            // Fixed-size shallow copy into the blob region (e.g. a tuple of copy-type elements: a flat
            // memcpy fully materializes it — there are no nested heap fields to follow).
            LlvmBuilderHandle builder = state.Target.Builder;
            LlvmValueHandle dest = EmitAlloc(state, staticSizeBytes, state.BlobCursorSlot, state.BlobEndSlot);
            LlvmValueHandle srcBytes = LlvmApi.BuildIntToPtr(builder, srcPtr, state.I8Ptr, "blob_copy_src");
            LlvmValueHandle destBytes = LlvmApi.BuildIntToPtr(builder, dest, state.I8Ptr, "blob_copy_dest");
            EmitCopyBytes(state, destBytes, srcBytes, LlvmApi.ConstInt(state.I64, (ulong)staticSizeBytes, 0), "blob_copy");
            return dest;
        }

        return EmitCopyOutStringValue(state, srcPtr, state.BlobCursorSlot, state.BlobEndSlot);
    }

    /// <summary>
    /// Deep-copies an entire cons-cell chain out of the arena. Uses a three-phase
    /// approach to avoid aliasing between source and destination cells (which share
    /// the same arena region after RestoreArenaState resets the cursor):
    /// <para>
    /// <b>Count:</b> Walk the source list to count cells (N).
    /// </para>
    /// <para>
    /// <b>Cache:</b> Stack-allocate N i64 slots via dynamic alloca, then
    /// walk the source list again, storing each head value into the buffer. After
    /// this phase all source data has been read; the buffer is on the stack, not
    /// in the arena, so it is safe from arena overwrites.
    /// </para>
    /// <para>
    /// <b>Build:</b> Allocate N fresh 16-byte arena cells, populating each
    /// from the cached head values and linking tail pointers. When
    /// <paramref name="headCopy"/> is <see cref="ListHeadCopyKind.String"/>, each
    /// cached head (a string pointer) is also copied to a fresh allocation. When
    /// <paramref name="headCopy"/> is <see cref="ListHeadCopyKind.InnerList"/>, each
    /// cached head (an inner list pointer) is deep-copied recursively with inline heads.
    /// </para>
    /// </summary>
    private static LlvmValueHandle EmitCopyOutList(LlvmCodegenState state, int srcTemp, ListHeadCopyKind headCopy)
        => EmitCopyOutListCore(state, LoadTemp(state, srcTemp), headCopy, "copy_list");

    /// <summary>
    /// Shared three-phase copy-out over a cons-cell chain given as a pointer value: nil guard,
    /// then the count, cache, and build phases, all named under <paramref name="prefix"/>.
    /// </summary>
    private static LlvmValueHandle EmitCopyOutListCore(LlvmCodegenState state, LlvmValueHandle srcPtr, ListHeadCopyKind headCopy, string prefix)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle zero = LlvmApi.ConstInt(state.I64, 0, 0);

        // Guard: if the source list is nil, return nil immediately.
        LlvmValueHandle srcIsNil = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq,
            srcPtr, zero, prefix + "_src_nil");
        LlvmValueHandle overallResultSlot = LlvmApi.BuildAlloca(builder, state.I64, prefix + "_overall_result");
        LlvmApi.BuildStore(builder, zero, overallResultSlot);
        var copyListStart = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_start");
        var copyListFinal = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_final");
        LlvmApi.BuildCondBr(builder, srcIsNil, copyListFinal, copyListStart);

        LlvmApi.PositionBuilderAtEnd(builder, copyListStart);

        LlvmValueHandle totalCells = EmitCopyOutListCoreCount(state, srcPtr, prefix);
        var (headBufAddr, headBufBytes, headBufIsOsSlot, headBuf) = EmitCopyOutListCoreCacheHeads(state, srcPtr, totalCells, prefix);
        LlvmValueHandle firstCell = EmitCopyOutListCoreBuild(state, headBuf, totalCells, headCopy, prefix);

        // Done
        EmitListHeadCacheFree(state, headBufAddr, headBufBytes, headBufIsOsSlot, prefix + "_head_buf");
        LlvmApi.BuildStore(builder, firstCell, overallResultSlot);
        LlvmApi.BuildBr(builder, copyListFinal);

        LlvmApi.PositionBuilderAtEnd(builder, copyListFinal);
        return LlvmApi.BuildLoad2(builder, state.I64, overallResultSlot, prefix + "_result_val");
    }

    /// <summary>Count phase of <see cref="EmitCopyOutListCore"/>: walks the source chain and returns the cell count.</summary>
    private static LlvmValueHandle EmitCopyOutListCoreCount(LlvmCodegenState state, LlvmValueHandle srcPtr, string prefix)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle zero = LlvmApi.ConstInt(state.I64, 0, 0);
        LlvmValueHandle one = LlvmApi.ConstInt(state.I64, 1, 0);

        // ── Count source cells ────────────────────────────────────────
        LlvmValueHandle countSlot = LlvmApi.BuildAlloca(builder, state.I64, prefix + "_count");
        LlvmApi.BuildStore(builder, zero, countSlot);
        LlvmValueHandle countCurSlot = LlvmApi.BuildAlloca(builder, state.I64, prefix + "_count_cur");
        LlvmApi.BuildStore(builder, srcPtr, countCurSlot);

        var countHead = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_count_head");
        var countBody = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_count_body");
        var countDone = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_count_done");
        LlvmApi.BuildBr(builder, countHead);

        LlvmApi.PositionBuilderAtEnd(builder, countHead);
        LlvmValueHandle countCur = LlvmApi.BuildLoad2(builder, state.I64, countCurSlot, prefix + "_count_cur_val");
        LlvmValueHandle countIsNil = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq,
            countCur, zero, prefix + "_count_nil");
        LlvmApi.BuildCondBr(builder, countIsNil, countDone, countBody);

        LlvmApi.PositionBuilderAtEnd(builder, countBody);
        LlvmValueHandle oldCount = LlvmApi.BuildLoad2(builder, state.I64, countSlot, prefix + "_count_old");
        LlvmValueHandle newCount = LlvmApi.BuildAdd(builder, oldCount, one, prefix + "_count_inc");
        LlvmApi.BuildStore(builder, newCount, countSlot);
        LlvmValueHandle countTail = LoadMemory(state, countCur, 8, prefix + "_count_tail");
        LlvmApi.BuildStore(builder, countTail, countCurSlot);
        LlvmApi.BuildBr(builder, countHead);

        LlvmApi.PositionBuilderAtEnd(builder, countDone);
        return LlvmApi.BuildLoad2(builder, state.I64, countSlot, prefix + "_total_cells");
    }

    /// <summary>Cache phase of <see cref="EmitCopyOutListCore"/>: snapshots every head value into a scratch buffer.</summary>
    private static (LlvmValueHandle BufAddr, LlvmValueHandle TotalBytes, LlvmValueHandle IsOsSlot, LlvmValueHandle HeadBuf) EmitCopyOutListCoreCacheHeads(
        LlvmCodegenState state, LlvmValueHandle srcPtr, LlvmValueHandle totalCells, string prefix)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle zero = LlvmApi.ConstInt(state.I64, 0, 0);
        LlvmValueHandle one = LlvmApi.ConstInt(state.I64, 1, 0);

        // ── Cache head values into a scratch buffer (stack when small, OS memory when large) ──
        var (headBufAddr, headBufBytes, headBufIsOsSlot) = EmitListHeadCacheAlloc(state, totalCells, prefix + "_head_buf");
        LlvmValueHandle headBuf = LlvmApi.BuildIntToPtr(builder, headBufAddr, state.I8Ptr, prefix + "_head_buf_ptr");
        LlvmValueHandle cacheCurSlot = LlvmApi.BuildAlloca(builder, state.I64, prefix + "_cache_cur");
        LlvmApi.BuildStore(builder, srcPtr, cacheCurSlot);
        LlvmValueHandle cacheIdxSlot = LlvmApi.BuildAlloca(builder, state.I64, prefix + "_cache_idx");
        LlvmApi.BuildStore(builder, zero, cacheIdxSlot);

        var cacheHead = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_cache_head");
        var cacheBody = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_cache_body");
        var cacheDone = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_cache_done");
        LlvmApi.BuildBr(builder, cacheHead);

        LlvmApi.PositionBuilderAtEnd(builder, cacheHead);
        LlvmValueHandle cacheCur = LlvmApi.BuildLoad2(builder, state.I64, cacheCurSlot, prefix + "_cache_cur_val");
        LlvmValueHandle cacheIsNil = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq,
            cacheCur, zero, prefix + "_cache_nil");
        LlvmApi.BuildCondBr(builder, cacheIsNil, cacheDone, cacheBody);

        LlvmApi.PositionBuilderAtEnd(builder, cacheBody);
        LlvmValueHandle headVal = LoadMemory(state, cacheCur, 0, prefix + "_cache_head_val");
        LlvmValueHandle cacheIdx = LlvmApi.BuildLoad2(builder, state.I64, cacheIdxSlot, prefix + "_cache_idx_val");
        LlvmValueHandle bufSlot = LlvmApi.BuildGEP2(builder, state.I64, headBuf, [cacheIdx], prefix + "_buf_slot");
        LlvmApi.BuildStore(builder, headVal, bufSlot);
        LlvmValueHandle nextCacheIdx = LlvmApi.BuildAdd(builder, cacheIdx, one, prefix + "_cache_idx_inc");
        LlvmApi.BuildStore(builder, nextCacheIdx, cacheIdxSlot);
        LlvmValueHandle cacheTail = LoadMemory(state, cacheCur, 8, prefix + "_cache_tail");
        LlvmApi.BuildStore(builder, cacheTail, cacheCurSlot);
        LlvmApi.BuildBr(builder, cacheHead);

        LlvmApi.PositionBuilderAtEnd(builder, cacheDone);
        return (headBufAddr, headBufBytes, headBufIsOsSlot, headBuf);
    }

    /// <summary>Build phase of <see cref="EmitCopyOutListCore"/>: allocates and links the destination cells from the cached heads, returning the first cell.</summary>
    private static LlvmValueHandle EmitCopyOutListCoreBuild(LlvmCodegenState state, LlvmValueHandle headBuf, LlvmValueHandle totalCells, ListHeadCopyKind headCopy, string prefix)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle zero = LlvmApi.ConstInt(state.I64, 0, 0);
        LlvmValueHandle one = LlvmApi.ConstInt(state.I64, 1, 0);
        LlvmValueHandle cellSize = LlvmApi.ConstInt(state.I64, 16, 0);

        // ── Build destination list from cached head values ──────────────
        // Arena allocations happen here. Source cells are never read again.
        LlvmValueHandle buildIdxSlot = LlvmApi.BuildAlloca(builder, state.I64, prefix + "_build_idx");
        LlvmApi.BuildStore(builder, zero, buildIdxSlot);
        LlvmValueHandle prevCellSlot = LlvmApi.BuildAlloca(builder, state.I64, prefix + "_prev");
        LlvmApi.BuildStore(builder, zero, prevCellSlot);

        // Eagerly allocate the first cell from headBuf[0].
        LlvmValueHandle firstHeadSlot = LlvmApi.BuildGEP2(builder, state.I64, headBuf, [zero], prefix + "_first_head_slot");
        LlvmValueHandle firstHeadVal = LlvmApi.BuildLoad2(builder, state.I64, firstHeadSlot, prefix + "_first_head_val");
        LlvmValueHandle firstHeadCopied = EmitCopyOutListHead(state, firstHeadVal, headCopy);
        LlvmValueHandle firstCell = EmitAllocDynamic(state, cellSize);
        StoreMemory(state, firstCell, 0, firstHeadCopied, prefix + "_store_first_head");
        StoreMemory(state, firstCell, 8, zero, prefix + "_store_first_tail");
        LlvmApi.BuildStore(builder, firstCell, prevCellSlot);
        LlvmApi.BuildStore(builder, one, buildIdxSlot);

        var buildHead = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_build_head");
        var buildBody = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_build_body");
        var buildDone = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_build_done");
        LlvmApi.BuildBr(builder, buildHead);

        LlvmApi.PositionBuilderAtEnd(builder, buildHead);
        LlvmValueHandle buildIdx = LlvmApi.BuildLoad2(builder, state.I64, buildIdxSlot, prefix + "_build_idx_val");
        LlvmValueHandle buildDoneCheck = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Uge,
            buildIdx, totalCells, prefix + "_build_done_check");
        LlvmApi.BuildCondBr(builder, buildDoneCheck, buildDone, buildBody);

        LlvmApi.PositionBuilderAtEnd(builder, buildBody);
        LlvmValueHandle buildHeadSlot = LlvmApi.BuildGEP2(builder, state.I64, headBuf, [buildIdx], prefix + "_build_head_slot");
        LlvmValueHandle buildHeadVal = LlvmApi.BuildLoad2(builder, state.I64, buildHeadSlot, prefix + "_build_head_val");
        LlvmValueHandle buildHeadCopied = EmitCopyOutListHead(state, buildHeadVal, headCopy);
        LlvmValueHandle newCell = EmitAllocDynamic(state, cellSize);
        StoreMemory(state, newCell, 0, buildHeadCopied, prefix + "_store_head");
        StoreMemory(state, newCell, 8, zero, prefix + "_store_tail");

        // Link: prevCell.tail = newCell
        LlvmValueHandle prevCell = LlvmApi.BuildLoad2(builder, state.I64, prevCellSlot, prefix + "_prev_val");
        StoreMemory(state, prevCell, 8, newCell, prefix + "_link_tail");

        // Advance
        LlvmApi.BuildStore(builder, newCell, prevCellSlot);
        LlvmValueHandle nextBuildIdx = LlvmApi.BuildAdd(builder, buildIdx, one, prefix + "_build_idx_inc");
        LlvmApi.BuildStore(builder, nextBuildIdx, buildIdxSlot);
        LlvmApi.BuildBr(builder, buildHead);

        LlvmApi.PositionBuilderAtEnd(builder, buildDone);
        return firstCell;
    }

    /// <summary>
    /// Allocates the head-cache buffer for a list copy-out. Small lists cache on the stack
    /// (dynamic alloca, as before); lists whose cache exceeds 32 KB get an OS allocation instead —
    /// an unbounded dynamic alloca overflows the default 8 MB stack past ~1M cells, and entry-frame
    /// allocas are never popped, so consecutive top-level list copies compound until the crash.
    /// Returns the buffer address (i64), the byte size, and the slot recording whether the buffer
    /// must be released via <see cref="EmitListHeadCacheFree"/>.
    /// </summary>
    private static (LlvmValueHandle BufAddr, LlvmValueHandle TotalBytes, LlvmValueHandle IsOsSlot) EmitListHeadCacheAlloc(
        LlvmCodegenState state, LlvmValueHandle totalCells, string prefix)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle zero = LlvmApi.ConstInt(state.I64, 0, 0);
        LlvmValueHandle one = LlvmApi.ConstInt(state.I64, 1, 0);
        LlvmValueHandle totalBytes = LlvmApi.BuildMul(builder, totalCells,
            LlvmApi.ConstInt(state.I64, 8, 0), prefix + "_bytes");

        LlvmValueHandle bufAddrSlot = LlvmApi.BuildAlloca(builder, state.I64, prefix + "_addr_slot");
        LlvmValueHandle isOsSlot = LlvmApi.BuildAlloca(builder, state.I64, prefix + "_isos_slot");

        LlvmValueHandle isLarge = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ugt,
            totalBytes, LlvmApi.ConstInt(state.I64, 32768, 0), prefix + "_is_large");
        var osBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_os");
        var stackBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_stack");
        var contBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_cont");
        LlvmApi.BuildCondBr(builder, isLarge, osBlock, stackBlock);

        LlvmApi.PositionBuilderAtEnd(builder, stackBlock);
        LlvmValueHandle stackBuf = LlvmApi.BuildArrayAlloca(builder, state.I64, totalCells, prefix + "_stack_buf");
        LlvmApi.BuildStore(builder, LlvmApi.BuildPtrToInt(builder, stackBuf, state.I64, prefix + "_stack_addr"), bufAddrSlot);
        LlvmApi.BuildStore(builder, zero, isOsSlot);
        LlvmApi.BuildBr(builder, contBlock);

        LlvmApi.PositionBuilderAtEnd(builder, osBlock);
        LlvmValueHandle osBuf = EmitAllocateOsMemory(state, totalBytes, prefix);
        LlvmApi.BuildStore(builder, osBuf, bufAddrSlot);
        LlvmApi.BuildStore(builder, one, isOsSlot);
        LlvmApi.BuildBr(builder, contBlock);

        LlvmApi.PositionBuilderAtEnd(builder, contBlock);
        LlvmValueHandle bufAddr = LlvmApi.BuildLoad2(builder, state.I64, bufAddrSlot, prefix + "_addr");
        return (bufAddr, totalBytes, isOsSlot);
    }

    /// <summary>
    /// Releases a head-cache buffer allocated by <see cref="EmitListHeadCacheAlloc"/> when it was
    /// OS-allocated (stack buffers pop with the frame).
    /// </summary>
    private static void EmitListHeadCacheFree(LlvmCodegenState state,
        LlvmValueHandle bufAddr, LlvmValueHandle totalBytes, LlvmValueHandle isOsSlot, string prefix)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle isOs = LlvmApi.BuildLoad2(builder, state.I64, isOsSlot, prefix + "_isos");
        LlvmValueHandle needsFree = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne,
            isOs, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_needs_free");
        var freeBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_free");
        var contBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_free_cont");
        LlvmApi.BuildCondBr(builder, needsFree, freeBlock, contBlock);

        LlvmApi.PositionBuilderAtEnd(builder, freeBlock);
        EmitFreeOsMemory(state, bufAddr, totalBytes, prefix + "_release");
        LlvmApi.BuildBr(builder, contBlock);

        LlvmApi.PositionBuilderAtEnd(builder, contBlock);
    }

    /// <summary>
    /// Copies a list head value according to the specified <paramref name="headCopy"/> kind.
    /// For <see cref="ListHeadCopyKind.Inline"/>, returns the value unchanged.
    /// For <see cref="ListHeadCopyKind.String"/>, copies the string to a fresh allocation.
    /// For <see cref="ListHeadCopyKind.InnerList"/>, deep-copies the inner cons-cell chain
    /// (with inline/copy-type heads) via the same three-phase algorithm.
    /// </summary>
    private static LlvmValueHandle EmitCopyOutListHead(LlvmCodegenState state, LlvmValueHandle headVal, ListHeadCopyKind headCopy)
    {
        switch (headCopy)
        {
            case ListHeadCopyKind.Inline:
                return headVal;

            case ListHeadCopyKind.String:
                return EmitCopyOutStringValue(state, headVal);

            case ListHeadCopyKind.InnerList:
                return EmitCopyOutListFromValue(state, headVal);

            default:
                return headVal;
        }
    }

    /// <summary>
    /// Deep-copies a cons-cell chain starting from the given pointer value (not a temp slot).
    /// Uses the same three-phase count/cache/build algorithm as <see cref="EmitCopyOutList"/>
    /// but with inline (copy-type) head values only. Used for inner list elements in
    /// <see cref="ListHeadCopyKind.InnerList"/> copy-out.
    /// </summary>
    private static LlvmValueHandle EmitCopyOutListFromValue(LlvmCodegenState state, LlvmValueHandle srcPtr)
        => EmitCopyOutListCore(state, srcPtr, ListHeadCopyKind.Inline, "copy_inner");

    /// <summary>
    /// Copies a closure (24 bytes: {code, env, env_size}) and its environment
    /// out of the arena. If the env pointer is non-nil, allocates a fresh env of
    /// env_size bytes and memcpy's it, then allocates a fresh 24-byte closure and
    /// stores the new env pointer.
    /// </summary>
    private static LlvmValueHandle EmitCopyOutClosure(LlvmCodegenState state, int srcTemp)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle srcPtr = LoadTemp(state, srcTemp);

        // Load fields from source closure
        LlvmValueHandle code = LoadMemory(state, srcPtr, 0, "copy_closure_code");
        LlvmValueHandle envPtr = LoadMemory(state, srcPtr, 8, "copy_closure_env");
        LlvmValueHandle envSize = LoadMemory(state, srcPtr, 16, "copy_closure_env_size");
        LlvmValueHandle dropper = LoadMemory(state, srcPtr, 24, "copy_closure_dropper");

        // Alloca for the new env pointer (nil path: keep 0; non-nil path: new alloc)
        LlvmValueHandle newEnvSlot = LlvmApi.BuildAlloca(builder, state.I64, "copy_closure_new_env_slot");
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), newEnvSlot);

        LlvmValueHandle envIsNil = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq,
            envPtr, LlvmApi.ConstInt(state.I64, 0, 0), "copy_closure_env_nil");
        var envCopyBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "copy_closure_env_copy");
        var envMergeBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "copy_closure_env_merge");
        LlvmApi.BuildCondBr(builder, envIsNil, envMergeBlock, envCopyBlock);

        // Non-nil env: allocate and copy
        LlvmApi.PositionBuilderAtEnd(builder, envCopyBlock);
        LlvmValueHandle newEnv = EmitAllocDynamic(state, envSize);
        LlvmValueHandle envSrcBytes = LlvmApi.BuildIntToPtr(builder, envPtr, state.I8Ptr, "copy_closure_env_src");
        LlvmValueHandle envDestBytes = LlvmApi.BuildIntToPtr(builder, newEnv, state.I8Ptr, "copy_closure_env_dest");
        EmitCopyBytes(state, envDestBytes, envSrcBytes, envSize, "copy_closure_env");
        LlvmApi.BuildStore(builder, newEnv, newEnvSlot);
        LlvmApi.BuildBr(builder, envMergeBlock);

        // Merge: allocate new closure struct
        LlvmApi.PositionBuilderAtEnd(builder, envMergeBlock);
        LlvmValueHandle newEnvPtr = LlvmApi.BuildLoad2(builder, state.I64, newEnvSlot, "copy_closure_new_env");
        LlvmValueHandle closureSize = LlvmApi.ConstInt(state.I64, ClosureSizeBytes, 0);
        LlvmValueHandle newClosure = EmitAllocDynamic(state, closureSize);
        StoreMemory(state, newClosure, 0, code, "copy_closure_store_code");
        StoreMemory(state, newClosure, 8, newEnvPtr, "copy_closure_store_env");
        StoreMemory(state, newClosure, 16, envSize, "copy_closure_store_env_size");
        StoreMemory(state, newClosure, 24, dropper, "copy_closure_store_dropper");

        return newClosure;
    }

    /// <summary>
    /// TCO-specific: copies a single cons cell (16 bytes) out of the arena and
    /// copies or deep-copies the head value according to <paramref name="headCopy"/>.
    /// <para>
    /// In TCO loops, only the top cons cell (created in the current iteration) is above
    /// the arena watermark. The tail pointer references pre-watermark memory from previous
    /// iterations and is preserved unchanged.
    /// </para>
    /// <para>
    /// For nil source pointers (empty list), returns 0 immediately.
    /// </para>
    /// </summary>
    private static LlvmValueHandle EmitCopyOutTcoListCell(LlvmCodegenState state, int srcTemp, ListHeadCopyKind headCopy)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle srcPtr = LoadTemp(state, srcTemp);
        LlvmValueHandle zero = LlvmApi.ConstInt(state.I64, 0, 0);
        LlvmValueHandle cellSize = LlvmApi.ConstInt(state.I64, 16, 0);

        // Nil guard: empty list → return 0.
        LlvmValueHandle isNil = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq,
            srcPtr, zero, "tco_cell_nil_check");
        LlvmValueHandle resultSlot = LlvmApi.BuildAlloca(builder, state.I64, "tco_cell_result_slot");
        LlvmApi.BuildStore(builder, zero, resultSlot);
        var copyBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "tco_cell_copy");
        var mergeBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "tco_cell_merge");
        LlvmApi.BuildCondBr(builder, isNil, mergeBlock, copyBlock);

        LlvmApi.PositionBuilderAtEnd(builder, copyBlock);

        // Read head and tail from the source cell.
        LlvmValueHandle oldHead = LoadMemory(state, srcPtr, 0, "tco_cell_old_head");
        LlvmValueHandle oldTail = LoadMemory(state, srcPtr, 8, "tco_cell_old_tail");

        // Copy the head value according to the element type.
        // CopyOutTcoListCell is only emitted for String or InnerList heads;
        // Inline heads use CopyOutArena(16) instead, so Inline here is a bug.
        LlvmValueHandle newHead = headCopy switch
        {
            ListHeadCopyKind.String => EmitCopyOutStringValue(state, oldHead),
            ListHeadCopyKind.InnerList => EmitCopyOutListFromValue(state, oldHead),
            _ => throw new InvalidOperationException(
                $"CopyOutTcoListCell should not be emitted with HeadCopy={headCopy}; " +
                "Inline heads use CopyOutArena(16) instead."),
        };

        // Allocate new 16-byte cons cell and populate.
        LlvmValueHandle newCell = EmitAllocDynamic(state, cellSize);
        StoreMemory(state, newCell, 0, newHead, "tco_cell_store_head");
        StoreMemory(state, newCell, 8, oldTail, "tco_cell_store_tail");
        LlvmApi.BuildStore(builder, newCell, resultSlot);
        LlvmApi.BuildBr(builder, mergeBlock);

        LlvmApi.PositionBuilderAtEnd(builder, mergeBlock);
        return LlvmApi.BuildLoad2(builder, state.I64, resultSlot, "tco_cell_result");
    }

    /// <summary>
    /// Frees an OS memory chunk previously allocated by <see cref="EmitAllocateOsMemory"/>.
    /// Linux: <c>munmap(ptr, size)</c> — syscall 11 (x86-64) / 215 (AArch64).
    /// Windows: <c>VirtualFree(ptr, 0, MEM_RELEASE)</c> — <c>dwSize</c> must be 0
    ///   for <c>MEM_RELEASE</c>.
    /// </summary>
    private static void EmitFreeOsMemory(LlvmCodegenState state, LlvmValueHandle basePtr, long sizeBytes, string prefix)
        => EmitFreeOsMemory(state, basePtr, LlvmApi.ConstInt(state.I64, (ulong)sizeBytes, 0), prefix);

    private static void EmitFreeOsMemory(LlvmCodegenState state, LlvmValueHandle basePtr, LlvmValueHandle sizeBytes, string prefix)
    {
        if (IsLinuxFlavor(state.Flavor))
        {
            EmitLinuxSyscall(state, SyscallMunmap,
                basePtr,
                sizeBytes,
                LlvmApi.ConstInt(state.I64, 0, 0), // unused third arg
                prefix + "_munmap");
        }
        else
        {
            LlvmBuilderHandle builder = state.Target.Builder;
            const uint memRelease = 0x8000; // MEM_RELEASE
            LlvmTypeHandle virtualFreeType = LlvmApi.FunctionType(state.I32, [state.I64, state.I64, state.I32]);
            LlvmValueHandle virtualFreePtr = LlvmApi.BuildLoad2(builder,
                LlvmApi.PointerTypeInContext(state.Target.Context, 0),
                state.WindowsVirtualFreeImport,
                prefix + "_vf_ptr");
            LlvmApi.BuildCall2(builder,
                virtualFreeType,
                virtualFreePtr,
                [
                    basePtr,                                          // lpAddress
                    LlvmApi.ConstInt(state.I64, 0, 0),               // dwSize = 0 (MEM_RELEASE requirement)
                    LlvmApi.ConstInt(state.I32, memRelease, 0)        // dwFreeType = MEM_RELEASE
                ],
                prefix + "_vf_call");
        }
    }

    /// <summary>
    /// Checks if the OS memory allocation succeeded. On Linux raw syscalls report failures as
    /// negative errno values in the range [-4095, -1]; on Windows VirtualAlloc returns NULL (0).
    /// Panics with a diagnostic message if the allocation failed.
    /// </summary>
    private static void EmitHeapChunkInitCheck(LlvmCodegenState state, LlvmValueHandle chunkBase)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle failed;

        if (IsLinuxFlavor(state.Flavor))
        {
            LlvmValueHandle linuxErrnoMin = LlvmApi.ConstInt(state.I64, unchecked((ulong)(-4095L)), 1);
            LlvmValueHandle zero = LlvmApi.ConstInt(state.I64, 0, 1);
            LlvmValueHandle isErrnoMinOrAbove = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Sge, chunkBase, linuxErrnoMin, "mmap_errno_min_or_above");
            LlvmValueHandle isNegative = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Slt, chunkBase, zero, "mmap_negative");
            failed = LlvmApi.BuildAnd(builder, isErrnoMinOrAbove, isNegative, "mmap_failed");
        }
        else
        {
            LlvmValueHandle failValue = LlvmApi.ConstInt(state.I64, 0, 0); // NULL
            failed = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, chunkBase, failValue, "virtualalloc_failed");
        }
        var failBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "heap_alloc_fail");
        var okBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "heap_alloc_ok");
        LlvmApi.BuildCondBr(builder, failed, failBlock, okBlock);

        LlvmApi.PositionBuilderAtEnd(builder, failBlock);
        EmitHeapAllocFailedPanic(state);

        LlvmApi.PositionBuilderAtEnd(builder, okBlock);
    }

    /// <summary>Applies a unary LLVM math intrinsic (e.g. <c>llvm.sqrt.f64</c>) to an f64 value,
    /// declaring the intrinsic in the module on first use. Backs the Ashes.Math Float unary
    /// primitives (sqrt/floor/ceil/round/trunc).</summary>
    private static LlvmValueHandle EmitFloatUnaryIntrinsic(LlvmCodegenState state, LlvmValueHandle value, string intrinsicName)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmTypeHandle fnTy = LlvmApi.FunctionType(state.F64, [state.F64]);
        LlvmValueHandle fn = LlvmApi.GetNamedFunction(state.Target.Module, intrinsicName);
        if (fn.Ptr == 0)
        {
            fn = LlvmApi.AddFunction(state.Target.Module, intrinsicName, fnTy);
        }

        return LlvmApi.BuildCall2(builder, fnTy, fn, [value], $"{intrinsicName.Replace('.', '_')}_call");
    }

    /// <summary>Calls an openlibm transcendental symbol (e.g. <c>sin</c>, <c>pow</c>). All arguments
    /// and the result are f64. The function is declared in the module on first use; its body comes
    /// from the openlibm bitcode linked into the module when the program uses the math runtime.</summary>
    private static LlvmValueHandle EmitCallLibm(LlvmCodegenState state, string symbol, IReadOnlyList<int> args)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        Span<LlvmTypeHandle> paramTypes = stackalloc LlvmTypeHandle[args.Count];
        for (int i = 0; i < args.Count; i++)
        {
            paramTypes[i] = state.F64;
        }

        LlvmTypeHandle fnTy = LlvmApi.FunctionType(state.F64, paramTypes);
        LlvmValueHandle fn = LlvmApi.GetNamedFunction(state.Target.Module, symbol);
        if (fn.Ptr == 0)
        {
            fn = LlvmApi.AddFunction(state.Target.Module, symbol, fnTy);
        }

        var argValues = new LlvmValueHandle[args.Count];
        for (int i = 0; i < args.Count; i++)
        {
            argValues[i] = LoadTempAsFloat(state, args[i]);
        }

        return LlvmApi.BuildCall2(builder, fnTy, fn, argValues, $"libm_{symbol}");
    }

    /// <summary>Saves the current stack pointer (llvm.stacksave) into a local slot at a TCO loop header.</summary>
    private static bool EmitSaveStackPointer(LlvmCodegenState state, int slot)
    {
        LlvmValueHandle saveFn = LlvmApi.GetNamedFunction(state.Target.Module, "llvm.stacksave.p0");
        LlvmTypeHandle saveTy = LlvmApi.FunctionType(state.I8Ptr, []);
        if (saveFn.Ptr == 0)
        {
            saveFn = LlvmApi.AddFunction(state.Target.Module, "llvm.stacksave.p0", saveTy);
        }

        LlvmValueHandle sp = LlvmApi.BuildCall2(state.Target.Builder, saveTy, saveFn, [], "stacksave");
        LlvmValueHandle spInt = LlvmApi.BuildPtrToInt(state.Target.Builder, sp, state.I64, "stacksave_i64");
        LlvmApi.BuildStore(state.Target.Builder, spInt, state.LocalSlots[slot]);
        return false;
    }

    /// <summary>Restores the stack pointer (llvm.stackrestore) from a slot at a TCO back-edge, freeing the
    /// loop body's per-iteration dynamic stack allocations.</summary>
    private static bool EmitRestoreStackPointer(LlvmCodegenState state, int slot)
    {
        LlvmValueHandle restoreFn = LlvmApi.GetNamedFunction(state.Target.Module, "llvm.stackrestore.p0");
        LlvmTypeHandle restoreTy = LlvmApi.FunctionType(LlvmApi.VoidTypeInContext(state.Target.Context), [state.I8Ptr]);
        if (restoreFn.Ptr == 0)
        {
            restoreFn = LlvmApi.AddFunction(state.Target.Module, "llvm.stackrestore.p0", restoreTy);
        }

        LlvmValueHandle spInt = LlvmApi.BuildLoad2(state.Target.Builder, state.I64, state.LocalSlots[slot], "stackrestore_i64");
        LlvmValueHandle sp = LlvmApi.BuildIntToPtr(state.Target.Builder, spInt, state.I8Ptr, "stackrestore_ptr");
        LlvmApi.BuildCall2(state.Target.Builder, restoreTy, restoreFn, [sp], "");
        return false;
    }

    private static void EmitHeapAllocFailedPanic(LlvmCodegenState state)
    {
        LlvmBuilderHandle builder = state.Target.Builder;

        if (IsLinuxFlavor(state.Flavor))
        {
            LlvmValueHandle msgPtr = EmitStackByteArray(state, HeapAllocFailedMessage);
            LlvmValueHandle msgLen = LlvmApi.ConstInt(state.I64, (ulong)HeapAllocFailedMessage.Length, 0);
            EmitLinuxSyscall(state, SyscallWrite,
                LlvmApi.ConstInt(state.I64, 2, 0),
                LlvmApi.BuildPtrToInt(builder, msgPtr, state.I64, "oom_msg_i64"),
                msgLen,
                "sys_write_oom");
            EmitExit(state, LlvmApi.ConstInt(state.I64, 1, 0));
        }
        else
        {
            EmitWindowsExitProcess(state, LlvmApi.ConstInt(state.I32, 1, 0));
        }
    }

    /// <summary>
    /// Allocates memory from the OS.
    /// Linux: mmap(NULL, size, PROT_READ|PROT_WRITE, MAP_PRIVATE|MAP_ANONYMOUS, -1, 0)
    /// Windows: VirtualAlloc(NULL, size, MEM_COMMIT|MEM_RESERVE, PAGE_READWRITE)
    /// Returns the base address as an i64.
    /// </summary>
    private static LlvmValueHandle EmitAllocateOsMemory(LlvmCodegenState state, LlvmValueHandle sizeBytes, string prefix)
    {
        if (IsLinuxFlavor(state.Flavor))
        {
            return EmitLinuxMmap(state, sizeBytes, prefix);
        }

        return EmitWindowsVirtualAlloc(state, sizeBytes, prefix);
    }

    private static LlvmValueHandle EmitLinuxMmap(LlvmCodegenState state, LlvmValueHandle sizeBytes, string prefix)
    {
        // mmap(addr=NULL, length, prot=PROT_READ|PROT_WRITE, flags=MAP_PRIVATE|MAP_ANONYMOUS, fd=-1, offset=0)
        const long protReadWrite = 0x1 | 0x2;       // PROT_READ | PROT_WRITE
        const long mapPrivateAnon = 0x02 | 0x20;     // MAP_PRIVATE | MAP_ANONYMOUS
        return EmitLinuxSyscall6(state, SyscallMmap,
            LlvmApi.ConstInt(state.I64, 0, 0),                          // addr = NULL
            sizeBytes,                                                    // length
            LlvmApi.ConstInt(state.I64, (ulong)protReadWrite, 0),        // prot
            LlvmApi.ConstInt(state.I64, (ulong)mapPrivateAnon, 0),       // flags
            LlvmApi.ConstInt(state.I64, unchecked((ulong)(-1L)), 1),     // fd = -1
            LlvmApi.ConstInt(state.I64, 0, 0),                           // offset = 0
            prefix + "_mmap");
    }

    private static LlvmValueHandle EmitWindowsVirtualAlloc(LlvmCodegenState state, LlvmValueHandle sizeBytes, string prefix)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        // VirtualAlloc(lpAddress=NULL, dwSize, flAllocationType=MEM_COMMIT|MEM_RESERVE, flProtect=PAGE_READWRITE)
        const uint memCommitReserve = 0x1000 | 0x2000; // MEM_COMMIT | MEM_RESERVE
        const uint pageReadWrite = 0x04;                // PAGE_READWRITE

        LlvmTypeHandle virtualAllocType = LlvmApi.FunctionType(state.I64, [state.I64, state.I64, state.I32, state.I32]);
        LlvmValueHandle virtualAllocPtr = LlvmApi.BuildLoad2(builder,
            LlvmApi.PointerTypeInContext(state.Target.Context, 0),
            state.WindowsVirtualAllocImport,
            prefix + "_va_ptr");
        return LlvmApi.BuildCall2(builder,
            virtualAllocType,
            virtualAllocPtr,
            [
                LlvmApi.ConstInt(state.I64, 0, 0),                        // lpAddress = NULL
                NormalizeToI64(state, sizeBytes),                          // dwSize
                LlvmApi.ConstInt(state.I32, memCommitReserve, 0),          // flAllocationType
                LlvmApi.ConstInt(state.I32, pageReadWrite, 0)              // flProtect
            ],
            prefix + "_va_call");
    }

    private static LlvmValueHandle EmitStringToCString(LlvmCodegenState state, LlvmValueHandle stringRef, string prefix)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle len = LoadStringLength(state, stringRef, prefix + "_len");
        LlvmValueHandle cstrRef = EmitAllocDynamic(state, LlvmApi.BuildAdd(builder, len, LlvmApi.ConstInt(state.I64, 1, 0), prefix + "_size"));
        LlvmValueHandle destPtr = LlvmApi.BuildIntToPtr(builder, cstrRef, state.I8Ptr, prefix + "_dest");
        EmitCopyBytes(state, destPtr, GetStringBytesPointer(state, stringRef, prefix + "_src"), len, prefix + "_copy");
        LlvmValueHandle terminatorPtr = LlvmApi.BuildGEP2(builder, state.I8, destPtr, [len], prefix + "_nul_ptr");
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I8, 0, 0), terminatorPtr);
        return destPtr;
    }

    private static LlvmValueHandle EmitUnitValue(LlvmCodegenState state)
    {
        return EmitAllocAdt(state, 0, 0);
    }

    private static LlvmValueHandle EmitResultOk(LlvmCodegenState state, LlvmValueHandle value)
    {
        LlvmValueHandle result = EmitAllocAdt(state, 0, 1);
        StoreMemory(state, result, 8, value, "result_ok_value");
        return result;
    }

    private static LlvmValueHandle EmitResultError(LlvmCodegenState state, LlvmValueHandle errorStringRef)
    {
        LlvmValueHandle result = EmitAllocAdt(state, 1, 1);
        StoreMemory(state, result, 8, errorStringRef, "result_error_value");
        return result;
    }

    /// <summary>
    /// Emit a string literal as a read-only global constant in the .rodata section.
    /// The global has layout { i64 length, [N x i8] data } matching the runtime
    /// string representation, so no heap allocation is needed. Since Ashes strings
    /// are immutable, this is always safe for literals.
    /// </summary>
    private static LlvmValueHandle EmitHeapStringLiteral(LlvmCodegenState state, string value)
    {
        // Compile-time string-literal interning: identical literal values share a single
        // module-level `.rodata` global (built lazily on first use). The per-use ptrtoint is
        // still emitted at the current builder position — it is a constant that folds away.
        LlvmValueHandle global = state.Target.GetOrAddStringLiteralGlobal(value, () =>
        {
            byte[] utf8 = System.Text.Encoding.UTF8.GetBytes(value);

            // Build the constant initializer: { i64 len, [N x i8] data }
            LlvmTypeHandle arrayType = LlvmApi.ArrayType2(state.I8, (ulong)utf8.Length);

            var byteElements = new LlvmValueHandle[utf8.Length];
            for (int i = 0; i < utf8.Length; i++)
            {
                byteElements[i] = LlvmApi.ConstInt(state.I8, utf8[i], 0);
            }

            LlvmValueHandle constLen = LlvmApi.ConstInt(state.I64, (ulong)utf8.Length, 0);
            LlvmValueHandle constData = LlvmApi.ConstArray2(state.I8, byteElements);
            LlvmValueHandle constStruct = LlvmApi.ConstStructInContext(
                state.Target.Context, [constLen, constData]);

            LlvmTypeHandle structType = LlvmApi.StructTypeInContext(
                state.Target.Context, [state.I64, arrayType]);

            int id = state.Target.NextGlobalConstantId();
            LlvmValueHandle created = LlvmApi.AddGlobal(state.Target.Module, structType, $".str_lit_{id}");
            LlvmApi.SetInitializer(created, constStruct);
            LlvmApi.SetLinkage(created, LlvmLinkage.Internal);
            LlvmApi.SetGlobalConstant(created, 1);
            LlvmApi.SetUnnamedAddr(created, 1); // LocalUnnamedAddr — enable merging
            return created;
        });

        return LlvmApi.BuildPtrToInt(state.Target.Builder, global, state.I64, "str_lit_ref");
    }

    private static LlvmValueHandle EmitHeapStringFromBytes(LlvmCodegenState state, IReadOnlyList<byte> bytes, string prefix)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle len = LlvmApi.ConstInt(state.I64, (ulong)bytes.Count, 0);
        LlvmValueHandle stringRef = EmitAllocDynamic(state, LlvmApi.BuildAdd(builder, len, LlvmApi.ConstInt(state.I64, 8, 0), prefix + "_size"));
        StoreMemory(state, stringRef, 0, len, prefix + "_len");

        if (bytes.Count > 0)
        {
            LlvmValueHandle destPtr = GetStringBytesPointer(state, stringRef, prefix + "_bytes");
            LlvmValueHandle srcPtr = CreateGlobalConstantBytes(state, bytes, prefix);
            LlvmApi.BuildMemCpy(builder, destPtr, 1, srcPtr, 1, len);
        }

        return stringRef;
    }

    private static LlvmValueHandle GetStringBytesAddress(LlvmCodegenState state, LlvmValueHandle stringRef, string name)
    {
        return LlvmApi.BuildAdd(state.Target.Builder, stringRef, LlvmApi.ConstInt(state.I64, 8, 0), name);
    }

    private static LlvmValueHandle EmitValidateUtf8(LlvmCodegenState state, LlvmValueHandle bytesPtr, LlvmValueHandle len, string prefix)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle indexSlot = LlvmApi.BuildAlloca(builder, state.I64, prefix + "_index");
        LlvmValueHandle resultSlot = LlvmApi.BuildAlloca(builder, state.I64, prefix + "_result");
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), indexSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), resultSlot);

        var loopBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_loop");
        var asciiBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_ascii");
        var twoBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_two");
        var threeBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_three");
        var e0Block = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_e0");
        var edBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_ed");
        var f0Block = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_f0");
        var fourBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_four");
        var f4Block = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_f4");
        var validBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_valid");
        var invalidBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_invalid");
        var continueBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_continue");

        LlvmApi.BuildBr(builder, loopBlock);

        LlvmValueHandle index = EmitValidateUtf8LoopHead(state, bytesPtr, len, indexSlot, loopBlock, asciiBlock, validBlock, out LlvmValueHandle firstByte64, prefix);
        EmitValidateUtf8ClassifyNonAscii(state, firstByte64, twoBlock, threeBlock, e0Block, edBlock, f0Block, fourBlock, f4Block, invalidBlock, prefix);

        LlvmApi.PositionBuilderAtEnd(builder, asciiBlock);
        LlvmApi.BuildStore(builder, LlvmApi.BuildAdd(builder, index, LlvmApi.ConstInt(state.I64, 1, 0), prefix + "_ascii_next"), indexSlot);
        LlvmApi.BuildBr(builder, loopBlock);

        EmitUtf8SequenceValidation(state, bytesPtr, len, indexSlot, 2, 0x80, 0xBF, prefix + "_two", twoBlock, loopBlock, invalidBlock);
        EmitUtf8SequenceValidation(state, bytesPtr, len, indexSlot, 3, 0x80, 0xBF, prefix + "_three", threeBlock, loopBlock, invalidBlock);
        EmitUtf8SequenceValidation(state, bytesPtr, len, indexSlot, 3, 0xA0, 0xBF, prefix + "_e0", e0Block, loopBlock, invalidBlock);
        EmitUtf8SequenceValidation(state, bytesPtr, len, indexSlot, 3, 0x80, 0x9F, prefix + "_ed", edBlock, loopBlock, invalidBlock);
        EmitUtf8SequenceValidation(state, bytesPtr, len, indexSlot, 4, 0x90, 0xBF, prefix + "_f0", f0Block, loopBlock, invalidBlock);
        EmitUtf8SequenceValidation(state, bytesPtr, len, indexSlot, 4, 0x80, 0xBF, prefix + "_four", fourBlock, loopBlock, invalidBlock);
        EmitUtf8SequenceValidation(state, bytesPtr, len, indexSlot, 4, 0x80, 0x8F, prefix + "_f4", f4Block, loopBlock, invalidBlock);

        LlvmApi.PositionBuilderAtEnd(builder, validBlock);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 1, 0), resultSlot);
        LlvmApi.BuildBr(builder, continueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, invalidBlock);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), resultSlot);
        LlvmApi.BuildBr(builder, continueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, continueBlock);
        return LlvmApi.BuildLoad2(builder, state.I64, resultSlot, prefix + "_result_value");
    }

    /// <summary>
    /// Loop head of <see cref="EmitValidateUtf8"/>: checks for end-of-input, loads the lead byte,
    /// and branches ASCII/non-ASCII. Leaves the builder in the non-ASCII block and returns the
    /// current index (which dominates the ASCII block) plus the zero-extended lead byte.
    /// </summary>
    private static LlvmValueHandle EmitValidateUtf8LoopHead(
        LlvmCodegenState state,
        LlvmValueHandle bytesPtr,
        LlvmValueHandle len,
        LlvmValueHandle indexSlot,
        LlvmBasicBlockHandle loopBlock,
        LlvmBasicBlockHandle asciiBlock,
        LlvmBasicBlockHandle validBlock,
        out LlvmValueHandle firstByte64,
        string prefix)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmApi.PositionBuilderAtEnd(builder, loopBlock);
        LlvmValueHandle index = LlvmApi.BuildLoad2(builder, state.I64, indexSlot, prefix + "_index_value");
        LlvmValueHandle done = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, index, len, prefix + "_done");
        var inspectBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_inspect");
        LlvmApi.BuildCondBr(builder, done, validBlock, inspectBlock);

        LlvmApi.PositionBuilderAtEnd(builder, inspectBlock);
        LlvmValueHandle firstByte = LoadByteAt(state, bytesPtr, index, prefix + "_byte0");
        firstByte64 = LlvmApi.BuildZExt(builder, firstByte, state.I64, prefix + "_byte0_i64");
        LlvmValueHandle isAscii = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ult, firstByte64, LlvmApi.ConstInt(state.I64, 0x80, 0), prefix + "_is_ascii");
        var nonAsciiBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_non_ascii");
        LlvmApi.BuildCondBr(builder, isAscii, asciiBlock, nonAsciiBlock);

        LlvmApi.PositionBuilderAtEnd(builder, nonAsciiBlock);
        return index;
    }

    /// <summary>
    /// Lead-byte classification chain of <see cref="EmitValidateUtf8"/>: dispatches a non-ASCII
    /// lead byte to the matching sequence-validation block (or the invalid block). The builder
    /// must be positioned in the non-ASCII block on entry.
    /// </summary>
    private static void EmitValidateUtf8ClassifyNonAscii(
        LlvmCodegenState state,
        LlvmValueHandle firstByte64,
        LlvmBasicBlockHandle twoBlock,
        LlvmBasicBlockHandle threeBlock,
        LlvmBasicBlockHandle e0Block,
        LlvmBasicBlockHandle edBlock,
        LlvmBasicBlockHandle f0Block,
        LlvmBasicBlockHandle fourBlock,
        LlvmBasicBlockHandle f4Block,
        LlvmBasicBlockHandle invalidBlock,
        string prefix)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle ltC2 = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ult, firstByte64, LlvmApi.ConstInt(state.I64, 0xC2, 0), prefix + "_lt_c2");
        var geC2Block = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_ge_c2");
        LlvmApi.BuildCondBr(builder, ltC2, invalidBlock, geC2Block);

        LlvmApi.PositionBuilderAtEnd(builder, geC2Block);
        LlvmValueHandle leDf = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ule, firstByte64, LlvmApi.ConstInt(state.I64, 0xDF, 0), prefix + "_le_df");
        var gtDfBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_gt_df");
        LlvmApi.BuildCondBr(builder, leDf, twoBlock, gtDfBlock);

        LlvmApi.PositionBuilderAtEnd(builder, gtDfBlock);
        LlvmValueHandle isE0 = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, firstByte64, LlvmApi.ConstInt(state.I64, 0xE0, 0), prefix + "_is_e0");
        var afterE0Block = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_after_e0");
        LlvmApi.BuildCondBr(builder, isE0, e0Block, afterE0Block);

        LlvmApi.PositionBuilderAtEnd(builder, afterE0Block);
        LlvmValueHandle leEc = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ule, firstByte64, LlvmApi.ConstInt(state.I64, 0xEC, 0), prefix + "_le_ec");
        var afterEcBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_after_ec");
        LlvmApi.BuildCondBr(builder, leEc, threeBlock, afterEcBlock);

        LlvmApi.PositionBuilderAtEnd(builder, afterEcBlock);
        LlvmValueHandle isEd = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, firstByte64, LlvmApi.ConstInt(state.I64, 0xED, 0), prefix + "_is_ed");
        var afterEdBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_after_ed");
        LlvmApi.BuildCondBr(builder, isEd, edBlock, afterEdBlock);

        LlvmApi.PositionBuilderAtEnd(builder, afterEdBlock);
        LlvmValueHandle leEf = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ule, firstByte64, LlvmApi.ConstInt(state.I64, 0xEF, 0), prefix + "_le_ef");
        var afterEfBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_after_ef");
        LlvmApi.BuildCondBr(builder, leEf, threeBlock, afterEfBlock);

        LlvmApi.PositionBuilderAtEnd(builder, afterEfBlock);
        LlvmValueHandle isF0 = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, firstByte64, LlvmApi.ConstInt(state.I64, 0xF0, 0), prefix + "_is_f0");
        var afterF0Block = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_after_f0");
        LlvmApi.BuildCondBr(builder, isF0, f0Block, afterF0Block);

        LlvmApi.PositionBuilderAtEnd(builder, afterF0Block);
        LlvmValueHandle leF3 = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ule, firstByte64, LlvmApi.ConstInt(state.I64, 0xF3, 0), prefix + "_le_f3");
        var afterF3Block = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_after_f3");
        LlvmApi.BuildCondBr(builder, leF3, fourBlock, afterF3Block);

        LlvmApi.PositionBuilderAtEnd(builder, afterF3Block);
        LlvmValueHandle isF4 = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, firstByte64, LlvmApi.ConstInt(state.I64, 0xF4, 0), prefix + "_is_f4");
        LlvmApi.BuildCondBr(builder, isF4, f4Block, invalidBlock);
    }

    private static void EmitUtf8SequenceValidation(
        LlvmCodegenState state,
        LlvmValueHandle bytesPtr,
        LlvmValueHandle len,
        LlvmValueHandle indexSlot,
        int sequenceLength,
        int secondByteMin,
        int secondByteMax,
        string prefix,
        LlvmBasicBlockHandle entryBlock,
        LlvmBasicBlockHandle successBlock,
        LlvmBasicBlockHandle invalidBlock)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmApi.PositionBuilderAtEnd(builder, entryBlock);
        LlvmValueHandle index = LlvmApi.BuildLoad2(builder, state.I64, indexSlot, prefix + "_index_value");
        LlvmValueHandle remaining = LlvmApi.BuildSub(builder, len, index, prefix + "_remaining");
        LlvmValueHandle enoughBytes = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Uge, remaining, LlvmApi.ConstInt(state.I64, (ulong)sequenceLength, 0), prefix + "_enough");
        var bodyBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_body");
        LlvmApi.BuildCondBr(builder, enoughBytes, bodyBlock, invalidBlock);

        LlvmApi.PositionBuilderAtEnd(builder, bodyBlock);
        LlvmValueHandle secondByte = LoadByteAt(state, bytesPtr, LlvmApi.BuildAdd(builder, index, LlvmApi.ConstInt(state.I64, 1, 0), prefix + "_second_index"), prefix + "_second_byte");
        LlvmValueHandle secondByte64 = LlvmApi.BuildZExt(builder, secondByte, state.I64, prefix + "_second_i64");
        LlvmValueHandle secondInRange = BuildByteRangeCheck(state, secondByte64, secondByteMin, secondByteMax, prefix + "_second_range");
        LlvmBasicBlockHandle nextBlock = bodyBlock;
        for (int offset = 2; offset < sequenceLength; offset++)
        {
            var checkBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_cont_" + offset);
            LlvmApi.BuildCondBr(builder, secondInRange, checkBlock, invalidBlock);
            LlvmApi.PositionBuilderAtEnd(builder, checkBlock);
            nextBlock = checkBlock;
            LlvmValueHandle extraByte = LoadByteAt(state, bytesPtr, LlvmApi.BuildAdd(builder, index, LlvmApi.ConstInt(state.I64, (ulong)offset, 0), prefix + "_idx_" + offset), prefix + "_byte_" + offset);
            LlvmValueHandle extraByte64 = LlvmApi.BuildZExt(builder, extraByte, state.I64, prefix + "_byte_i64_" + offset);
            LlvmValueHandle extraInRange = BuildByteRangeCheck(state, extraByte64, 0x80, 0xBF, prefix + "_range_" + offset);
            secondInRange = extraInRange;
        }

        LlvmApi.PositionBuilderAtEnd(builder, nextBlock);
        LlvmBasicBlockHandle advanceBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_advance");
        LlvmApi.BuildCondBr(builder, secondInRange, advanceBlock, invalidBlock);
        LlvmApi.PositionBuilderAtEnd(builder, advanceBlock);
        LlvmApi.BuildStore(builder, LlvmApi.BuildAdd(builder, index, LlvmApi.ConstInt(state.I64, (ulong)sequenceLength, 0), prefix + "_next"), indexSlot);
        LlvmApi.BuildBr(builder, successBlock);
    }

    private static LlvmValueHandle BuildByteRangeCheck(LlvmCodegenState state, LlvmValueHandle byteValue, int minInclusive, int maxInclusive, string prefix)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle geMin = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Uge, byteValue, LlvmApi.ConstInt(state.I64, (ulong)minInclusive, 0), prefix + "_ge_min");
        LlvmValueHandle leMax = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ule, byteValue, LlvmApi.ConstInt(state.I64, (ulong)maxInclusive, 0), prefix + "_le_max");
        return LlvmApi.BuildAnd(builder, geMin, leMax, prefix + "_in_range");
    }

    private static LlvmValueHandle LoadByteAt(LlvmCodegenState state, LlvmValueHandle bytesPtr, LlvmValueHandle index, string name)
    {
        LlvmValueHandle bytePtr = LlvmApi.BuildGEP2(state.Target.Builder, state.I8, bytesPtr, [index], name + "_ptr");
        return LlvmApi.BuildLoad2(state.Target.Builder, state.I8, bytePtr, name);
    }

    private static LlvmValueHandle EmitNonNegativeIntToString(LlvmCodegenState state, LlvmValueHandle value, string prefix)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmTypeHandle bufferType = LlvmApi.ArrayType2(state.I8, 32);
        LlvmValueHandle buffer = LlvmApi.BuildAlloca(builder, bufferType, prefix + "_buffer");
        LlvmValueHandle indexSlot = LlvmApi.BuildAlloca(builder, state.I64, prefix + "_index");
        LlvmValueHandle workSlot = LlvmApi.BuildAlloca(builder, state.I64, prefix + "_work");
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), indexSlot);
        LlvmApi.BuildStore(builder, value, workSlot);

        var zeroBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_zero");
        var loopCheckBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_loop_check");
        var loopBodyBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_loop_body");
        var finishBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_finish");
        LlvmValueHandle isZero = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, value, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_is_zero");
        LlvmApi.BuildCondBr(builder, isZero, zeroBlock, loopCheckBlock);

        LlvmApi.PositionBuilderAtEnd(builder, zeroBlock);
        StoreBufferByte(state, buffer, LlvmApi.ConstInt(state.I64, 31, 0), (byte)'0');
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 1, 0), indexSlot);
        LlvmApi.BuildBr(builder, finishBlock);

        LlvmApi.PositionBuilderAtEnd(builder, loopCheckBlock);
        LlvmValueHandle work = LlvmApi.BuildLoad2(builder, state.I64, workSlot, prefix + "_work_value");
        LlvmValueHandle done = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, work, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_done");
        LlvmApi.BuildCondBr(builder, done, finishBlock, loopBodyBlock);

        LlvmApi.PositionBuilderAtEnd(builder, loopBodyBlock);
        LlvmValueHandle digit = LlvmApi.BuildURem(builder, work, LlvmApi.ConstInt(state.I64, 10, 0), prefix + "_digit");
        LlvmApi.BuildStore(builder, LlvmApi.BuildUDiv(builder, work, LlvmApi.ConstInt(state.I64, 10, 0), prefix + "_next_work"), workSlot);
        LlvmValueHandle idx = LlvmApi.BuildLoad2(builder, state.I64, indexSlot, prefix + "_idx_value");
        StoreBufferByte(state, buffer, LlvmApi.BuildSub(builder, LlvmApi.ConstInt(state.I64, 31, 0), idx, prefix + "_write_idx"), LlvmApi.BuildAdd(builder, digit, LlvmApi.ConstInt(state.I64, (byte)'0', 0), prefix + "_ascii"));
        LlvmApi.BuildStore(builder, LlvmApi.BuildAdd(builder, idx, LlvmApi.ConstInt(state.I64, 1, 0), prefix + "_idx_next"), indexSlot);
        LlvmApi.BuildBr(builder, loopCheckBlock);

        LlvmApi.PositionBuilderAtEnd(builder, finishBlock);
        LlvmValueHandle count = LlvmApi.BuildLoad2(builder, state.I64, indexSlot, prefix + "_count");
        LlvmValueHandle startIndex = LlvmApi.BuildSub(builder, LlvmApi.ConstInt(state.I64, 32, 0), count, prefix + "_start_index");
        LlvmValueHandle startPtr = GetArrayElementPointer(state, bufferType, buffer, startIndex, prefix + "_start_ptr");
        return EmitHeapStringSliceFromBytesPointer(state, startPtr, count, prefix + "_string");
    }

    private static LlvmValueHandle EmitSignedIntToString(LlvmCodegenState state, LlvmValueHandle value, string prefix)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmTypeHandle bufferType = LlvmApi.ArrayType2(state.I8, 32);
        LlvmValueHandle buffer = LlvmApi.BuildAlloca(builder, bufferType, prefix + "_buffer");
        LlvmValueHandle indexSlot = LlvmApi.BuildAlloca(builder, state.I64, prefix + "_index");
        LlvmValueHandle workSlot = LlvmApi.BuildAlloca(builder, state.I64, prefix + "_work");
        LlvmValueHandle negativeSlot = LlvmApi.BuildAlloca(builder, state.I64, prefix + "_negative");
        LlvmValueHandle zero = LlvmApi.ConstInt(state.I64, 0, 0);
        LlvmValueHandle isNegative = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Slt, value, zero, prefix + "_is_negative");
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), indexSlot);
        LlvmApi.BuildStore(builder, LlvmApi.BuildZExt(builder, isNegative, state.I64, prefix + "_negative_i64"), negativeSlot);
        LlvmApi.BuildStore(builder, LlvmApi.BuildSelect(builder, isNegative, LlvmApi.BuildSub(builder, zero, value, prefix + "_magnitude_neg"), value, prefix + "_magnitude"), workSlot);

        var zeroBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_zero");
        var loopCheckBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_loop_check");
        var loopBodyBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_loop_body");
        var maybeSignBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_maybe_sign");
        var signBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_sign");
        var finishBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_finish");

        LlvmValueHandle isZero = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, value, zero, prefix + "_is_zero");
        LlvmApi.BuildCondBr(builder, isZero, zeroBlock, loopCheckBlock);

        LlvmApi.PositionBuilderAtEnd(builder, zeroBlock);
        StoreBufferByte(state, buffer, LlvmApi.ConstInt(state.I64, 31, 0), (byte)'0');
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 1, 0), indexSlot);
        LlvmApi.BuildBr(builder, finishBlock);

        EmitSignedIntToStringDigits(state, buffer, workSlot, indexSlot, loopCheckBlock, loopBodyBlock, maybeSignBlock, prefix);
        EmitSignedIntToStringSign(state, buffer, indexSlot, negativeSlot, maybeSignBlock, signBlock, finishBlock, prefix);

        LlvmApi.PositionBuilderAtEnd(builder, finishBlock);
        LlvmValueHandle count = LlvmApi.BuildLoad2(builder, state.I64, indexSlot, prefix + "_count");
        LlvmValueHandle startIndex = LlvmApi.BuildSub(builder, LlvmApi.ConstInt(state.I64, 32, 0), count, prefix + "_start_index");
        LlvmValueHandle startPtr = GetArrayElementPointer(state, bufferType, buffer, startIndex, prefix + "_start_ptr");
        return EmitHeapStringSliceFromBytesPointer(state, startPtr, count, prefix + "_string");
    }

    /// <summary>Digit loop of <see cref="EmitSignedIntToString"/>: writes the magnitude's decimal digits back-to-front.</summary>
    private static void EmitSignedIntToStringDigits(LlvmCodegenState state, LlvmValueHandle buffer, LlvmValueHandle workSlot, LlvmValueHandle indexSlot, LlvmBasicBlockHandle loopCheckBlock, LlvmBasicBlockHandle loopBodyBlock, LlvmBasicBlockHandle maybeSignBlock, string prefix)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle zero = LlvmApi.ConstInt(state.I64, 0, 0);

        LlvmApi.PositionBuilderAtEnd(builder, loopCheckBlock);
        LlvmValueHandle work = LlvmApi.BuildLoad2(builder, state.I64, workSlot, prefix + "_work_value");
        LlvmValueHandle done = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, work, zero, prefix + "_done");
        LlvmApi.BuildCondBr(builder, done, maybeSignBlock, loopBodyBlock);

        LlvmApi.PositionBuilderAtEnd(builder, loopBodyBlock);
        LlvmValueHandle digit = LlvmApi.BuildURem(builder, work, LlvmApi.ConstInt(state.I64, 10, 0), prefix + "_digit");
        LlvmApi.BuildStore(builder, LlvmApi.BuildUDiv(builder, work, LlvmApi.ConstInt(state.I64, 10, 0), prefix + "_next_work"), workSlot);
        LlvmValueHandle idx = LlvmApi.BuildLoad2(builder, state.I64, indexSlot, prefix + "_idx_value");
        StoreBufferByte(state, buffer, LlvmApi.BuildSub(builder, LlvmApi.ConstInt(state.I64, 31, 0), idx, prefix + "_write_idx"), LlvmApi.BuildAdd(builder, digit, LlvmApi.ConstInt(state.I64, (byte)'0', 0), prefix + "_ascii"));
        LlvmApi.BuildStore(builder, LlvmApi.BuildAdd(builder, idx, LlvmApi.ConstInt(state.I64, 1, 0), prefix + "_idx_next"), indexSlot);
        LlvmApi.BuildBr(builder, loopCheckBlock);
    }

    /// <summary>Sign section of <see cref="EmitSignedIntToString"/>: prepends '-' when the value was negative.</summary>
    private static void EmitSignedIntToStringSign(LlvmCodegenState state, LlvmValueHandle buffer, LlvmValueHandle indexSlot, LlvmValueHandle negativeSlot, LlvmBasicBlockHandle maybeSignBlock, LlvmBasicBlockHandle signBlock, LlvmBasicBlockHandle finishBlock, string prefix)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle zero = LlvmApi.ConstInt(state.I64, 0, 0);

        LlvmApi.PositionBuilderAtEnd(builder, maybeSignBlock);
        LlvmValueHandle negative = LlvmApi.BuildLoad2(builder, state.I64, negativeSlot, prefix + "_negative_value");
        LlvmValueHandle hasSign = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, negative, zero, prefix + "_has_sign");
        LlvmApi.BuildCondBr(builder, hasSign, signBlock, finishBlock);

        LlvmApi.PositionBuilderAtEnd(builder, signBlock);
        LlvmValueHandle idxBeforeSign = LlvmApi.BuildLoad2(builder, state.I64, indexSlot, prefix + "_idx_before_sign");
        StoreBufferByte(state, buffer, LlvmApi.BuildSub(builder, LlvmApi.ConstInt(state.I64, 31, 0), idxBeforeSign, prefix + "_sign_index"), (byte)'-');
        LlvmApi.BuildStore(builder, LlvmApi.BuildAdd(builder, idxBeforeSign, LlvmApi.ConstInt(state.I64, 1, 0), prefix + "_idx_with_sign"), indexSlot);
        LlvmApi.BuildBr(builder, finishBlock);
    }

    private static LlvmValueHandle EmitIntToHexString(LlvmCodegenState state, LlvmValueHandle value, string prefix)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmTypeHandle bufferType = LlvmApi.ArrayType2(state.I8, 32);
        LlvmValueHandle buffer = LlvmApi.BuildAlloca(builder, bufferType, prefix + "_buffer");
        LlvmValueHandle indexSlot = LlvmApi.BuildAlloca(builder, state.I64, prefix + "_index");
        LlvmValueHandle workSlot = LlvmApi.BuildAlloca(builder, state.I64, prefix + "_work");
        LlvmValueHandle negativeSlot = LlvmApi.BuildAlloca(builder, state.I64, prefix + "_negative");
        LlvmValueHandle zero = LlvmApi.ConstInt(state.I64, 0, 0);
        LlvmValueHandle isNegative = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Slt, value, zero, prefix + "_is_negative");
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), indexSlot);
        LlvmApi.BuildStore(builder, LlvmApi.BuildZExt(builder, isNegative, state.I64, prefix + "_negative_i64"), negativeSlot);
        LlvmApi.BuildStore(builder, LlvmApi.BuildSelect(builder, isNegative, LlvmApi.BuildSub(builder, zero, value, prefix + "_magnitude_neg"), value, prefix + "_magnitude"), workSlot);

        var zeroBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_zero");
        var loopCheckBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_loop_check");
        var loopBodyBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_loop_body");
        var prefixBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_prefix");
        var maybeSignBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_maybe_sign");
        var signBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_sign");
        var finishBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_finish");

        LlvmValueHandle isZero = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, value, zero, prefix + "_is_zero");
        LlvmApi.BuildCondBr(builder, isZero, zeroBlock, loopCheckBlock);

        LlvmApi.PositionBuilderAtEnd(builder, zeroBlock);
        StoreBufferByte(state, buffer, LlvmApi.ConstInt(state.I64, 31, 0), (byte)'0');
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 1, 0), indexSlot);
        LlvmApi.BuildBr(builder, prefixBlock);

        EmitIntToHexStringDigits(state, buffer, workSlot, indexSlot, loopCheckBlock, loopBodyBlock, prefixBlock, prefix);
        EmitIntToHexStringPrefixAndSign(state, buffer, indexSlot, negativeSlot, prefixBlock, maybeSignBlock, signBlock, finishBlock, prefix);

        LlvmApi.PositionBuilderAtEnd(builder, finishBlock);
        LlvmValueHandle count = LlvmApi.BuildLoad2(builder, state.I64, indexSlot, prefix + "_count");
        LlvmValueHandle startIndex = LlvmApi.BuildSub(builder, LlvmApi.ConstInt(state.I64, 32, 0), count, prefix + "_start_index");
        LlvmValueHandle startPtr = GetArrayElementPointer(state, bufferType, buffer, startIndex, prefix + "_start_ptr");
        return EmitHeapStringSliceFromBytesPointer(state, startPtr, count, prefix + "_string");
    }

    /// <summary>Digit loop of <see cref="EmitIntToHexString"/>: writes the magnitude's hex digits back-to-front.</summary>
    private static void EmitIntToHexStringDigits(LlvmCodegenState state, LlvmValueHandle buffer, LlvmValueHandle workSlot, LlvmValueHandle indexSlot, LlvmBasicBlockHandle loopCheckBlock, LlvmBasicBlockHandle loopBodyBlock, LlvmBasicBlockHandle prefixBlock, string prefix)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle zero = LlvmApi.ConstInt(state.I64, 0, 0);

        LlvmApi.PositionBuilderAtEnd(builder, loopCheckBlock);
        LlvmValueHandle work = LlvmApi.BuildLoad2(builder, state.I64, workSlot, prefix + "_work_value");
        LlvmValueHandle done = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, work, zero, prefix + "_done");
        LlvmApi.BuildCondBr(builder, done, prefixBlock, loopBodyBlock);

        LlvmApi.PositionBuilderAtEnd(builder, loopBodyBlock);
        LlvmValueHandle nibble = LlvmApi.BuildAnd(builder, work, LlvmApi.ConstInt(state.I64, 0xFUL, 0), prefix + "_nibble");
        LlvmValueHandle isDecimal = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ult, nibble, LlvmApi.ConstInt(state.I64, 10, 0), prefix + "_is_decimal");
        LlvmValueHandle digitAscii = LlvmApi.BuildSelect(
            builder,
            isDecimal,
            LlvmApi.BuildAdd(builder, nibble, LlvmApi.ConstInt(state.I64, (byte)'0', 0), prefix + "_decimal_ascii"),
            LlvmApi.BuildAdd(builder, LlvmApi.BuildSub(builder, nibble, LlvmApi.ConstInt(state.I64, 10, 0), prefix + "_hex_alpha_index"), LlvmApi.ConstInt(state.I64, (byte)'a', 0), prefix + "_hex_ascii"),
            prefix + "_ascii");
        LlvmValueHandle idx = LlvmApi.BuildLoad2(builder, state.I64, indexSlot, prefix + "_idx_value");
        StoreBufferByte(state, buffer, LlvmApi.BuildSub(builder, LlvmApi.ConstInt(state.I64, 31, 0), idx, prefix + "_write_idx"), digitAscii);
        LlvmApi.BuildStore(builder, LlvmApi.BuildLShr(builder, work, LlvmApi.ConstInt(state.I64, 4, 0), prefix + "_next_work"), workSlot);
        LlvmApi.BuildStore(builder, LlvmApi.BuildAdd(builder, idx, LlvmApi.ConstInt(state.I64, 1, 0), prefix + "_idx_next"), indexSlot);
        LlvmApi.BuildBr(builder, loopCheckBlock);
    }

    /// <summary>Prefix and sign sections of <see cref="EmitIntToHexString"/>: prepends "0x" (reversed) and '-' when negative.</summary>
    private static void EmitIntToHexStringPrefixAndSign(LlvmCodegenState state, LlvmValueHandle buffer, LlvmValueHandle indexSlot, LlvmValueHandle negativeSlot, LlvmBasicBlockHandle prefixBlock, LlvmBasicBlockHandle maybeSignBlock, LlvmBasicBlockHandle signBlock, LlvmBasicBlockHandle finishBlock, string prefix)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle zero = LlvmApi.ConstInt(state.I64, 0, 0);

        LlvmApi.PositionBuilderAtEnd(builder, prefixBlock);
        LlvmValueHandle idxBeforePrefix = LlvmApi.BuildLoad2(builder, state.I64, indexSlot, prefix + "_idx_before_prefix");
        StoreBufferByte(state, buffer, LlvmApi.BuildSub(builder, LlvmApi.ConstInt(state.I64, 31, 0), idxBeforePrefix, prefix + "_x_index"), (byte)'x');
        LlvmValueHandle idxWithX = LlvmApi.BuildAdd(builder, idxBeforePrefix, LlvmApi.ConstInt(state.I64, 1, 0), prefix + "_idx_with_x");
        StoreBufferByte(state, buffer, LlvmApi.BuildSub(builder, LlvmApi.ConstInt(state.I64, 31, 0), idxWithX, prefix + "_zero_index"), (byte)'0');
        LlvmApi.BuildStore(builder, LlvmApi.BuildAdd(builder, idxWithX, LlvmApi.ConstInt(state.I64, 1, 0), prefix + "_idx_with_prefix"), indexSlot);
        LlvmApi.BuildBr(builder, maybeSignBlock);

        LlvmApi.PositionBuilderAtEnd(builder, maybeSignBlock);
        LlvmValueHandle negative = LlvmApi.BuildLoad2(builder, state.I64, negativeSlot, prefix + "_negative_value");
        LlvmValueHandle hasSign = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, negative, zero, prefix + "_has_sign");
        LlvmApi.BuildCondBr(builder, hasSign, signBlock, finishBlock);

        LlvmApi.PositionBuilderAtEnd(builder, signBlock);
        LlvmValueHandle idxBeforeSign = LlvmApi.BuildLoad2(builder, state.I64, indexSlot, prefix + "_idx_before_sign");
        StoreBufferByte(state, buffer, LlvmApi.BuildSub(builder, LlvmApi.ConstInt(state.I64, 31, 0), idxBeforeSign, prefix + "_sign_index"), (byte)'-');
        LlvmApi.BuildStore(builder, LlvmApi.BuildAdd(builder, idxBeforeSign, LlvmApi.ConstInt(state.I64, 1, 0), prefix + "_idx_with_sign"), indexSlot);
        LlvmApi.BuildBr(builder, finishBlock);
    }

    private static LlvmValueHandle EmitFloatToString(LlvmCodegenState state, LlvmValueHandle value, string prefix)
        => EmitFloatToDecimalString(state, value, LlvmApi.ConstInt(state.I64, 6, 0), trimTrailingZeros: true, prefix);

    private static LlvmValueHandle EmitFloatToFixedString(LlvmCodegenState state, LlvmValueHandle value, LlvmValueHandle decimals, string prefix)
    {
        // Fixed-precision formatting keeps trailing zeros. decimals is clamped to [0, 18] so the
        // 10^decimals fraction scale stays representable in signed i64.
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle zero = LlvmApi.ConstInt(state.I64, 0, 0);
        LlvmValueHandle maxDecimals = LlvmApi.ConstInt(state.I64, 18, 0);
        LlvmValueHandle belowZero = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Slt, decimals, zero, prefix + "_below_zero");
        LlvmValueHandle clampedLow = LlvmApi.BuildSelect(builder, belowZero, zero, decimals, prefix + "_clamped_low");
        LlvmValueHandle aboveMax = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Sgt, clampedLow, maxDecimals, prefix + "_above_max");
        LlvmValueHandle clamped = LlvmApi.BuildSelect(builder, aboveMax, maxDecimals, clampedLow, prefix + "_clamped");
        return EmitFloatToDecimalString(state, value, clamped, trimTrailingZeros: false, prefix);
    }

    private static LlvmValueHandle EmitFloatToDecimalString(LlvmCodegenState state, LlvmValueHandle value, LlvmValueHandle decimals, bool trimTrailingZeros, string prefix)
    {
        // Caller must ensure 0 <= decimals <= 18.
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle zeroFloat = LlvmApi.ConstReal(state.F64, 0.0);
        LlvmValueHandle isNegative = LlvmApi.BuildFCmp(builder, LlvmRealPredicate.Olt, value, zeroFloat, prefix + "_is_negative");
        LlvmValueHandle absValue = LlvmApi.BuildSelect(builder, isNegative, LlvmApi.BuildFSub(builder, zeroFloat, value, prefix + "_abs_neg"), value, prefix + "_abs");
        // 2^63 is not representable in signed i64; using the next representable f64 below 2^63
        // keeps FPToSI-to-i64 conversions in defined range for all values on the fixed-format path.
        LlvmValueHandle safeIntegerLimit = LlvmApi.ConstReal(state.F64, 9223372036854773760.0);
        LlvmValueHandle needsScientific = LlvmApi.BuildFCmp(builder, LlvmRealPredicate.Ogt, absValue, safeIntegerLimit, prefix + "_needs_scientific");
        LlvmValueHandle resultSlot = LlvmApi.BuildAlloca(builder, state.I64, prefix + "_result");
        LlvmValueHandle normalizedSlot = LlvmApi.BuildAlloca(builder, state.F64, prefix + "_normalized");
        LlvmValueHandle exponentSlot = LlvmApi.BuildAlloca(builder, state.I64, prefix + "_exponent");
        LlvmValueHandle signText = LlvmApi.BuildSelect(builder, isNegative, EmitHeapStringLiteral(state, "-"), EmitHeapStringLiteral(state, ""), prefix + "_sign_text");

        var fixedBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_fixed");
        var scientificInitBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_scientific_init");
        var scientificLoopCheckBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_scientific_loop_check");
        var scientificLoopBodyBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_scientific_loop_body");
        var scientificFinishBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_scientific_finish");
        var mergeBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_merge");

        LlvmApi.BuildCondBr(builder, needsScientific, scientificInitBlock, fixedBlock);

        LlvmApi.PositionBuilderAtEnd(builder, fixedBlock);
        LlvmValueHandle fixedUnsigned = EmitUnsignedFloatToDecimalString(state, absValue, decimals, trimTrailingZeros, prefix + "_fixed_unsigned");
        LlvmApi.BuildStore(builder, EmitStringConcat(state, signText, fixedUnsigned), resultSlot);
        LlvmApi.BuildBr(builder, mergeBlock);

        EmitFloatToDecimalStringScientific(state, absValue, decimals, trimTrailingZeros, signText, resultSlot, normalizedSlot, exponentSlot,
            scientificInitBlock, scientificLoopCheckBlock, scientificLoopBodyBlock, scientificFinishBlock, mergeBlock, prefix);

        LlvmApi.PositionBuilderAtEnd(builder, mergeBlock);
        return LlvmApi.BuildLoad2(builder, state.I64, resultSlot, prefix + "_result_value");
    }

    /// <summary>
    /// Scientific path of <see cref="EmitFloatToDecimalString"/>: normalizes the magnitude into
    /// [0, 10) by repeated division, renders mantissa and exponent, and stores "m e+x" (with sign)
    /// into the result slot.
    /// </summary>
    private static void EmitFloatToDecimalStringScientific(
        LlvmCodegenState state,
        LlvmValueHandle absValue,
        LlvmValueHandle decimals,
        bool trimTrailingZeros,
        LlvmValueHandle signText,
        LlvmValueHandle resultSlot,
        LlvmValueHandle normalizedSlot,
        LlvmValueHandle exponentSlot,
        LlvmBasicBlockHandle scientificInitBlock,
        LlvmBasicBlockHandle scientificLoopCheckBlock,
        LlvmBasicBlockHandle scientificLoopBodyBlock,
        LlvmBasicBlockHandle scientificFinishBlock,
        LlvmBasicBlockHandle mergeBlock,
        string prefix)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmApi.PositionBuilderAtEnd(builder, scientificInitBlock);
        LlvmApi.BuildStore(builder, absValue, normalizedSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), exponentSlot);
        LlvmApi.BuildBr(builder, scientificLoopCheckBlock);

        LlvmApi.PositionBuilderAtEnd(builder, scientificLoopCheckBlock);
        LlvmValueHandle normalizedValue = LlvmApi.BuildLoad2(builder, state.F64, normalizedSlot, prefix + "_normalized_value");
        LlvmValueHandle shouldScale = LlvmApi.BuildFCmp(builder, LlvmRealPredicate.Oge, normalizedValue, LlvmApi.ConstReal(state.F64, 10.0), prefix + "_should_scale");
        LlvmApi.BuildCondBr(builder, shouldScale, scientificLoopBodyBlock, scientificFinishBlock);

        LlvmApi.PositionBuilderAtEnd(builder, scientificLoopBodyBlock);
        LlvmApi.BuildStore(builder, LlvmApi.BuildFDiv(builder, normalizedValue, LlvmApi.ConstReal(state.F64, 10.0), prefix + "_normalized_next"), normalizedSlot);
        LlvmValueHandle exponent = LlvmApi.BuildLoad2(builder, state.I64, exponentSlot, prefix + "_exponent_value");
        LlvmApi.BuildStore(builder, LlvmApi.BuildAdd(builder, exponent, LlvmApi.ConstInt(state.I64, 1, 0), prefix + "_exponent_next"), exponentSlot);
        LlvmApi.BuildBr(builder, scientificLoopCheckBlock);

        LlvmApi.PositionBuilderAtEnd(builder, scientificFinishBlock);
        LlvmValueHandle normalizedFinal = LlvmApi.BuildLoad2(builder, state.F64, normalizedSlot, prefix + "_normalized_final");
        LlvmValueHandle exponentFinal = LlvmApi.BuildLoad2(builder, state.I64, exponentSlot, prefix + "_exponent_final");
        LlvmValueHandle mantissaText = EmitUnsignedFloatToDecimalString(state, normalizedFinal, decimals, trimTrailingZeros, prefix + "_scientific_mantissa");
        LlvmValueHandle exponentText = EmitNonNegativeIntToString(state, exponentFinal, prefix + "_scientific_exponent");
        LlvmValueHandle scientificResult = EmitStringConcat(state, mantissaText, EmitHeapStringLiteral(state, "e+"));
        scientificResult = EmitStringConcat(state, scientificResult, exponentText);
        LlvmApi.BuildStore(builder, EmitStringConcat(state, signText, scientificResult), resultSlot);
        LlvmApi.BuildBr(builder, mergeBlock);
    }

    private static LlvmValueHandle EmitUnsignedFloatToDecimalString(LlvmCodegenState state, LlvmValueHandle absValue, LlvmValueHandle decimals, bool trimTrailingZeros, string prefix)
    {
        // Caller must ensure absValue <= safeIntegerLimit from EmitFloatToDecimalString and
        // 0 <= decimals <= 18 so that 10^decimals fits in signed i64.
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle integerPart = LlvmApi.BuildFPToSI(builder, absValue, state.I64, prefix + "_integer");
        LlvmValueHandle integerAsFloat = LlvmApi.BuildSIToFP(builder, integerPart, state.F64, prefix + "_integer_f64");
        LlvmValueHandle fractional = LlvmApi.BuildFSub(builder, absValue, integerAsFloat, prefix + "_fractional");

        LlvmValueHandle scaleSlot = LlvmApi.BuildAlloca(builder, state.I64, prefix + "_scale");
        LlvmValueHandle scaleCounterSlot = LlvmApi.BuildAlloca(builder, state.I64, prefix + "_scale_counter");
        LlvmValueHandle resultSlot = LlvmApi.BuildAlloca(builder, state.I64, prefix + "_result");
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 1, 0), scaleSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), scaleCounterSlot);

        var scaleCheckBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_scale_check");
        var scaleBodyBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_scale_body");
        var scaleDoneBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_scale_done");
        var fractionBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_fraction");
        var joinBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_join");

        LlvmApi.BuildBr(builder, scaleCheckBlock);

        EmitUnsignedFloatToDecimalStringScaleLoop(state, decimals, scaleSlot, scaleCounterSlot, scaleCheckBlock, scaleBodyBlock, scaleDoneBlock, prefix);
        EmitUnsignedFloatToDecimalStringRender(state, integerPart, fractional, decimals, trimTrailingZeros, scaleSlot, resultSlot, scaleDoneBlock, fractionBlock, joinBlock, prefix);

        LlvmApi.PositionBuilderAtEnd(builder, joinBlock);
        return LlvmApi.BuildLoad2(builder, state.I64, resultSlot, prefix + "_result_value");
    }

    /// <summary>Scale loop of <see cref="EmitUnsignedFloatToDecimalString"/>: computes 10^decimals into the scale slot.</summary>
    private static void EmitUnsignedFloatToDecimalStringScaleLoop(LlvmCodegenState state, LlvmValueHandle decimals, LlvmValueHandle scaleSlot, LlvmValueHandle scaleCounterSlot, LlvmBasicBlockHandle scaleCheckBlock, LlvmBasicBlockHandle scaleBodyBlock, LlvmBasicBlockHandle scaleDoneBlock, string prefix)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmApi.PositionBuilderAtEnd(builder, scaleCheckBlock);
        LlvmValueHandle scaleCounter = LlvmApi.BuildLoad2(builder, state.I64, scaleCounterSlot, prefix + "_scale_counter_value");
        LlvmValueHandle scaleContinue = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Slt, scaleCounter, decimals, prefix + "_scale_continue");
        LlvmApi.BuildCondBr(builder, scaleContinue, scaleBodyBlock, scaleDoneBlock);

        LlvmApi.PositionBuilderAtEnd(builder, scaleBodyBlock);
        LlvmValueHandle scaleValue = LlvmApi.BuildLoad2(builder, state.I64, scaleSlot, prefix + "_scale_value");
        LlvmApi.BuildStore(builder, LlvmApi.BuildMul(builder, scaleValue, LlvmApi.ConstInt(state.I64, 10, 0), prefix + "_scale_next"), scaleSlot);
        LlvmApi.BuildStore(builder, LlvmApi.BuildAdd(builder, scaleCounter, LlvmApi.ConstInt(state.I64, 1, 0), prefix + "_scale_counter_next"), scaleCounterSlot);
        LlvmApi.BuildBr(builder, scaleCheckBlock);
    }

    /// <summary>
    /// Render section of <see cref="EmitUnsignedFloatToDecimalString"/>: rounds the scaled
    /// fraction (carrying into the integer part), renders the integer text, and appends the
    /// fractional digits when decimals is positive.
    /// </summary>
    private static void EmitUnsignedFloatToDecimalStringRender(
        LlvmCodegenState state,
        LlvmValueHandle integerPart,
        LlvmValueHandle fractional,
        LlvmValueHandle decimals,
        bool trimTrailingZeros,
        LlvmValueHandle scaleSlot,
        LlvmValueHandle resultSlot,
        LlvmBasicBlockHandle scaleDoneBlock,
        LlvmBasicBlockHandle fractionBlock,
        LlvmBasicBlockHandle joinBlock,
        string prefix)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmApi.PositionBuilderAtEnd(builder, scaleDoneBlock);
        LlvmValueHandle scale = LlvmApi.BuildLoad2(builder, state.I64, scaleSlot, prefix + "_scale_final");
        LlvmValueHandle scaleAsFloat = LlvmApi.BuildSIToFP(builder, scale, state.F64, prefix + "_scale_f64");
        LlvmValueHandle scaledFractionalFloat = LlvmApi.BuildFMul(builder, fractional, scaleAsFloat, prefix + "_scaled_fractional_f64");
        LlvmValueHandle scaledFractionalRounded = LlvmApi.BuildFAdd(builder, scaledFractionalFloat, LlvmApi.ConstReal(state.F64, 0.5), prefix + "_scaled_fractional_rounded");
        LlvmValueHandle scaledFractionalRaw = LlvmApi.BuildFPToSI(builder, scaledFractionalRounded, state.I64, prefix + "_scaled_fractional_raw");
        LlvmValueHandle hasCarry = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Sge, scaledFractionalRaw, scale, prefix + "_has_carry");
        LlvmValueHandle scaledFractional = LlvmApi.BuildSelect(builder, hasCarry, LlvmApi.ConstInt(state.I64, 0, 0), scaledFractionalRaw, prefix + "_scaled_fractional");
        LlvmValueHandle integerFinal = LlvmApi.BuildSelect(builder, hasCarry, LlvmApi.BuildAdd(builder, integerPart, LlvmApi.ConstInt(state.I64, 1, 0), prefix + "_integer_carry"), integerPart, prefix + "_integer_final");
        LlvmValueHandle integerText = EmitSignedIntToString(state, integerFinal, prefix + "_integer_text");
        LlvmApi.BuildStore(builder, integerText, resultSlot);
        LlvmValueHandle hasFraction = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Sgt, decimals, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_has_fraction");
        LlvmApi.BuildCondBr(builder, hasFraction, fractionBlock, joinBlock);

        LlvmApi.PositionBuilderAtEnd(builder, fractionBlock);
        LlvmValueHandle withDot = EmitStringConcat(state, integerText, EmitHeapStringLiteral(state, "."));
        LlvmValueHandle fractionText = EmitFractionalDigitsToString(state, scaledFractional, decimals, trimTrailingZeros, prefix + "_fraction_text");
        LlvmApi.BuildStore(builder, EmitStringConcat(state, withDot, fractionText), resultSlot);
        LlvmApi.BuildBr(builder, joinBlock);
    }

    private static LlvmValueHandle EmitFractionalDigitsToString(LlvmCodegenState state, LlvmValueHandle scaledValue, LlvmValueHandle digits, bool trimTrailingZeros, string prefix)
    {
        // Caller must ensure 1 <= digits <= 18 (the 32-byte buffer holds at most 18 digits).
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmTypeHandle bufferType = LlvmApi.ArrayType2(state.I8, 32);
        LlvmValueHandle buffer = LlvmApi.BuildAlloca(builder, bufferType, prefix + "_buffer");
        LlvmValueHandle workSlot = LlvmApi.BuildAlloca(builder, state.I64, prefix + "_work");
        LlvmValueHandle widthSlot = LlvmApi.BuildAlloca(builder, state.I64, prefix + "_width");
        LlvmValueHandle indexSlot = LlvmApi.BuildAlloca(builder, state.I64, prefix + "_index");
        LlvmApi.BuildStore(builder, scaledValue, workSlot);
        LlvmApi.BuildStore(builder, digits, widthSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), indexSlot);

        var emitCheckBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_emit_check");
        var emitBodyBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_emit_body");
        var finishBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_finish");

        EmitFractionalDigitsToStringTrim(state, workSlot, widthSlot, emitCheckBlock, trimTrailingZeros, prefix);

        LlvmApi.PositionBuilderAtEnd(builder, emitCheckBlock);
        LlvmValueHandle index = LlvmApi.BuildLoad2(builder, state.I64, indexSlot, prefix + "_emit_index");
        LlvmValueHandle emitWidth = LlvmApi.BuildLoad2(builder, state.I64, widthSlot, prefix + "_emit_width");
        LlvmValueHandle done = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, index, emitWidth, prefix + "_emit_done");
        LlvmApi.BuildCondBr(builder, done, finishBlock, emitBodyBlock);

        LlvmApi.PositionBuilderAtEnd(builder, emitBodyBlock);
        LlvmValueHandle emitWork = LlvmApi.BuildLoad2(builder, state.I64, workSlot, prefix + "_emit_work");
        LlvmValueHandle digit = LlvmApi.BuildURem(builder, emitWork, LlvmApi.ConstInt(state.I64, 10, 0), prefix + "_digit");
        StoreBufferByte(state, buffer, LlvmApi.BuildSub(builder, LlvmApi.ConstInt(state.I64, 31, 0), index, prefix + "_write_index"), LlvmApi.BuildAdd(builder, digit, LlvmApi.ConstInt(state.I64, (byte)'0', 0), prefix + "_ascii"));
        LlvmApi.BuildStore(builder, LlvmApi.BuildUDiv(builder, emitWork, LlvmApi.ConstInt(state.I64, 10, 0), prefix + "_next_work"), workSlot);
        LlvmApi.BuildStore(builder, LlvmApi.BuildAdd(builder, index, LlvmApi.ConstInt(state.I64, 1, 0), prefix + "_next_index"), indexSlot);
        LlvmApi.BuildBr(builder, emitCheckBlock);

        LlvmApi.PositionBuilderAtEnd(builder, finishBlock);
        LlvmValueHandle count = LlvmApi.BuildLoad2(builder, state.I64, indexSlot, prefix + "_count");
        LlvmValueHandle startIndex = LlvmApi.BuildSub(builder, LlvmApi.ConstInt(state.I64, 32, 0), count, prefix + "_start_index");
        LlvmValueHandle startPtr = GetArrayElementPointer(state, bufferType, buffer, startIndex, prefix + "_start_ptr");
        return EmitHeapStringSliceFromBytesPointer(state, startPtr, count, prefix + "_string");
    }

    /// <summary>
    /// Trim section of <see cref="EmitFractionalDigitsToString"/>: when trimming, strips trailing
    /// zero digits (keeping at least one) before the emit loop; otherwise falls straight through.
    /// </summary>
    private static void EmitFractionalDigitsToStringTrim(LlvmCodegenState state, LlvmValueHandle workSlot, LlvmValueHandle widthSlot, LlvmBasicBlockHandle emitCheckBlock, bool trimTrailingZeros, string prefix)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        if (trimTrailingZeros)
        {
            var trimCheckBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_trim_check");
            var trimBodyBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_trim_body");
            LlvmApi.BuildBr(builder, trimCheckBlock);

            LlvmApi.PositionBuilderAtEnd(builder, trimCheckBlock);
            LlvmValueHandle work = LlvmApi.BuildLoad2(builder, state.I64, workSlot, prefix + "_trim_work");
            LlvmValueHandle width = LlvmApi.BuildLoad2(builder, state.I64, widthSlot, prefix + "_trim_width");
            LlvmValueHandle canTrim = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ugt, width, LlvmApi.ConstInt(state.I64, 1, 0), prefix + "_can_trim");
            LlvmValueHandle trailingZero = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, LlvmApi.BuildURem(builder, work, LlvmApi.ConstInt(state.I64, 10, 0), prefix + "_trim_rem"), LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_trailing_zero");
            LlvmApi.BuildCondBr(builder, LlvmApi.BuildAnd(builder, canTrim, trailingZero, prefix + "_should_trim"), trimBodyBlock, emitCheckBlock);

            LlvmApi.PositionBuilderAtEnd(builder, trimBodyBlock);
            LlvmApi.BuildStore(builder, LlvmApi.BuildUDiv(builder, work, LlvmApi.ConstInt(state.I64, 10, 0), prefix + "_trimmed_work"), workSlot);
            LlvmApi.BuildStore(builder, LlvmApi.BuildSub(builder, width, LlvmApi.ConstInt(state.I64, 1, 0), prefix + "_trimmed_width"), widthSlot);
            LlvmApi.BuildBr(builder, trimCheckBlock);
        }
        else
        {
            LlvmApi.BuildBr(builder, emitCheckBlock);
        }
    }

    private static LlvmValueHandle EmitHeapStringSliceFromBytesPointer(LlvmCodegenState state, LlvmValueHandle bytesPtr, LlvmValueHandle len, string prefix)
    {
        // Copy the backing bytes into a fresh OWNED string. Used where the bytes are a transient
        // buffer (e.g. Ashes.Text.fromInt), so a view would dangle. uncons/substring instead build
        // views (EmitStringView) directly, since their backing is a live string.
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle normalizedLen = NormalizeToI64(state, len);
        LlvmValueHandle stringRef = EmitAllocDynamic(state, LlvmApi.BuildAdd(builder, normalizedLen, LlvmApi.ConstInt(state.I64, 8, 0), prefix + "_size"));
        StoreMemory(state, stringRef, 0, normalizedLen, prefix + "_len");
        LlvmValueHandle destBytes = GetStringBytesPointer(state, stringRef, prefix + "_dest");
        EmitCopyBytes(state, destBytes, bytesPtr, normalizedLen, prefix + "_copy");
        return stringRef;
    }

    /// <summary>
    /// Affine-accumulator string append (<see cref="IrInst.ConcatStrTip"/>): semantically a string
    /// concat, but the accumulator grows inside a RESERVATION instead of being copied per append.
    /// The loop keeps two slots — the reservation's start and end. When the left operand IS the
    /// reserved string (pointer identity with the recorded start) and the appended bytes fit below
    /// the recorded end, right's bytes are copied onto its end and only the length header grows —
    /// the arena cursor is untouched, so per-iteration scratch allocated above the reservation
    /// (uncons views, tuples, closure results) is irrelevant. Otherwise the fallback concatenates
    /// into a NEW allocation with doubling headroom (capacity = 2x the result) and records it in
    /// the slots: fallbacks are geometric in the accumulator's growth, so total copy work stays
    /// linear in appended bytes. The identity check makes the mutation safe — only a string this
    /// loop itself reserved can match the recorded start (a caller-passed seed never does), and
    /// lowering only arms the instruction for accumulators the affine analysis proved unaliased.
    /// </summary>
    private static LlvmValueHandle EmitConcatStrTip(LlvmCodegenState state, LlvmValueHandle leftRef, LlvmValueHandle rightRef, int resvStartSlot, int resvEndSlot)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        var extendBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "csr_extend");
        var fallbackBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "csr_fallback");
        var doneBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "csr_done");
        LlvmValueHandle resultSlot = LlvmApi.BuildAlloca(builder, state.I64, "csr_result");

        LlvmValueHandle la = LoadStringLength(state, leftRef, "csr_la");
        LlvmValueHandle lb = LoadStringLength(state, rightRef, "csr_lb");
        LlvmValueHandle resvStart = LlvmApi.BuildLoad2(builder, state.I64, state.LocalSlots[resvStartSlot], "csr_rstart");
        LlvmValueHandle resvEnd = LlvmApi.BuildLoad2(builder, state.I64, state.LocalSlots[resvEndSlot], "csr_rend");
        LlvmValueHandle accEnd = LlvmApi.BuildAdd(builder,
            LlvmApi.BuildAdd(builder, leftRef, LlvmApi.ConstInt(state.I64, 8, 0), "csr_l8"), la, "csr_acc_end");

        LlvmValueHandle isOurs = LlvmApi.BuildAnd(builder,
            LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, leftRef, resvStart, "csr_identity"),
            LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, resvStart, LlvmApi.ConstInt(state.I64, 0, 0), "csr_nonzero"),
            "csr_ours");
        LlvmValueHandle fits = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ule,
            LlvmApi.BuildAdd(builder, accEnd, lb, "csr_new_end"), resvEnd, "csr_fits");
        LlvmApi.BuildCondBr(builder, LlvmApi.BuildAnd(builder, isOurs, fits, "csr_extendable"), extendBlock, fallbackBlock);

        LlvmApi.PositionBuilderAtEnd(builder, extendBlock);
        EmitConcatStrTipExtend(state, leftRef, rightRef, la, lb, accEnd, resultSlot, doneBlock);

        LlvmApi.PositionBuilderAtEnd(builder, fallbackBlock);
        EmitConcatStrTipFallback(state, leftRef, rightRef, la, lb, resvStartSlot, resvEndSlot, resultSlot, doneBlock);

        LlvmApi.PositionBuilderAtEnd(builder, doneBlock);
        return LlvmApi.BuildLoad2(builder, state.I64, resultSlot, "csr_final");
    }

    /// <summary>Extend path of <see cref="EmitConcatStrTip"/>: appends in place onto the reserved accumulator.</summary>
    private static void EmitConcatStrTipExtend(LlvmCodegenState state, LlvmValueHandle leftRef, LlvmValueHandle rightRef, LlvmValueHandle la, LlvmValueHandle lb, LlvmValueHandle accEnd, LlvmValueHandle resultSlot, LlvmBasicBlockHandle doneBlock)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        // Destination [accEnd, accEnd+lb) is unused reservation space — disjoint from every
        // live byte (the affine analysis keeps the accumulator out of the right operand, so
        // right cannot be a view into it), hence a plain memcpy.
        LlvmValueHandle rightBytes = GetStringBytesPointer(state, rightRef, "csr_ext_rbytes");
        LlvmValueHandle destPtr = LlvmApi.BuildIntToPtr(builder, accEnd, state.I8Ptr, "csr_ext_dest");
        EmitCopyBytes(state, destPtr, rightBytes, lb, "csr_ext_copy");
        StoreMemory(state, leftRef, 0, LlvmApi.BuildAdd(builder, la, lb, "csr_ext_len"), "csr_ext_hdr");
        LlvmApi.BuildStore(builder, leftRef, resultSlot);
        LlvmApi.BuildBr(builder, doneBlock);
    }

    /// <summary>Fallback path of <see cref="EmitConcatStrTip"/>: concatenates into a fresh doubling-headroom allocation and records the new reservation.</summary>
    private static void EmitConcatStrTipFallback(LlvmCodegenState state, LlvmValueHandle leftRef, LlvmValueHandle rightRef, LlvmValueHandle la, LlvmValueHandle lb, int resvStartSlot, int resvEndSlot, LlvmValueHandle resultSlot, LlvmBasicBlockHandle doneBlock)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        // Concatenate into a fresh allocation with doubling headroom and record the new
        // reservation. Handles the caller seed (never matches the identity check), views, a
        // filled reservation, and post-compaction re-reservation alike.
        LlvmValueHandle total = LlvmApi.BuildAdd(builder, la, lb, "csr_fb_total");
        LlvmValueHandle allocSize = LlvmApi.BuildAdd(builder,
            LlvmApi.BuildMul(builder, total, LlvmApi.ConstInt(state.I64, 2, 0), "csr_fb_2x"),
            LlvmApi.ConstInt(state.I64, 8, 0), "csr_fb_size");
        LlvmValueHandle dest = EmitAllocDynamic(state, allocSize);
        StoreMemory(state, dest, 0, total, "csr_fb_hdr");
        LlvmValueHandle destBytes = LlvmApi.BuildIntToPtr(builder,
            LlvmApi.BuildAdd(builder, dest, LlvmApi.ConstInt(state.I64, 8, 0), "csr_fb_d8"), state.I8Ptr, "csr_fb_dbytes");
        LlvmValueHandle leftBytes = GetStringBytesPointer(state, leftRef, "csr_fb_lbytes");
        EmitCopyBytes(state, destBytes, leftBytes, la, "csr_fb_lcopy");
        LlvmValueHandle destTail = LlvmApi.BuildIntToPtr(builder,
            LlvmApi.BuildAdd(builder,
                LlvmApi.BuildAdd(builder, dest, LlvmApi.ConstInt(state.I64, 8, 0), "csr_fb_d8b"), la, "csr_fb_dtail_i"),
            state.I8Ptr, "csr_fb_dtail");
        LlvmValueHandle rightBytesF = GetStringBytesPointer(state, rightRef, "csr_fb_rbytes");
        EmitCopyBytes(state, destTail, rightBytesF, lb, "csr_fb_rcopy");
        // Reservation bounds: EmitAllocDynamic reserved align8(allocSize) bytes at dest.
        LlvmValueHandle reservedEnd = LlvmApi.BuildAdd(builder, dest,
            AlignRuntimeSize(state, allocSize, "csr_fb_rsz"), "csr_fb_rend");
        LlvmApi.BuildStore(builder, dest, state.LocalSlots[resvStartSlot]);
        LlvmApi.BuildStore(builder, reservedEnd, state.LocalSlots[resvEndSlot]);
        LlvmApi.BuildStore(builder, dest, resultSlot);
        LlvmApi.BuildBr(builder, doneBlock);
    }

    /// <summary>
    /// ASCII-only case map: copies the source string and flips bit 0x20 on every ASCII letter of
    /// the source case (a-z for <paramref name="upper"/>, A-Z otherwise). Every byte of a multibyte
    /// UTF-8 sequence is >= 0x80 and never matches the letter range, so non-ASCII text passes
    /// through byte-identical — the transform is UTF-8 safe without decoding.
    /// </summary>
    private static LlvmValueHandle EmitAsciiCaseString(LlvmCodegenState state, LlvmValueHandle sourceRef, bool upper, string prefix)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle len = LoadStringLength(state, sourceRef, prefix + "_len");
        LlvmValueHandle srcBytes = GetStringBytesPointer(state, sourceRef, prefix + "_src");
        LlvmValueHandle result = EmitHeapStringSliceFromBytesPointer(state, srcBytes, len, prefix);
        LlvmValueHandle destBytes = GetStringBytesPointer(state, result, prefix + "_dest");

        LlvmValueHandle idxSlot = LlvmApi.BuildAlloca(builder, state.I64, prefix + "_idx_slot");
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), idxSlot);
        var checkBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_check");
        var bodyBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_body");
        var doneBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_done");
        LlvmApi.BuildBr(builder, checkBlock);

        LlvmApi.PositionBuilderAtEnd(builder, checkBlock);
        LlvmValueHandle idx = LlvmApi.BuildLoad2(builder, state.I64, idxSlot, prefix + "_idx");
        LlvmValueHandle more = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ult, idx, len, prefix + "_more");
        LlvmApi.BuildCondBr(builder, more, bodyBlock, doneBlock);

        LlvmApi.PositionBuilderAtEnd(builder, bodyBlock);
        LlvmValueHandle bytePtr = LlvmApi.BuildGEP2(builder, state.I8, destBytes, [idx], prefix + "_byte_ptr");
        LlvmValueHandle byteVal = LlvmApi.BuildLoad2(builder, state.I8, bytePtr, prefix + "_byte");
        ulong lowBound = upper ? (byte)'a' : (byte)'A';
        ulong highBound = upper ? (byte)'z' : (byte)'Z';
        LlvmValueHandle geLow = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Uge, byteVal, LlvmApi.ConstInt(state.I8, lowBound, 0), prefix + "_ge_low");
        LlvmValueHandle leHigh = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ule, byteVal, LlvmApi.ConstInt(state.I8, highBound, 0), prefix + "_le_high");
        LlvmValueHandle isLetter = LlvmApi.BuildAnd(builder, geLow, leHigh, prefix + "_is_letter");
        LlvmValueHandle flipped = LlvmApi.BuildXor(builder, byteVal, LlvmApi.ConstInt(state.I8, 0x20, 0), prefix + "_flipped");
        LlvmValueHandle mapped = LlvmApi.BuildSelect(builder, isLetter, flipped, byteVal, prefix + "_mapped");
        LlvmApi.BuildStore(builder, mapped, bytePtr);
        LlvmApi.BuildStore(builder, LlvmApi.BuildAdd(builder, idx, LlvmApi.ConstInt(state.I64, 1, 0), prefix + "_idx_next"), idxSlot);
        LlvmApi.BuildBr(builder, checkBlock);

        LlvmApi.PositionBuilderAtEnd(builder, doneBlock);
        return result;
    }

    private static void EmitConditionalWrite(LlvmCodegenState state, LlvmValueHandle condition, string whenTrue, string whenFalse, bool appendNewline)
    {
        var trueBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "bool_true");
        var falseBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "bool_false");
        var continueBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "bool_continue");
        LlvmApi.BuildCondBr(state.Target.Builder, condition, trueBlock, falseBlock);

        LlvmApi.PositionBuilderAtEnd(state.Target.Builder, trueBlock);
        EmitWriteBytes(
            state,
            EmitStackByteArray(state, System.Text.Encoding.UTF8.GetBytes(whenTrue)),
            LlvmApi.ConstInt(state.I64, (ulong)whenTrue.Length, 0));
        if (appendNewline)
        {
            EmitWriteBytes(state, EmitStackByteArray(state, [10]), LlvmApi.ConstInt(state.I64, 1, 0));
        }
        LlvmApi.BuildBr(state.Target.Builder, continueBlock);

        LlvmApi.PositionBuilderAtEnd(state.Target.Builder, falseBlock);
        EmitWriteBytes(state, EmitStackByteArray(state, System.Text.Encoding.UTF8.GetBytes(whenFalse)), LlvmApi.ConstInt(state.I64, (ulong)whenFalse.Length, 0));
        if (appendNewline)
        {
            EmitWriteBytes(state, EmitStackByteArray(state, [10]), LlvmApi.ConstInt(state.I64, 1, 0));
        }
        LlvmApi.BuildBr(state.Target.Builder, continueBlock);

        LlvmApi.PositionBuilderAtEnd(state.Target.Builder, continueBlock);
    }

    private static LlvmValueHandle EmitStackStringObject(LlvmCodegenState state, string value)
    {
        byte[] utf8 = System.Text.Encoding.UTF8.GetBytes(value);
        LlvmTypeHandle objectType = LlvmApi.ArrayType2(state.I8, (uint)(utf8.Length + 8));
        LlvmValueHandle storage = LlvmApi.BuildAlloca(state.Target.Builder, objectType, "str_obj");
        LlvmValueHandle lenPtr = LlvmApi.BuildBitCast(state.Target.Builder, storage, state.I64Ptr, "str_obj_len_ptr");
        LlvmApi.BuildStore(state.Target.Builder, LlvmApi.ConstInt(state.I64, (ulong)utf8.Length, 0), lenPtr);

        if (utf8.Length > 0)
        {
            LlvmValueHandle bytesPtr = GetArrayElementPointer(state, objectType, storage, LlvmApi.ConstInt(state.I64, 8, 0), "str_obj_bytes");
            LlvmValueHandle srcPtr = CreateGlobalConstantBytes(state, utf8, "str_obj_data");
            LlvmApi.BuildMemCpy(state.Target.Builder, bytesPtr, 1, srcPtr, 1,
                LlvmApi.ConstInt(state.I64, (ulong)utf8.Length, 0));
        }

        return LlvmApi.BuildPtrToInt(state.Target.Builder, storage, state.I64, "str_obj_i64");
    }

    private static LlvmValueHandle EmitStackByteArray(LlvmCodegenState state, IReadOnlyList<byte> bytes)
    {
        LlvmTypeHandle arrayType = LlvmApi.ArrayType2(state.I8, (uint)bytes.Count);
        LlvmValueHandle storage = LlvmApi.BuildAlloca(state.Target.Builder, arrayType, "byte_array");

        if (bytes.Count > 0)
        {
            LlvmValueHandle destPtr = GetArrayElementPointer(state, arrayType, storage, LlvmApi.ConstInt(state.I64, 0, 0), "byte_array_dest");
            LlvmValueHandle srcPtr = CreateGlobalConstantBytes(state, bytes, "byte_data");
            LlvmApi.BuildMemCpy(state.Target.Builder, destPtr, 1, srcPtr, 1,
                LlvmApi.ConstInt(state.I64, (ulong)bytes.Count, 0));
        }

        return GetArrayElementPointer(state, arrayType, storage, LlvmApi.ConstInt(state.I64, 0, 0), "byte_array_ptr");
    }

    private static void StoreBufferByte(LlvmCodegenState state, LlvmValueHandle buffer, LlvmValueHandle index, byte value)
    {
        StoreBufferByte(state, buffer, index, LlvmApi.ConstInt(state.I64, value, 0));
    }

    private static void StoreBufferByte(LlvmCodegenState state, LlvmValueHandle buffer, LlvmValueHandle index, LlvmValueHandle value)
    {
        LlvmValueHandle ptr = GetArrayElementPointer(state, LlvmApi.ArrayType2(state.I8, 32), buffer, index, "buf_ptr");
        LlvmValueHandle byteValue = LlvmApi.GetTypeKind(LlvmApi.TypeOf(value)) == LlvmTypeKind.Integer && LlvmApi.GetIntTypeWidth(LlvmApi.TypeOf(value)) == 8
            ? value
            : LlvmApi.BuildTrunc(state.Target.Builder, value, state.I8, "to_i8");
        LlvmApi.BuildStore(state.Target.Builder, byteValue, ptr);
    }

    private static LlvmValueHandle GetArrayElementPointer(LlvmCodegenState state, LlvmTypeHandle arrayType, LlvmValueHandle storage, LlvmValueHandle index, string name)
    {
        return LlvmApi.BuildGEP2(state.Target.Builder,
            arrayType,
            storage,
            [
                LlvmApi.ConstInt(state.I64, 0, 0),
                index
            ],
            name);
    }
}
