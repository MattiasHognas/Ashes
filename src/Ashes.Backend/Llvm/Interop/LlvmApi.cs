using System.Runtime.InteropServices;

namespace Ashes.Backend.Llvm.Interop;

// ── Opaque handle types ─────────────────────────────────────────────────
public readonly record struct LlvmContextHandle(nint Ptr);
public readonly record struct LlvmModuleHandle(nint Ptr);
public readonly record struct LlvmBuilderHandle(nint Ptr);
public readonly record struct LlvmTypeHandle(nint Ptr);
public readonly record struct LlvmValueHandle(nint Ptr);
public readonly record struct LlvmBasicBlockHandle(nint Ptr);
public readonly record struct LlvmTargetHandle(nint Ptr);
public readonly record struct LlvmTargetMachineHandle(nint Ptr);
public readonly record struct LlvmTargetDataHandle(nint Ptr);

// ── Enums ───────────────────────────────────────────────────────────────
public enum LlvmIntPredicate
{
    Eq = 32, Ne = 33,
    Ugt = 34, Uge = 35, Ult = 36, Ule = 37,
    Sgt = 38, Sge = 39, Slt = 40, Sle = 41,
}

public enum LlvmRealPredicate
{
    False = 0,
    Oeq = 1, Ogt = 2, Oge = 3, Olt = 4, Ole = 5, One = 6, Ord = 7,
    Uno = 8, Ueq = 9, Ugt = 10, Uge = 11, Ult = 12, Ule = 13, Une = 14,
    True = 15,
}

public enum LlvmCodeGenOptLevel { None = 0, Less = 1, Default = 2, Aggressive = 3 }
public enum LlvmRelocMode { Default = 0, Static = 1, PIC = 2, DynamicNoPic = 3 }
public enum LlvmCodeModel { Default = 0, JITDefault = 1, Tiny = 2, Small = 3, Kernel = 4, Medium = 5, Large = 6 }
public enum LlvmLinkage { External = 0, Internal = 8 }
public enum LlvmCodeGenFileType { Assembly = 0, Object = 1 }
public enum LlvmVerifierFailureAction { AbortProcess = 0, PrintMessage = 1, ReturnStatus = 2 }

public enum LlvmTypeKind
{
    Void = 0, Half = 1, Float = 2, Double = 3, X86Fp80 = 4,
    Fp128 = 5, PpcFp128 = 6, Label = 7, Integer = 8,
    Function = 9, Struct = 10, Array = 11, Pointer = 12,
    Vector = 13, Metadata = 14, Token = 16, ScalableVector = 17,
    BFloat = 18, X86Amx = 19, TargetExt = 20,
}

internal static partial class LlvmApi
{
    private const string Lib = "libLLVM";

    // ── Target initialization ───────────────────────────────────────────
    [LibraryImport(Lib, EntryPoint = "LLVMInitializeX86TargetInfo")]
    public static partial void InitializeX86TargetInfo();

    [LibraryImport(Lib, EntryPoint = "LLVMInitializeX86Target")]
    public static partial void InitializeX86Target();

    [LibraryImport(Lib, EntryPoint = "LLVMInitializeX86TargetMC")]
    public static partial void InitializeX86TargetMC();

    [LibraryImport(Lib, EntryPoint = "LLVMInitializeX86AsmParser")]
    public static partial void InitializeX86AsmParser();

    [LibraryImport(Lib, EntryPoint = "LLVMInitializeX86AsmPrinter")]
    public static partial void InitializeX86AsmPrinter();

    // ── Target lookup & machine ─────────────────────────────────────────
    [LibraryImport(Lib, EntryPoint = "LLVMGetTargetFromTriple", StringMarshalling = StringMarshalling.Utf8)]
    public static partial int GetTargetFromTriple(string triple, out LlvmTargetHandle target, out nint errorMessage);

    [LibraryImport(Lib, EntryPoint = "LLVMCreateTargetMachine", StringMarshalling = StringMarshalling.Utf8)]
    public static partial LlvmTargetMachineHandle CreateTargetMachine(
        LlvmTargetHandle target, string triple, string cpu, string features,
        LlvmCodeGenOptLevel level, LlvmRelocMode reloc, LlvmCodeModel codeModel);

