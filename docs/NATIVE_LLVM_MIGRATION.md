# LLVM Native Library Migration Guide

This document describes how to replace the **LLVMSharp** and **libLLVM.runtime.\***
NuGet packages with direct P/Invoke calls against the official LLVM C API
libraries downloaded from LLVM releases.

---

## Overview

Currently `Ashes.Backend` depends on three NuGet packages for LLVM:

| Package | Purpose |
|---------|---------|
| `LLVMSharp` 20.1.2 | C# wrappers around the LLVM C API |
| `libLLVM.runtime.linux-x64` 20.1.2 | Ships `libLLVM.so` (native, Linux) |
| `libLLVM.runtime.win-x64` 20.1.2 | Ships `libLLVM.dll` (native, Windows) |

After the migration:

- **LLVMSharp** is removed entirely; replaced by a thin P/Invoke interop layer.
- **libLLVM.runtime.\*** packages are removed; replaced by native libraries
  downloaded from official LLVM GitHub releases via the provided scripts.

---

## Step 0 – Provision native libraries

### Linux – apt install

The official LLVM 22+ Linux release only ships static `.a` archives — **no shared
`libLLVM.so`**. Use `apt` to install the shared library:

```bash
./scripts/download-llvm-native.sh        # default major = 22
./scripts/download-llvm-native.sh 23     # or specify a different major
```

This runs `sudo apt-get install libllvm22` (adding the LLVM apt repo if needed),
then symlinks the installed `libLLVM-22.so` into `lib/Ashes/linux-x64/libLLVM.so`.

### Windows – download from LLVM release

The official Windows release (`clang+llvm-*-x86_64-pc-windows-msvc.tar.xz`)
ships `LLVM-C.dll`, the shared C API library.

```powershell
.\scripts\download-llvm-native.ps1                       # default: 22.1.2
.\scripts\download-llvm-native.ps1 -LlvmVersion 22.1.3   # or specify a version
```

This downloads `LLVM-C.dll` and renames it to `libLLVM.dll` in
`lib/Ashes/win-x64/`.

### Result

```
lib/Ashes/
  linux-x64/libLLVM.so   ← symlink to /usr/lib/.../libLLVM-22.so (from apt)
  win-x64/libLLVM.dll     ← LLVM-C.dll from LLVM release, renamed
```

> The files are named `libLLVM.{so,dll}` to match the existing DllImport name
> used by LLVMSharp (`"libLLVM"`), making the transition seamless.
> The publish scripts (`scripts/publish.{sh,ps1}`) already copy the whole
> `lib/` tree to the output, so these files are automatically included in
> published builds.

---

## Step 1 – Update Ashes.Backend.csproj

Remove the three LLVMSharp-related PackageReferences and uncomment the native
library items that are already prepared in the csproj:

```xml
<!-- REMOVE these three lines -->
<PackageReference Include="LLVMSharp" Version="20.1.2" />
<PackageReference Include="libLLVM.runtime.linux-x64" Version="20.1.2" />
<PackageReference Include="libLLVM.runtime.win-x64" Version="20.1.2" />

<!-- UNCOMMENT this block (already in the csproj) -->
<ItemGroup>
  <None Include="..\..\lib\Ashes\win-x64\libLLVM.dll"
        Condition="Exists('..\..\lib\Ashes\win-x64\libLLVM.dll')"
        CopyToOutputDirectory="PreserveNewest"
        Link="runtimes\win-x64\native\libLLVM.dll" />
  <None Include="..\..\lib\Ashes\linux-x64\libLLVM.so"
        Condition="Exists('..\..\lib\Ashes\linux-x64\libLLVM.so')"
        CopyToOutputDirectory="PreserveNewest"
        Link="runtimes\linux-x64\native\libLLVM.so" />
</ItemGroup>
```

---

## Step 2 – Create the P/Invoke interop layer

Create a new file `src/Ashes.Backend/Llvm/Interop/LlvmApi.cs` containing
`LibraryImport` declarations for every LLVM C API function used by the
compiler. Use `"libLLVM"` as the library name (matches the native file names).

### 2a – Opaque handle types

Define lightweight struct wrappers for the LLVM opaque pointer types:

```csharp
namespace Ashes.Backend.Llvm.Interop;

// Each wraps a raw IntPtr; provides type safety without allocations.
public readonly record struct LlvmContextHandle(nint Ptr);
public readonly record struct LlvmModuleHandle(nint Ptr);
public readonly record struct LlvmBuilderHandle(nint Ptr);
public readonly record struct LlvmTypeHandle(nint Ptr);
public readonly record struct LlvmValueHandle(nint Ptr);
public readonly record struct LlvmBasicBlockHandle(nint Ptr);
public readonly record struct LlvmTargetHandle(nint Ptr);
public readonly record struct LlvmTargetMachineHandle(nint Ptr);
public readonly record struct LlvmTargetDataHandle(nint Ptr);
```

