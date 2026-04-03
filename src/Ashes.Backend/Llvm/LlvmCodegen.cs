using System.Text;
using Ashes.Backend.Backends;
using Ashes.Semantics;
using LLVMSharp.Interop;

namespace Ashes.Backend.Llvm;

internal static class LlvmCodegen
{
    public static byte[] Compile(IrProgram program, string targetId, BackendCompileOptions options)
    {
        using LlvmTargetContext target = LlvmTargetSetup.Create(targetId, options.OptimizationLevel);

        EmitModuleSkeleton(target, program);
        throw BuildNotYetSupportedException(program, targetId, options);
    }

    private static void EmitModuleSkeleton(LlvmTargetContext target, IrProgram program)
    {
        foreach (IrStringLiteral literal in program.StringLiterals)
        {
            byte[] utf8 = Encoding.UTF8.GetBytes(literal.Value);
            LLVMTypeRef stringType = LLVMTypeRef.CreateArray(LLVMTypeRef.Int8, (uint)utf8.Length);
            LLVMValueRef global = target.Module.AddGlobal(stringType, literal.Label);
            LLVMValueRef[] bytes = utf8
                .Select(static value => LLVMValueRef.CreateConstInt(LLVMTypeRef.Int8, value, false))
                .ToArray();
            global.Initializer = LLVMValueRef.CreateConstArray(LLVMTypeRef.Int8, bytes);
            global.Linkage = LLVMLinkage.LLVMPrivateLinkage;
            global.IsGlobalConstant = true;
        }

        AddPlaceholderFunction(target, program.EntryFunction, isEntry: true);
        foreach (IrFunction function in program.Functions)
        {
            AddPlaceholderFunction(target, function, isEntry: false);
        }
    }

    private static void AddPlaceholderFunction(LlvmTargetContext target, IrFunction function, bool isEntry)
    {
        LLVMTypeRef returnType = LLVMTypeRef.Int64;
        LLVMTypeRef[] parameterTypes = function.HasEnvAndArgParams
            ? [LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0), LLVMTypeRef.Int64]
            : [];
        LLVMTypeRef functionType = LLVMTypeRef.CreateFunction(returnType, parameterTypes);
        string label = isEntry ? "main" : function.Label;
        LLVMValueRef llvmFunction = target.Module.AddFunction(label, functionType);
        llvmFunction.Linkage = isEntry ? LLVMLinkage.LLVMExternalLinkage : LLVMLinkage.LLVMInternalLinkage;

        LLVMBasicBlockRef entryBlock = llvmFunction.AppendBasicBlock("entry");
        target.Builder.PositionAtEnd(entryBlock);
        target.Builder.BuildRet(LLVMValueRef.CreateConstInt(LLVMTypeRef.Int64, 0));
    }

    private static InvalidOperationException BuildNotYetSupportedException(IrProgram program, string targetId, BackendCompileOptions options)
    {
        StringBuilder builder = new();
        builder.Append("LLVM backend scaffolding is enabled but full executable emission is not implemented yet.");
        builder.Append(" Target='");
        builder.Append(targetId);
        builder.Append("', implementation='");
        builder.Append("llvm");
        builder.Append("', optimization='");
        builder.Append(options.OptimizationLevel);
        builder.Append("'. Entry function='");
        builder.Append(program.EntryFunction.Label);
        builder.Append("'.");
        return new InvalidOperationException(builder.ToString());
    }
}