    [LibraryImport(Lib, EntryPoint = "LLVMCreateTargetDataLayout")]
    public static partial LlvmTargetDataHandle CreateTargetDataLayout(LlvmTargetMachineHandle machine);

    [LibraryImport(Lib, EntryPoint = "LLVMCopyStringRepOfTargetData")]
    public static partial nint CopyStringRepOfTargetData(LlvmTargetDataHandle data);

    [LibraryImport(Lib, EntryPoint = "LLVMDisposeTargetData")]
    public static partial void DisposeTargetData(LlvmTargetDataHandle data);

    [LibraryImport(Lib, EntryPoint = "LLVMDisposeTargetMachine")]
    public static partial void DisposeTargetMachine(LlvmTargetMachineHandle machine);

    [LibraryImport(Lib, EntryPoint = "LLVMDisposeMessage")]
    public static partial void DisposeMessage(nint message);

    // ── Context & module ────────────────────────────────────────────────
    [LibraryImport(Lib, EntryPoint = "LLVMContextCreate")]
    public static partial LlvmContextHandle ContextCreate();

    [LibraryImport(Lib, EntryPoint = "LLVMContextDispose")]
    public static partial void ContextDispose(LlvmContextHandle context);

    [LibraryImport(Lib, EntryPoint = "LLVMModuleCreateWithNameInContext", StringMarshalling = StringMarshalling.Utf8)]
    public static partial LlvmModuleHandle ModuleCreateWithNameInContext(string name, LlvmContextHandle context);

    [LibraryImport(Lib, EntryPoint = "LLVMSetTarget", StringMarshalling = StringMarshalling.Utf8)]
    public static partial void SetTarget(LlvmModuleHandle module, string triple);

    [LibraryImport(Lib, EntryPoint = "LLVMSetDataLayout", StringMarshalling = StringMarshalling.Utf8)]
    public static partial void SetDataLayout(LlvmModuleHandle module, string dataLayout);

    [LibraryImport(Lib, EntryPoint = "LLVMDisposeModule")]
    public static partial void DisposeModule(LlvmModuleHandle module);

    // ── Builder ─────────────────────────────────────────────────────────
    [LibraryImport(Lib, EntryPoint = "LLVMCreateBuilderInContext")]
    public static partial LlvmBuilderHandle CreateBuilderInContext(LlvmContextHandle context);

    [LibraryImport(Lib, EntryPoint = "LLVMPositionBuilderAtEnd")]
    public static partial void PositionBuilderAtEnd(LlvmBuilderHandle builder, LlvmBasicBlockHandle block);

    [LibraryImport(Lib, EntryPoint = "LLVMDisposeBuilder")]
    public static partial void DisposeBuilder(LlvmBuilderHandle builder);

    // ── Types ───────────────────────────────────────────────────────────
    [LibraryImport(Lib, EntryPoint = "LLVMInt8TypeInContext")]
    public static partial LlvmTypeHandle Int8TypeInContext(LlvmContextHandle context);

    [LibraryImport(Lib, EntryPoint = "LLVMInt16TypeInContext")]
    public static partial LlvmTypeHandle Int16TypeInContext(LlvmContextHandle context);

    [LibraryImport(Lib, EntryPoint = "LLVMInt32TypeInContext")]
    public static partial LlvmTypeHandle Int32TypeInContext(LlvmContextHandle context);

    [LibraryImport(Lib, EntryPoint = "LLVMInt64TypeInContext")]
    public static partial LlvmTypeHandle Int64TypeInContext(LlvmContextHandle context);

    [LibraryImport(Lib, EntryPoint = "LLVMDoubleTypeInContext")]
    public static partial LlvmTypeHandle DoubleTypeInContext(LlvmContextHandle context);

    [LibraryImport(Lib, EntryPoint = "LLVMVoidTypeInContext")]
    public static partial LlvmTypeHandle VoidTypeInContext(LlvmContextHandle context);

    [LibraryImport(Lib, EntryPoint = "LLVMFunctionType")]
    private static unsafe partial LlvmTypeHandle FunctionTypeRaw(
        LlvmTypeHandle returnType, LlvmTypeHandle* paramTypes, uint paramCount, int isVarArg);