### 2b – Enums

Map each LLVMSharp enum to a plain C# enum:

```csharp
public enum LlvmIntPredicate
{
    Eq  = 32, Ne  = 33,
    Ugt = 34, Uge = 35, Ult = 36, Ule = 37,
    Sgt = 38, Sge = 39, Slt = 40, Sle = 41,
}

public enum LlvmRealPredicate
{
    Oeq = 1, Ogt = 2, Oge = 3, Olt = 4, Ole = 5, One = 6,
    // ... add as needed
}

public enum LlvmCodeGenOptLevel { None = 0, Less = 1, Default = 2, Aggressive = 3 }
public enum LlvmRelocMode      { Default = 0, Static = 1, PIC = 2, DynamicNoPic = 3 }
public enum LlvmCodeModel       { Default = 0, JITDefault = 1, Tiny = 2, Small = 3, Kernel = 4, Medium = 5, Large = 6 }
public enum LlvmLinkage         { External = 0, Internal = 8 }
public enum LlvmCodeGenFileType { Assembly = 0, Object = 1 }
```

### 2c – LibraryImport declarations

Group by category. Every function maps 1:1 to the LLVM C API:

```csharp
using System.Runtime.InteropServices;

namespace Ashes.Backend.Llvm.Interop;

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
    public static unsafe partial LlvmTypeHandle FunctionType(
        LlvmTypeHandle returnType, LlvmTypeHandle* paramTypes, uint paramCount, int isVarArg);

    [LibraryImport(Lib, EntryPoint = "LLVMPointerTypeInContext")]
    public static partial LlvmTypeHandle PointerTypeInContext(LlvmContextHandle context, uint addressSpace);

    [LibraryImport(Lib, EntryPoint = "LLVMArrayType2")]
    public static partial LlvmTypeHandle ArrayType2(LlvmTypeHandle elementType, ulong elementCount);

    // ── Constants ───────────────────────────────────────────────────────
    [LibraryImport(Lib, EntryPoint = "LLVMConstInt")]
    public static partial LlvmValueHandle ConstInt(LlvmTypeHandle type, ulong value, int signExtend);

    [LibraryImport(Lib, EntryPoint = "LLVMConstNull")]
    public static partial LlvmValueHandle ConstNull(LlvmTypeHandle type);

    [LibraryImport(Lib, EntryPoint = "LLVMConstReal")]
    public static partial LlvmValueHandle ConstReal(LlvmTypeHandle type, double value);

    [LibraryImport(Lib, EntryPoint = "LLVMGetInlineAsm", StringMarshalling = StringMarshalling.Utf8)]
    public static partial LlvmValueHandle GetInlineAsm(
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
    public static unsafe partial LlvmValueHandle BuildGEP2(
        LlvmBuilderHandle b, LlvmTypeHandle type, LlvmValueHandle ptr,
        LlvmValueHandle* indices, uint numIndices, string name);

    // ── Call & select ───────────────────────────────────────────────────
    [LibraryImport(Lib, EntryPoint = "LLVMBuildCall2", StringMarshalling = StringMarshalling.Utf8)]
    public static unsafe partial LlvmValueHandle BuildCall2(
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
    public static partial int VerifyModule(LlvmModuleHandle module, int action, out nint outMessage);

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
}
```

> **Note:** The exact signatures above cover all ~60 distinct LLVM C API
> functions used by the compiler. Some may need `unsafe` contexts for pointer
> parameters — use `LlvmValueHandle*` or `Span<LlvmValueHandle>` as needed.

---

## Step 3 – Migrate each codegen file

Replace `using LLVMSharp.Interop;` with `using Ashes.Backend.Llvm.Interop;`
in each file, then update the call sites.

### Files to migrate (in suggested order)

| File | Lines | Complexity |
|------|-------|-----------|
| `LlvmTargetSetup.cs` | ~110 | Low — target init, context/module creation |
| `LlvmCodegenExpressions.cs` | ~146 | Low — comparisons, closures, control flow |
| `LlvmCodegenPlatform.cs` | ~404 | Medium — syscalls, Windows API stubs |
| `LlvmCodegenMemory.cs` | ~537 | Medium — heap, strings, UTF-8 |
| `LlvmCodegen.cs` | ~695 | Medium — core dispatch, module setup |
| `LlvmCodegenBuiltins.cs` | ~1957 | High — IO, File, HTTP, TCP, args |

### Mapping cheat sheet

The main change pattern is from LLVMSharp's method syntax to static function calls:

