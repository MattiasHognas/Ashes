using Ashes.Frontend;

namespace Ashes.Semantics;

public sealed partial class Lowering
{
    private static string StaticEvidenceParameterName(string kind, int ordinal) =>
        $"__{kind}_evidence_{ordinal}";

    private int ApplyStaticEvidenceArguments(int functionTemp, IEnumerable<int> evidenceTemps)
    {
        int applied = functionTemp;
        foreach (int evidenceTemp in evidenceTemps)
        {
            int next = NewTemp();
            Emit(new IrInst.CallClosure(next, applied, evidenceTemp));
            applied = next;
        }
        return applied;
    }

    private int NormalizeStaticEvidenceResult(int resultTemp, TypeRef resultType)
    {
        if (Prune(resultType) is not TypeRef.TFun)
        {
            return resultTemp;
        }

        int normalizedTemp = NewTemp();
        Emit(new IrInst.CopyOutClosure(
            normalizedTemp,
            resultTemp,
            RuntimeManaged: true,
            IrInst.CopyOutPurpose.RcNormalization));
        MarkRuntimeManagedTemp(normalizedTemp, type: Prune(resultType));
        return normalizedTemp;
    }

    private static Expr PrependStaticEvidenceParameters(
        Expr body,
        IReadOnlyList<string> parameterNames,
        Func<int, string, Expr, Expr> bindParameter)
    {
        for (int index = parameterNames.Count - 1; index >= 0; index--)
        {
            string parameterName = parameterNames[index];
            body = new Expr.Lambda(parameterName, bindParameter(index, parameterName, body));
        }
        return body;
    }
}