    [LibraryImport(Lib, EntryPoint = "LLVMPointerTypeInContext")]
    public static partial LlvmTypeHandle PointerTypeInContext(LlvmContextHandle context, uint addressSpace);

    [LibraryImport(Lib, EntryPoint = "LLVMArrayType2")]
    public static partial LlvmTypeHandle ArrayType2(LlvmTypeHandle elementType, ulong elementCount);

    // ── Type inspection ─────────────────────────────────────────────────
    [LibraryImport(Lib, EntryPoint = "LLVMTypeOf")]
    public static partial LlvmTypeHandle TypeOf(LlvmValueHandle value);

    [LibraryImport(Lib, EntryPoint = "LLVMGetTypeKind")]
    public static partial LlvmTypeKind GetTypeKind(LlvmTypeHandle type);

    [LibraryImport(Lib, EntryPoint = "LLVMGetIntTypeWidth")]
    public static partial uint GetIntTypeWidth(LlvmTypeHandle type);

    // ── Constants ───────────────────────────────────────────────────────
    [LibraryImport(Lib, EntryPoint = "LLVMConstInt")]
    public static partial LlvmValueHandle ConstInt(LlvmTypeHandle type, ulong value, int signExtend);

    [LibraryImport(Lib, EntryPoint = "LLVMConstNull")]
    public static partial LlvmValueHandle ConstNull(LlvmTypeHandle type);

    [LibraryImport(Lib, EntryPoint = "LLVMConstReal")]
    public static partial LlvmValueHandle ConstReal(LlvmTypeHandle type, double value);

    [LibraryImport(Lib, EntryPoint = "LLVMGetInlineAsm", StringMarshalling = StringMarshalling.Utf8)]
    private static partial LlvmValueHandle GetInlineAsmRaw(
        LlvmTypeHandle functionType, string asmString, nint asmLen,
        string constraints, nint constraintsLen,
        int hasSideEffects, int isAlignStack, int dialect, int canThrow);

    // ── Globals & functions ─────────────────────────────────────────────
    [LibraryImport(Lib, EntryPoint = "LLVMAddFunction", StringMarshalling = StringMarshalling.Utf8)]
    public static partial LlvmValueHandle AddFunction(LlvmModuleHandle module, string name, LlvmTypeHandle type);

    [LibraryImport(Lib, EntryPoint = "LLVMAddGlobal", StringMarshalling = StringMarshalling.Utf8)]
    public static partial LlvmValueHandle AddGlobal(LlvmModuleHandle module, LlvmTypeHandle type, string name);

    [LibraryImport(Lib, EntryPoint = "LLVMSetLinkage")]
    public static partial void SetLinkage(LlvmValueHandle global, LlvmLinkage linkage);

    [LibraryImport(Lib, EntryPoint = "LLVMSetInitializer")]
    public static partial void SetInitializer(LlvmValueHandle global, LlvmValueHandle constant);

    [LibraryImport(Lib, EntryPoint = "LLVMGetParam")]
    public static partial LlvmValueHandle GetParam(LlvmValueHandle function, uint index);

    [LibraryImport(Lib, EntryPoint = "LLVMAppendBasicBlockInContext", StringMarshalling = StringMarshalling.Utf8)]
    public static partial LlvmBasicBlockHandle AppendBasicBlockInContext(
        LlvmContextHandle context, LlvmValueHandle function, string name);

    // ── Arithmetic instructions ─────────────────────────────────────────
    [LibraryImport(Lib, EntryPoint = "LLVMBuildAdd", StringMarshalling = StringMarshalling.Utf8)]
    public static partial LlvmValueHandle BuildAdd(LlvmBuilderHandle b, LlvmValueHandle lhs, LlvmValueHandle rhs, string name);

    [LibraryImport(Lib, EntryPoint = "LLVMBuildSub", StringMarshalling = StringMarshalling.Utf8)]
    public static partial LlvmValueHandle BuildSub(LlvmBuilderHandle b, LlvmValueHandle lhs, LlvmValueHandle rhs, string name);

    [LibraryImport(Lib, EntryPoint = "LLVMBuildMul", StringMarshalling = StringMarshalling.Utf8)]
    public static partial LlvmValueHandle BuildMul(LlvmBuilderHandle b, LlvmValueHandle lhs, LlvmValueHandle rhs, string name);

