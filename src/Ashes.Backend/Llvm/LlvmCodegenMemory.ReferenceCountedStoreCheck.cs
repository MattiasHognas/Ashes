using Ashes.Backend.Llvm.Interop;

namespace Ashes.Backend.Llvm;

// Debugging aid: a block in the reference-counted region must never come to point at arena or stack
// memory. Compiling with ASHES_RC_VERIFY=1 makes every lowered store check that and raise SIGABRT at
// the offending store.
internal static partial class LlvmCodegen
{
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
        LlvmValueHandle zero = LlvmApi.ConstInt(state.I64, 0, 0);
        LlvmValueHandle baseInRegion = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne,
            EmitIsReferenceCounted(state, baseAddress, name + "_verify_base"), zero, name + "_verify_base_rc");
        LlvmValueHandle valueOutsideRegion = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq,
            EmitIsReferenceCounted(state, value, name + "_verify_value"), zero, name + "_verify_value_not_rc");
        // Anonymous mappings and the stack sit in the top terabyte of the user address space.
        LlvmValueHandle valueIsMapping = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq,
            LlvmApi.BuildLShr(builder, value, LlvmApi.ConstInt(state.I64, 40, 0), name + "_verify_value_high"),
            LlvmApi.ConstInt(state.I64, 0x7f, 0), name + "_verify_value_mapping");
        LlvmValueHandle violates = LlvmApi.BuildAnd(builder, baseInRegion,
            LlvmApi.BuildAnd(builder, valueOutsideRegion, valueIsMapping, name + "_verify_value_bad"), name + "_verify_violates");
        LlvmBasicBlockHandle abortBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, name + "_verify_abort");
        LlvmBasicBlockHandle okBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, name + "_verify_ok");
        LlvmApi.BuildCondBr(builder, violates, abortBlock, okBlock);
        LlvmApi.PositionBuilderAtEnd(builder, abortBlock);
        LlvmValueHandle pid = EmitLinuxSyscall(state, 39, zero, zero, zero, name + "_verify_getpid");
        EmitLinuxSyscall(state, 62, pid, LlvmApi.ConstInt(state.I64, 6, 0), zero, name + "_verify_kill");
        LlvmApi.BuildBr(builder, okBlock);
        LlvmApi.PositionBuilderAtEnd(builder, okBlock);
    }
}