```csharp
// BEFORE (LLVMSharp)
var result = builder.BuildAdd(lhs, rhs, "sum");
var fn = module.AddFunction(fnType, "main");
var block = fn.AppendBasicBlock("entry");
builder.PositionAtEnd(block);

// AFTER (P/Invoke)
var result = LlvmApi.BuildAdd(builder, lhs, rhs, "sum");
var fn = LlvmApi.AddFunction(module, "main", fnType);
var block = LlvmApi.AppendBasicBlockInContext(context, fn, "entry");
LlvmApi.PositionBuilderAtEnd(builder, block);
```

### Type mapping

| LLVMSharp type | P/Invoke type |
|---------------|--------------|
| `LLVMContextRef` | `LlvmContextHandle` |
| `LLVMModuleRef` | `LlvmModuleHandle` |
| `LLVMBuilderRef` | `LlvmBuilderHandle` |
| `LLVMTypeRef` | `LlvmTypeHandle` |
| `LLVMValueRef` | `LlvmValueHandle` |
| `LLVMBasicBlockRef` | `LlvmBasicBlockHandle` |
| `LLVMTargetRef` | `LlvmTargetHandle` |
| `LLVMTargetMachineRef` | `LlvmTargetMachineHandle` |
| `LLVMTargetDataRef` | `LlvmTargetDataHandle` |

### Enum mapping

| LLVMSharp enum | P/Invoke enum |
|---------------|--------------|
| `LLVMIntPredicate.LLVMIntEQ` | `LlvmIntPredicate.Eq` |
| `LLVMIntPredicate.LLVMIntNE` | `LlvmIntPredicate.Ne` |
| `LLVMIntPredicate.LLVMIntSLT` | `LlvmIntPredicate.Slt` |
| `LLVMIntPredicate.LLVMIntSGT` | `LlvmIntPredicate.Sgt` |
| `LLVMCodeGenOptLevel.LLVMCodeGenLevelNone` | `LlvmCodeGenOptLevel.None` |
| `LLVMLinkage.LLVMExternalLinkage` | `LlvmLinkage.External` |
| `LLVMLinkage.LLVMInternalLinkage` | `LlvmLinkage.Internal` |
| ... | (same pattern for all) |

---

## Step 4 – Emit to memory buffer (replacing EmitToFile)

The current code uses `LLVMTargetMachineRef.EmitToFile()` to write a `.o`/`.obj`.
Replace with `TargetMachineEmitToMemoryBuffer` → copy bytes → dispose buffer:

```csharp
int err = LlvmApi.TargetMachineEmitToMemoryBuffer(
    machine, module, LlvmCodeGenFileType.Object, out nint errMsg, out nint memBuf);

if (err != 0)
{
    string msg = Marshal.PtrToStringAnsi(errMsg) ?? "unknown error";
    LlvmApi.DisposeMessage(errMsg);
    throw new InvalidOperationException($"LLVM emit failed: {msg}");
}

try
{
    nint start = LlvmApi.GetBufferStart(memBuf);
    nint size = LlvmApi.GetBufferSize(memBuf);
    byte[] objectCode = new byte[(int)size];
    Marshal.Copy(start, objectCode, 0, (int)size);
    return objectCode;
}
finally
{
    LlvmApi.DisposeMemoryBuffer(memBuf);
}
```

---

## Step 5 – Verify & test

1. Run `dotnet build Ashes.slnx` — should compile without warnings.
2. Run the test suites:
   ```bash
   src/Ashes.Tests/bin/Debug/net10.0/Ashes.Tests
   src/Ashes.Lsp.Tests/bin/Debug/net10.0/Ashes.Lsp.Tests
   ```
3. Run the examples:
   ```bash
   dotnet run --project src/Ashes.Cli -- run examples/hello.ash
   dotnet run --project src/Ashes.Cli -- run examples/fibonacci.ash
   ```
4. Cross-compile check: build on Linux, verify Windows target still emits valid PE.

---

## Step 6 – CI update

Update `.github/workflows/pull-request.yaml` to run the download script
before the build step:

```yaml
- name: Download LLVM native libraries
  run: ./scripts/download-llvm-native.sh
  shell: bash
```

Or on Windows runners:
```yaml
- name: Download LLVM native libraries
  run: .\scripts\download-llvm-native.ps1
  shell: pwsh
```

> **Tip:** Cache the downloaded archives using `actions/cache` keyed on the
> LLVM version to avoid re-downloading on every CI run.

---

## Version bumping

To update the LLVM version later, just pass the new version to the download script:

```bash
./scripts/download-llvm-native.sh 23.1.0
```

No code changes needed — the C API is stable across LLVM versions.