    [LibraryImport(Lib, EntryPoint = "LLVMBuildSDiv", StringMarshalling = StringMarshalling.Utf8)]
    public static partial LlvmValueHandle BuildSDiv(LlvmBuilderHandle b, LlvmValueHandle lhs, LlvmValueHandle rhs, string name);

    [LibraryImport(Lib, EntryPoint = "LLVMBuildUDiv", StringMarshalling = StringMarshalling.Utf8)]
    public static partial LlvmValueHandle BuildUDiv(LlvmBuilderHandle b, LlvmValueHandle lhs, LlvmValueHandle rhs, string name);

    [LibraryImport(Lib, EntryPoint = "LLVMBuildSRem", StringMarshalling = StringMarshalling.Utf8)]
    public static partial LlvmValueHandle BuildSRem(LlvmBuilderHandle b, LlvmValueHandle lhs, LlvmValueHandle rhs, string name);

    [LibraryImport(Lib, EntryPoint = "LLVMBuildURem", StringMarshalling = StringMarshalling.Utf8)]
    public static partial LlvmValueHandle BuildURem(LlvmBuilderHandle b, LlvmValueHandle lhs, LlvmValueHandle rhs, string name);

    // ── Floating-point instructions ─────────────────────────────────────
    [LibraryImport(Lib, EntryPoint = "LLVMBuildFAdd", StringMarshalling = StringMarshalling.Utf8)]
    public static partial LlvmValueHandle BuildFAdd(LlvmBuilderHandle b, LlvmValueHandle lhs, LlvmValueHandle rhs, string name);

    [LibraryImport(Lib, EntryPoint = "LLVMBuildFSub", StringMarshalling = StringMarshalling.Utf8)]
    public static partial LlvmValueHandle BuildFSub(LlvmBuilderHandle b, LlvmValueHandle lhs, LlvmValueHandle rhs, string name);

    [LibraryImport(Lib, EntryPoint = "LLVMBuildFMul", StringMarshalling = StringMarshalling.Utf8)]
    public static partial LlvmValueHandle BuildFMul(LlvmBuilderHandle b, LlvmValueHandle lhs, LlvmValueHandle rhs, string name);

    [LibraryImport(Lib, EntryPoint = "LLVMBuildFDiv", StringMarshalling = StringMarshalling.Utf8)]
    public static partial LlvmValueHandle BuildFDiv(LlvmBuilderHandle b, LlvmValueHandle lhs, LlvmValueHandle rhs, string name);

    // ── Bitwise instructions ────────────────────────────────────────────
    [LibraryImport(Lib, EntryPoint = "LLVMBuildAnd", StringMarshalling = StringMarshalling.Utf8)]
    public static partial LlvmValueHandle BuildAnd(LlvmBuilderHandle b, LlvmValueHandle lhs, LlvmValueHandle rhs, string name);

    [LibraryImport(Lib, EntryPoint = "LLVMBuildOr", StringMarshalling = StringMarshalling.Utf8)]
    public static partial LlvmValueHandle BuildOr(LlvmBuilderHandle b, LlvmValueHandle lhs, LlvmValueHandle rhs, string name);

    [LibraryImport(Lib, EntryPoint = "LLVMBuildXor", StringMarshalling = StringMarshalling.Utf8)]
    public static partial LlvmValueHandle BuildXor(LlvmBuilderHandle b, LlvmValueHandle lhs, LlvmValueHandle rhs, string name);

    [LibraryImport(Lib, EntryPoint = "LLVMBuildShl", StringMarshalling = StringMarshalling.Utf8)]
    public static partial LlvmValueHandle BuildShl(LlvmBuilderHandle b, LlvmValueHandle lhs, LlvmValueHandle rhs, string name);

    [LibraryImport(Lib, EntryPoint = "LLVMBuildLShr", StringMarshalling = StringMarshalling.Utf8)]
    public static partial LlvmValueHandle BuildLShr(LlvmBuilderHandle b, LlvmValueHandle lhs, LlvmValueHandle rhs, string name);

