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
public readonly record struct LlvmDIBuilderHandle(nint Ptr);
public readonly record struct LlvmMetadataHandle(nint Ptr);
public readonly record struct LlvmPassBuilderOptionsHandle(nint Ptr);
public readonly record struct LlvmAttributeHandle(nint Ptr);

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

    [LibraryImport(Lib, EntryPoint = "LLVMInitializeAArch64TargetInfo")]
    public static partial void InitializeAArch64TargetInfo();

    [LibraryImport(Lib, EntryPoint = "LLVMInitializeAArch64Target")]
    public static partial void InitializeAArch64Target();

    [LibraryImport(Lib, EntryPoint = "LLVMInitializeAArch64TargetMC")]
    public static partial void InitializeAArch64TargetMC();

    [LibraryImport(Lib, EntryPoint = "LLVMInitializeAArch64AsmParser")]
    public static partial void InitializeAArch64AsmParser();

    [LibraryImport(Lib, EntryPoint = "LLVMInitializeAArch64AsmPrinter")]
    public static partial void InitializeAArch64AsmPrinter();

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

    // ── Host CPU detection ──────────────────────────────────────────────
    [LibraryImport(Lib, EntryPoint = "LLVMGetHostCPUName")]
    private static partial nint GetHostCPUNameRaw();

    [LibraryImport(Lib, EntryPoint = "LLVMGetHostCPUFeatures")]
    private static partial nint GetHostCPUFeaturesRaw();

    /// <summary>Returns the host CPU name (e.g. "skylake", "apple-m1"). The LLVM message is freed internally.</summary>
    public static string GetHostCPUName()
    {
        nint ptr = GetHostCPUNameRaw();
        try { return Marshal.PtrToStringAnsi(ptr) ?? string.Empty; }
        finally { DisposeMessage(ptr); }
    }

    /// <summary>Returns the host CPU feature string (e.g. "+sse4.2,+avx2,...").</summary>
    public static string GetHostCPUFeatures()
    {
        nint ptr = GetHostCPUFeaturesRaw();
        try { return Marshal.PtrToStringAnsi(ptr) ?? string.Empty; }
        finally { DisposeMessage(ptr); }
    }

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

    [LibraryImport(Lib, EntryPoint = "LLVMGetInsertBlock")]
    public static partial LlvmBasicBlockHandle GetInsertBlock(LlvmBuilderHandle builder);

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

    [LibraryImport(Lib, EntryPoint = "LLVMConstArray2")]
    private static unsafe partial LlvmValueHandle ConstArray2Raw(LlvmTypeHandle elementType, LlvmValueHandle* constantVals, ulong length);

    [LibraryImport(Lib, EntryPoint = "LLVMConstStructInContext")]
    private static unsafe partial LlvmValueHandle ConstStructInContextRaw(
        LlvmContextHandle context, LlvmValueHandle* constantVals, uint count, int packed);

    [LibraryImport(Lib, EntryPoint = "LLVMStructTypeInContext")]
    private static unsafe partial LlvmTypeHandle StructTypeInContextRaw(
        LlvmContextHandle context, LlvmTypeHandle* elementTypes, uint elementCount, int packed);

    [LibraryImport(Lib, EntryPoint = "LLVMGetInlineAsm", StringMarshalling = StringMarshalling.Utf8)]
    private static partial LlvmValueHandle GetInlineAsmRaw(
        LlvmTypeHandle functionType, string asmString, nint asmLen,
        string constraints, nint constraintsLen,
        int hasSideEffects, int isAlignStack, int dialect, int canThrow);

    // ── Globals & functions ─────────────────────────────────────────────
    [LibraryImport(Lib, EntryPoint = "LLVMAddFunction", StringMarshalling = StringMarshalling.Utf8)]
    public static partial LlvmValueHandle AddFunction(LlvmModuleHandle module, string name, LlvmTypeHandle type);

    [LibraryImport(Lib, EntryPoint = "LLVMGetNamedFunction", StringMarshalling = StringMarshalling.Utf8)]
    public static partial LlvmValueHandle GetNamedFunction(LlvmModuleHandle module, string name);

    [LibraryImport(Lib, EntryPoint = "LLVMAddGlobal", StringMarshalling = StringMarshalling.Utf8)]
    public static partial LlvmValueHandle AddGlobal(LlvmModuleHandle module, LlvmTypeHandle type, string name);

    [LibraryImport(Lib, EntryPoint = "LLVMSetLinkage")]
    public static partial void SetLinkage(LlvmValueHandle global, LlvmLinkage linkage);

    [LibraryImport(Lib, EntryPoint = "LLVMSetInitializer")]
    public static partial void SetInitializer(LlvmValueHandle global, LlvmValueHandle constant);

    [LibraryImport(Lib, EntryPoint = "LLVMSetGlobalConstant")]
    public static partial void SetGlobalConstant(LlvmValueHandle global, int isConstant);

    [LibraryImport(Lib, EntryPoint = "LLVMSetUnnamedAddr")]
    public static partial void SetUnnamedAddr(LlvmValueHandle global, int unnamedAddr);

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

    [LibraryImport(Lib, EntryPoint = "LLVMBuildMemCpy")]
    public static partial LlvmValueHandle BuildMemCpy(
        LlvmBuilderHandle b, LlvmValueHandle dst, uint dstAlign,
        LlvmValueHandle src, uint srcAlign, LlvmValueHandle size);

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

    // ── Tail call support ───────────────────────────────────────────────
    [LibraryImport(Lib, EntryPoint = "LLVMSetTailCall")]
    public static partial void SetTailCall(LlvmValueHandle callInst, int isTailCall);

    // ── Function attributes ─────────────────────────────────────────────
    [LibraryImport(Lib, EntryPoint = "LLVMGetEnumAttributeKindForName", StringMarshalling = StringMarshalling.Utf8)]
    private static partial uint GetEnumAttributeKindForNameRaw(string name, nint nameLen);

    [LibraryImport(Lib, EntryPoint = "LLVMCreateEnumAttribute")]
    public static partial LlvmAttributeHandle CreateEnumAttribute(LlvmContextHandle context, uint kindId, ulong val);

    [LibraryImport(Lib, EntryPoint = "LLVMAddAttributeAtIndex")]
    public static partial void AddAttributeAtIndex(LlvmValueHandle function, uint index, LlvmAttributeHandle attribute);

    [LibraryImport(Lib, EntryPoint = "LLVMCreateStringAttribute")]
    private static partial LlvmAttributeHandle CreateStringAttributeRaw(
        LlvmContextHandle context, nint kind, uint kindLength, nint value, uint valueLength);

    /// <summary>Function-level attribute index for <see cref="AddAttributeAtIndex"/>.</summary>
    public const uint AttributeIndexFunction = 0xFFFFFFFF; // (uint)-1

    /// <summary>Return-value attribute index for <see cref="AddAttributeAtIndex"/>.</summary>
    public const uint AttributeIndexReturn = 0;

    public static uint GetEnumAttributeKindForName(string name) =>
        GetEnumAttributeKindForNameRaw(name, (nint)name.Length);

    /// <summary>
    /// Creates a string attribute (e.g. <c>memory(read)</c>) via the LLVM C API.
    /// Used for attributes that cannot be represented as enum attributes.
    /// </summary>
    public static LlvmAttributeHandle CreateStringAttribute(LlvmContextHandle context, string kind, string value)
    {
        var kindBytes = System.Text.Encoding.UTF8.GetBytes(kind);
        var valueBytes = System.Text.Encoding.UTF8.GetBytes(value);
        unsafe
        {
            fixed (byte* kp = kindBytes)
            fixed (byte* vp = valueBytes)
            {
                return CreateStringAttributeRaw(context, (nint)kp, (uint)kindBytes.Length, (nint)vp, (uint)valueBytes.Length);
            }
        }
    }

    // ── Debugging ───────────────────────────────────────────────────────
    [LibraryImport(Lib, EntryPoint = "LLVMPrintModuleToString")]
    public static partial nint PrintModuleToString(LlvmModuleHandle module);

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

    public static LlvmValueHandle ConstArray2(LlvmTypeHandle elementType, ReadOnlySpan<LlvmValueHandle> constantVals)
    {
        unsafe
        {
            fixed (LlvmValueHandle* ptr = constantVals)
            {
                return ConstArray2Raw(elementType, ptr, (ulong)constantVals.Length);
            }
        }
    }

    public static LlvmValueHandle ConstStructInContext(LlvmContextHandle context, ReadOnlySpan<LlvmValueHandle> constantVals, bool packed = false)
    {
        unsafe
        {
            fixed (LlvmValueHandle* ptr = constantVals)
            {
                return ConstStructInContextRaw(context, ptr, (uint)constantVals.Length, packed ? 1 : 0);
            }
        }
    }

    public static LlvmTypeHandle StructTypeInContext(LlvmContextHandle context, ReadOnlySpan<LlvmTypeHandle> elementTypes, bool packed = false)
    {
        unsafe
        {
            fixed (LlvmTypeHandle* ptr = elementTypes)
            {
                return StructTypeInContextRaw(context, ptr, (uint)elementTypes.Length, packed ? 1 : 0);
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

    // ── Debug Info (DWARF) ──────────────────────────────────────────────

    [LibraryImport(Lib, EntryPoint = "LLVMCreateDIBuilder")]
    public static partial LlvmDIBuilderHandle CreateDIBuilder(LlvmModuleHandle module);

    [LibraryImport(Lib, EntryPoint = "LLVMDisposeDIBuilder")]
    public static partial void DisposeDIBuilder(LlvmDIBuilderHandle builder);

    [LibraryImport(Lib, EntryPoint = "LLVMDIBuilderFinalize")]
    public static partial void DIBuilderFinalize(LlvmDIBuilderHandle builder);

    [LibraryImport(Lib, EntryPoint = "LLVMDIBuilderCreateFile", StringMarshalling = StringMarshalling.Utf8)]
    public static partial LlvmMetadataHandle DIBuilderCreateFile(
        LlvmDIBuilderHandle builder,
        string filename, nint filenameLen,
        string directory, nint directoryLen);

    public static LlvmMetadataHandle DIBuilderCreateFile(LlvmDIBuilderHandle builder, string filename, string directory)
    {
        return DIBuilderCreateFile(builder, filename, (nint)filename.Length, directory, (nint)directory.Length);
    }

    [LibraryImport(Lib, EntryPoint = "LLVMDIBuilderCreateCompileUnit", StringMarshalling = StringMarshalling.Utf8)]
    public static partial LlvmMetadataHandle DIBuilderCreateCompileUnit(
        LlvmDIBuilderHandle builder,
        uint lang,
        LlvmMetadataHandle fileRef,
        string producer, nint producerLen,
        int isOptimized,
        string flags, nint flagsLen,
        uint runtimeVer,
        string splitName, nint splitNameLen,
        uint kind,
        uint dwoId,
        int splitDebugInlining,
        int debugInfoForProfiling,
        string sysRoot, nint sysRootLen,
        string sdk, nint sdkLen);

    public static LlvmMetadataHandle DIBuilderCreateCompileUnit(
        LlvmDIBuilderHandle builder, uint lang, LlvmMetadataHandle fileRef,
        string producer, bool isOptimized)
    {
        return DIBuilderCreateCompileUnit(
            builder, lang, fileRef,
            producer, (nint)producer.Length,
            isOptimized ? 1 : 0,
            "", 0,    // flags
            0,        // runtimeVer
            "", 0,    // splitName
            1,        // DW_NameTableKind_Default (full debug)
            0,        // dwoId
            1,        // splitDebugInlining
            0,        // debugInfoForProfiling
            "", 0,    // sysRoot
            "", 0);   // sdk
    }

    [LibraryImport(Lib, EntryPoint = "LLVMDIBuilderCreateFunction", StringMarshalling = StringMarshalling.Utf8)]
    public static partial LlvmMetadataHandle DIBuilderCreateFunction(
        LlvmDIBuilderHandle builder,
        LlvmMetadataHandle scope,
        string name, nint nameLen,
        string linkageName, nint linkageNameLen,
        LlvmMetadataHandle file,
        uint lineNo,
        LlvmMetadataHandle ty,
        int isLocalToUnit,
        int isDefinition,
        uint scopeLine,
        uint flags,
        int isOptimized);

    public static LlvmMetadataHandle DIBuilderCreateFunction(
        LlvmDIBuilderHandle builder, LlvmMetadataHandle scope,
        string name, string linkageName, LlvmMetadataHandle file,
        uint line, LlvmMetadataHandle ty, bool isOptimized)
    {
        return DIBuilderCreateFunction(
            builder, scope,
            name, (nint)name.Length,
            linkageName, (nint)linkageName.Length,
            file, line, ty,
            0,   // not local to unit
            1,   // is definition
            line, // scopeLine
            0,   // flags
            isOptimized ? 1 : 0);
    }

    [LibraryImport(Lib, EntryPoint = "LLVMDIBuilderCreateSubroutineType")]
    public static partial LlvmMetadataHandle DIBuilderCreateSubroutineType(
        LlvmDIBuilderHandle builder,
        LlvmMetadataHandle file,
        nint parameterTypes,
        uint numParameterTypes,
        uint flags);

    public static LlvmMetadataHandle DIBuilderCreateSubroutineType(LlvmDIBuilderHandle builder, LlvmMetadataHandle file)
    {
        return DIBuilderCreateSubroutineType(builder, file, 0, 0, 0);
    }

    [LibraryImport(Lib, EntryPoint = "LLVMDIBuilderCreateBasicType", StringMarshalling = StringMarshalling.Utf8)]
    public static partial LlvmMetadataHandle DIBuilderCreateBasicType(
        LlvmDIBuilderHandle builder,
        string name, nint nameLen,
        ulong sizeInBits,
        uint encoding,
        uint flags);

    public static LlvmMetadataHandle DIBuilderCreateBasicType(
        LlvmDIBuilderHandle builder, string name, ulong sizeInBits, uint encoding)
    {
        return DIBuilderCreateBasicType(builder, name, (nint)name.Length, sizeInBits, encoding, 0);
    }

    [LibraryImport(Lib, EntryPoint = "LLVMDIBuilderCreatePointerType")]
    public static partial LlvmMetadataHandle DIBuilderCreatePointerType(
        LlvmDIBuilderHandle builder,
        LlvmMetadataHandle pointeeType,
        ulong sizeInBits,
        uint alignInBits,
        uint addressSpace,
        nint name, nint nameLen);

    public static LlvmMetadataHandle DIBuilderCreatePointerType(
        LlvmDIBuilderHandle builder, LlvmMetadataHandle pointeeType, ulong sizeInBits)
    {
        return DIBuilderCreatePointerType(builder, pointeeType, sizeInBits, 0, 0, 0, 0);
    }

    [LibraryImport(Lib, EntryPoint = "LLVMDIBuilderCreateAutoVariable", StringMarshalling = StringMarshalling.Utf8)]
    public static partial LlvmMetadataHandle DIBuilderCreateAutoVariable(
        LlvmDIBuilderHandle builder,
        LlvmMetadataHandle scope,
        string name, nint nameLen,
        LlvmMetadataHandle file,
        uint lineNo,
        LlvmMetadataHandle ty,
        int alwaysPreserve,
        uint flags,
        uint alignInBits);

    public static LlvmMetadataHandle DIBuilderCreateAutoVariable(
        LlvmDIBuilderHandle builder, LlvmMetadataHandle scope,
        string name, LlvmMetadataHandle file, uint line, LlvmMetadataHandle ty)
    {
        return DIBuilderCreateAutoVariable(
            builder, scope, name, (nint)name.Length, file, line, ty, 1, 0, 0);
    }

    [LibraryImport(Lib, EntryPoint = "LLVMDIBuilderCreateParameterVariable", StringMarshalling = StringMarshalling.Utf8)]
    public static partial LlvmMetadataHandle DIBuilderCreateParameterVariable(
        LlvmDIBuilderHandle builder,
        LlvmMetadataHandle scope,
        string name, nint nameLen,
        uint argNo,
        LlvmMetadataHandle file,
        uint lineNo,
        LlvmMetadataHandle ty,
        int alwaysPreserve,
        uint flags);

    public static LlvmMetadataHandle DIBuilderCreateParameterVariable(
        LlvmDIBuilderHandle builder, LlvmMetadataHandle scope,
        string name, uint argNo, LlvmMetadataHandle file, uint line, LlvmMetadataHandle ty)
    {
        return DIBuilderCreateParameterVariable(
            builder, scope, name, (nint)name.Length, argNo, file, line, ty, 1, 0);
    }

    // LLVM 22: LLVMDIBuilderInsertDeclareAtEnd was removed; use the Record variant.
    // Returns LLVMDbgRecordRef (an opaque pointer); we don't use the return value.
    [LibraryImport(Lib, EntryPoint = "LLVMDIBuilderInsertDeclareRecordAtEnd")]
    public static partial nint DIBuilderInsertDeclareRecordAtEnd(
        LlvmDIBuilderHandle builder,
        LlvmValueHandle storage,
        LlvmMetadataHandle varInfo,
        LlvmMetadataHandle expr,
        LlvmMetadataHandle debugLoc,
        LlvmBasicBlockHandle block);

    [LibraryImport(Lib, EntryPoint = "LLVMDIBuilderCreateExpression")]
    public static partial LlvmMetadataHandle DIBuilderCreateExpression(
        LlvmDIBuilderHandle builder,
        nint addr,
        nint length);

    public static LlvmMetadataHandle DIBuilderCreateExpression(LlvmDIBuilderHandle builder)
    {
        return DIBuilderCreateExpression(builder, 0, 0);
    }

    [LibraryImport(Lib, EntryPoint = "LLVMSetSubprogram")]
    public static partial void SetSubprogram(LlvmValueHandle func, LlvmMetadataHandle sp);

    [LibraryImport(Lib, EntryPoint = "LLVMSetCurrentDebugLocation2")]
    public static partial void SetCurrentDebugLocation2(LlvmBuilderHandle builder, LlvmMetadataHandle loc);

    [LibraryImport(Lib, EntryPoint = "LLVMCreateDebugLocation")]
    public static partial LlvmMetadataHandle CreateDebugLocation(
        LlvmContextHandle ctx,
        uint line,
        uint column,
        LlvmMetadataHandle scope,
        LlvmMetadataHandle inlinedAt);

    [LibraryImport(Lib, EntryPoint = "LLVMAddModuleFlag", StringMarshalling = StringMarshalling.Utf8)]
    public static partial void AddModuleFlag(
        LlvmModuleHandle module,
        int behavior,
        string key, nint keyLen,
        LlvmMetadataHandle val);

    public static void AddModuleFlag(LlvmModuleHandle module, int behavior, string key, LlvmMetadataHandle val)
    {
        AddModuleFlag(module, behavior, key, (nint)key.Length, val);
    }

    [LibraryImport(Lib, EntryPoint = "LLVMValueAsMetadata")]
    public static partial LlvmMetadataHandle ValueAsMetadata(LlvmValueHandle val);

    [LibraryImport(Lib, EntryPoint = "LLVMMetadataAsValue")]
    public static partial LlvmValueHandle MetadataAsValue(LlvmContextHandle context, LlvmMetadataHandle md);

    // ── LLVM New Pass Manager (Phase 4: optimization pipeline) ────────

    /// <summary>
    /// Run a pipeline of LLVM passes on the module.
    /// <paramref name="passes"/> is a comma-separated list of pass names,
    /// e.g. "default&lt;O2&gt;" or "instcombine,simplifycfg,mem2reg".
    /// Returns 0 on success, non-zero on error (error message in <paramref name="errorMessage"/>).
    /// </summary>
    [LibraryImport(Lib, EntryPoint = "LLVMRunPasses", StringMarshalling = StringMarshalling.Utf8)]
    public static partial int RunPasses(
        LlvmModuleHandle module,
        string passes,
        LlvmTargetMachineHandle targetMachine,
        LlvmPassBuilderOptionsHandle options);

    [LibraryImport(Lib, EntryPoint = "LLVMCreatePassBuilderOptions")]
    public static partial LlvmPassBuilderOptionsHandle CreatePassBuilderOptions();

    [LibraryImport(Lib, EntryPoint = "LLVMDisposePassBuilderOptions")]
    public static partial void DisposePassBuilderOptions(LlvmPassBuilderOptionsHandle options);

    [LibraryImport(Lib, EntryPoint = "LLVMPassBuilderOptionsSetDebugLogging")]
    public static partial void PassBuilderOptionsSetDebugLogging(LlvmPassBuilderOptionsHandle options, int debugLogging);

    // ── Constants for debug info ────────────────────────────────────────

    /// <summary>User-defined DWARF language code for Ashes (DW_LANG_lo_user + 1).</summary>
    public const uint DwarfLangAshes = 0x8001;

    /// <summary>DW_ATE_signed — DWARF signed integer encoding.</summary>
    public const uint DwarfAteSigned = 5;

    /// <summary>DW_ATE_float — DWARF floating-point encoding.</summary>
    public const uint DwarfAteFloat = 4;

    /// <summary>DW_ATE_boolean — DWARF boolean encoding.</summary>
    public const uint DwarfAteBoolean = 2;

    /// <summary>LLVMModuleFlagBehaviorWarning — module flag behavior: warn on conflict.</summary>
    public const int ModuleFlagBehaviorWarning = 1;
}
