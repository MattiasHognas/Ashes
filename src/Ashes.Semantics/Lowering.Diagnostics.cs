using System.Diagnostics;
using Ashes.Frontend;

namespace Ashes.Semantics;

public sealed partial class Lowering
{
    private IDisposable PushDiagnosticContext(string context)
    {
        _diagnosticContext.Add(context);
        return new DiagnosticContextScope(_diagnosticContext);
    }

    private IDisposable PushDiagnosticCode(string code)
    {
        _diagnosticCodes.Push(code);
        return new DiagnosticCodeScope(_diagnosticCodes);
    }

    private string? CurrentDiagnosticCodeOrDefault(string? fallback = null)
    {
        return _diagnosticCodes.Count > 0 ? _diagnosticCodes.Peek() : fallback;
    }

    private void ReportDiagnostic(int pos, string message)
    {
        ReportDiagnostic(pos, message, null);
    }

    private void ReportDiagnostic(int pos, string message, string? code)
    {
        if (pos == 0 && _diagnosticSpans.Count > 0)
        {
            ReportDiagnostic(_diagnosticSpans.Peek(), message, code);
            return;
        }

        if (_diagnosticContext.Count == 0)
        {
            _diag.Error(pos, message, CurrentDiagnosticCodeOrDefault(code));
            return;
        }

        _diag.Error(pos, $"{message} Context: {string.Join(" -> ", _diagnosticContext.AsEnumerable().Reverse())}.", CurrentDiagnosticCodeOrDefault(code));
    }

    private void ReportDiagnostic(TextSpan span, string message)
    {
        ReportDiagnostic(span, message, null);
    }

    private void ReportDiagnostic(TextSpan span, string message, string? code)
    {
        if (_diagnosticContext.Count == 0)
        {
            _diag.Error(span, message, CurrentDiagnosticCodeOrDefault(code));
            return;
        }

        _diag.Error(span, $"{message} Context: {string.Join(" -> ", _diagnosticContext.AsEnumerable().Reverse())}.", CurrentDiagnosticCodeOrDefault(code));
    }

    private IDisposable PushDiagnosticSpan(Expr expr)
    {
        return PushDiagnosticSpan(GetSpan(expr));
    }

    private IDisposable PushDiagnosticSpan(Pattern pattern)
    {
        return PushDiagnosticSpan(GetSpan(pattern));
    }

    private IDisposable PushDiagnosticSpan(TextSpan span)
    {
        _diagnosticSpans.Push(span);
        return new DiagnosticSpanScope(_diagnosticSpans);
    }

    private static TextSpan CombineSpans(Expr left, Expr right)
    {
        var leftSpan = GetSpan(left);
        var rightSpan = GetSpan(right);
        return TextSpan.FromBounds(leftSpan.Start, Math.Max(leftSpan.End, rightSpan.End));
    }

    private static TextSpan GetSpan(Expr expr)
    {
        var span = AstSpans.GetOrDefault(expr);
        return span.Length == 0 ? TextSpan.FromBounds(span.Start, span.Start + 1) : span;
    }

    private static TextSpan GetSpan(Pattern pattern)
    {
        var span = AstSpans.GetOrDefault(pattern);
        return span.Length == 0 ? TextSpan.FromBounds(span.Start, span.Start + 1) : span;
    }

    private static TextSpan GetSpan(TypeDecl typeDecl)
    {
        var span = AstSpans.GetOrDefault(typeDecl);
        return span.Length == 0 ? TextSpan.FromBounds(span.Start, span.Start + 1) : span;
    }

    private static TextSpan GetSpan(TypeConstructor typeConstructor)
    {
        var span = AstSpans.GetOrDefault(typeConstructor);
        return span.Length == 0 ? TextSpan.FromBounds(span.Start, span.Start + 1) : span;
    }

    private (int, TypeRef) ReportArityMismatch(Expr callee, int expectedArgs, int providedArgs)
    {
        var calleeName = TryGetCalleeDisplayName(callee);
        if (calleeName is not null)
        {
            ReportDiagnostic(GetSpan(callee), $"Call to '{calleeName}' expects {expectedArgs} argument(s) but got {providedArgs}.");
        }
        else
        {
            ReportDiagnostic(GetSpan(callee), $"Call expects {expectedArgs} argument(s) but got {providedArgs}.");
        }

        return ReturnNeverWithDummyTemp();
    }

    private (int, TypeRef) ReportNonFunctionCall(Expr callee, TypeRef actualType, int providedArgs)
    {
        var calleeName = TryGetCalleeDisplayName(callee);
        if (calleeName is not null)
        {
            ReportDiagnostic(GetSpan(callee), $"Attempted to call '{calleeName}' with {providedArgs} argument(s), but its type is {Pretty(actualType)}, not a function.");
        }
        else
        {
            ReportDiagnostic(GetSpan(callee), $"Attempted to call non-function type {Pretty(actualType)}.");
        }

        return ReturnNeverWithDummyTemp();
    }
}