    // ── Comparison instructions ─────────────────────────────────────────
    [LibraryImport(Lib, EntryPoint = "LLVMBuildICmp", StringMarshalling = StringMarshalling.Utf8)]
    public static partial LlvmValueHandle BuildICmp(LlvmBuilderHandle b, LlvmIntPredicate op, LlvmValueHandle lhs, LlvmValueHandle rhs, string name);

    [LibraryImport(Lib, EntryPoint = "LLVMBuildFCmp", StringMarshalling = StringMarshalling.Utf8)]
    public static partial LlvmValueHandle BuildFCmp(LlvmBuilderHandle b, LlvmRealPredicate op, LlvmValueHandle lhs, LlvmValueHandle rhs, string name);

    // ── Conversion instructions ─────────────────────────────────────────
    [LibraryImport(Lib, EntryPoint = "LLVMBuildSExt", StringMarshalling = StringMarshalling.Utf8)]
    public static partial LlvmValueHandle BuildSExt(LlvmBuilderHandle b, LlvmValueHandle val, LlvmTypeHandle destType, string name);

    [LibraryImport(Lib, EntryPoint = "LLVMBuildZExt", StringMarshalling = StringMarshalling.Utf8)]
    public static partial LlvmValueHandle BuildZExt(LlvmBuilderHandle b, LlvmValueHandle val, LlvmTypeHandle destType, string name);

    [LibraryImport(Lib, EntryPoint = "LLVMBuildTrunc", StringMarshalling = StringMarshalling.Utf8)]
    public static partial LlvmValueHandle BuildTrunc(LlvmBuilderHandle b, LlvmValueHandle val, LlvmTypeHandle destType, string name);

    [LibraryImport(Lib, EntryPoint = "LLVMBuildIntToPtr", StringMarshalling = StringMarshalling.Utf8)]
    public static partial LlvmValueHandle BuildIntToPtr(LlvmBuilderHandle b, LlvmValueHandle val, LlvmTypeHandle destType, string name);

    [LibraryImport(Lib, EntryPoint = "LLVMBuildPtrToInt", StringMarshalling = StringMarshalling.Utf8)]
    public static partial LlvmValueHandle BuildPtrToInt(LlvmBuilderHandle b, LlvmValueHandle val, LlvmTypeHandle destType, string name);

    [LibraryImport(Lib, EntryPoint = "LLVMBuildBitCast", StringMarshalling = StringMarshalling.Utf8)]
    public static partial LlvmValueHandle BuildBitCast(LlvmBuilderHandle b, LlvmValueHandle val, LlvmTypeHandle destType, string name);

    // ── Memory instructions ─────────────────────────────────────────────
    [LibraryImport(Lib, EntryPoint = "LLVMBuildAlloca", StringMarshalling = StringMarshalling.Utf8)]
    public static partial LlvmValueHandle BuildAlloca(LlvmBuilderHandle b, LlvmTypeHandle type, string name);

    [LibraryImport(Lib, EntryPoint = "LLVMBuildLoad2", StringMarshalling = StringMarshalling.Utf8)]
    public static partial LlvmValueHandle BuildLoad2(LlvmBuilderHandle b, LlvmTypeHandle type, LlvmValueHandle ptr, string name);

    [LibraryImport(Lib, EntryPoint = "LLVMBuildStore")]
    public static partial LlvmValueHandle BuildStore(LlvmBuilderHandle b, LlvmValueHandle val, LlvmValueHandle ptr);

    [LibraryImport(Lib, EntryPoint = "LLVMBuildGEP2", StringMarshalling = StringMarshalling.Utf8)]
    private static unsafe partial LlvmValueHandle BuildGEP2Raw(
        LlvmBuilderHandle b, LlvmTypeHandle type, LlvmValueHandle ptr,
        LlvmValueHandle* indices, uint numIndices, string name);

    // ── Call & select ───────────────────────────────────────────────────
    [LibraryImport(Lib, EntryPoint = "LLVMBuildCall2", StringMarshalling = StringMarshalling.Utf8)]
    private static unsafe partial LlvmValueHandle BuildCall2Raw(
        LlvmBuilderHandle b, LlvmTypeHandle fnType, LlvmValueHandle fn,
        LlvmValueHandle* args, uint numArgs, string name);

    [LibraryImport(Lib, EntryPoint = "LLVMBuildSelect", StringMarshalling = StringMarshalling.Utf8)]
    public static partial LlvmValueHandle BuildSelect(
        LlvmBuilderHandle b, LlvmValueHandle cond, LlvmValueHandle thenVal, LlvmValueHandle elseVal, string name);

    // ── Control flow instructions ───────────────────────────────────────
    [LibraryImport(Lib, EntryPoint = "LLVMBuildBr")]
    public static partial LlvmValueHandle BuildBr(LlvmBuilderHandle b, LlvmBasicBlockHandle dest);

    [LibraryImport(Lib, EntryPoint = "LLVMBuildCondBr")]
    public static partial LlvmValueHandle BuildCondBr(
        LlvmBuilderHandle b, LlvmValueHandle cond, LlvmBasicBlockHandle thenBlock, LlvmBasicBlockHandle elseBlock);

    [LibraryImport(Lib, EntryPoint = "LLVMBuildRet")]
    public static partial LlvmValueHandle BuildRet(LlvmBuilderHandle b, LlvmValueHandle value);

    [LibraryImport(Lib, EntryPoint = "LLVMBuildRetVoid")]
    public static partial LlvmValueHandle BuildRetVoid(LlvmBuilderHandle b);

    [LibraryImport(Lib, EntryPoint = "LLVMBuildUnreachable")]
    public static partial LlvmValueHandle BuildUnreachable(LlvmBuilderHandle b);

    // ── Verification ────────────────────────────────────────────────────
    [LibraryImport(Lib, EntryPoint = "LLVMVerifyModule")]
    public static partial int VerifyModule(LlvmModuleHandle module, LlvmVerifierFailureAction action, out nint outMessage);

    // ── Code emission ───────────────────────────────────────────────────
    [LibraryImport(Lib, EntryPoint = "LLVMTargetMachineEmitToMemoryBuffer")]
    public static partial int TargetMachineEmitToMemoryBuffer(
        LlvmTargetMachineHandle machine, LlvmModuleHandle module,
        LlvmCodeGenFileType codegen, out nint errorMessage, out nint outMemBuf);

    [LibraryImport(Lib, EntryPoint = "LLVMGetBufferStart")]
    public static partial nint GetBufferStart(nint memBuf);

    [LibraryImport(Lib, EntryPoint = "LLVMGetBufferSize")]
    public static partial nint GetBufferSize(nint memBuf);

    [LibraryImport(Lib, EntryPoint = "LLVMDisposeMemoryBuffer")]
    public static partial void DisposeMemoryBuffer(nint memBuf);

    // ── Safe convenience wrappers ───────────────────────────────────────

    public static LlvmTypeHandle FunctionType(LlvmTypeHandle returnType, ReadOnlySpan<LlvmTypeHandle> paramTypes, int isVarArg = 0)
    {
        unsafe
        {
            fixed (LlvmTypeHandle* ptr = paramTypes)
            {
                return FunctionTypeRaw(returnType, ptr, (uint)paramTypes.Length, isVarArg);
            }
        }
    }

    public static LlvmValueHandle BuildCall2(
        LlvmBuilderHandle b, LlvmTypeHandle fnType, LlvmValueHandle fn,
        ReadOnlySpan<LlvmValueHandle> args, string name)
    {
        unsafe
        {
            fixed (LlvmValueHandle* ptr = args)
            {
                return BuildCall2Raw(b, fnType, fn, ptr, (uint)args.Length, name);
            }
        }
    }

    public static LlvmValueHandle BuildGEP2(
        LlvmBuilderHandle b, LlvmTypeHandle type, LlvmValueHandle ptr,
        ReadOnlySpan<LlvmValueHandle> indices, string name)
    {
        unsafe
        {
            fixed (LlvmValueHandle* p = indices)
            {
                return BuildGEP2Raw(b, type, ptr, p, (uint)indices.Length, name);
            }
        }
    }

    public static LlvmValueHandle GetInlineAsm(
        LlvmTypeHandle functionType, string asmString, string constraints,
        bool hasSideEffects, bool isAlignStack)
    {
        return GetInlineAsmRaw(
            functionType, asmString, (nint)asmString.Length,
            constraints, (nint)constraints.Length,
            hasSideEffects ? 1 : 0, isAlignStack ? 1 : 0, 0, 0);
    }
}
