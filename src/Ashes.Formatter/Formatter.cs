using Ashes.Frontend;
using System.Globalization;
using System.Text;

namespace Ashes.Formatter;

public static class Formatter
{
    // Precedence: larger = binds tighter
    private const int PrecLetIfLambda = 1;
    private const int PrecPipe = 3;
    private const int PrecCmp = 4;   // ==, !=, >=, <=  (lower than + and ::)
    private const int PrecCons = 5;
    private const int PrecAdd = 6;
    private const int PrecMul = 7;
    private const int PrecUnary = 8;
    private const int PrecCall = 9;

    public static string Format(Program program)
    {
        return Format(program, preferPipelines: false, options: null);
    }

    public static string Format(Program program, FormattingOptions options)
    {
        return Format(program, preferPipelines: false, options);
    }

    public static string Format(Program program, bool preferPipelines, FormattingOptions? options = null)
    {
        var formattingOptions = (options ?? new FormattingOptions()).Normalize();
        var sb = new StringBuilder();
        foreach (var decl in program.TypeDecls)
        {
            WriteTypeDecl(sb, decl, formattingOptions);
        }
        WriteExpr(sb, program.Body, indent: 0, parentPrec: 0, preferPipelines, formattingOptions);
        if (sb.Length == 0 || sb[^1] != '\n')
        {
            sb.Append('\n');
        }
        return formattingOptions.NewLine == "\n" ? sb.ToString() : sb.ToString().Replace("\n", formattingOptions.NewLine, StringComparison.Ordinal);
    }

    public static string Format(Expr expr)
    {
        return Format(expr, preferPipelines: false, options: null);
    }

    public static string Format(Expr expr, FormattingOptions options)
    {
        return Format(expr, preferPipelines: false, options);
    }

    public static string Format(Expr expr, bool preferPipelines, FormattingOptions? options = null)
    {
        var formattingOptions = (options ?? new FormattingOptions()).Normalize();
        var sb = new StringBuilder();
        WriteExpr(sb, expr, indent: 0, parentPrec: 0, preferPipelines, formattingOptions);
        if (sb.Length == 0 || sb[^1] != '\n')
        {
            sb.Append('\n');
        }

        return formattingOptions.NewLine == "\n" ? sb.ToString() : sb.ToString().Replace("\n", formattingOptions.NewLine, StringComparison.Ordinal);
    }

    private static void WriteTypeDecl(StringBuilder sb, TypeDecl decl, FormattingOptions options)
    {
        sb.Append("type ");
        sb.Append(decl.Name);
        if (decl.TypeParameters.Count > 0)
        {
            sb.Append('(');
            sb.Append(string.Join(", ", decl.TypeParameters.Select(p => p.Name)));
            sb.Append(')');
        }
        sb.Append(" =\n");
        foreach (var ctor in decl.Constructors)
        {
            WriteIndent(sb, options.IndentSize, options);
            sb.Append("| ");
            sb.Append(ctor.Name);
            if (ctor.Parameters.Count > 0)
            {
                sb.Append('(');
                sb.Append(string.Join(", ", ctor.Parameters));
                sb.Append(')');
            }
            sb.Append('\n');
        }
        sb.Append('\n');
    }

    private static bool EndsWithNewLine(StringBuilder sb, string newLine)
    {
        if (newLine == "\n")
        {
            return sb.Length > 0 && sb[^1] == '\n';
        }

        return sb.Length >= 2 && sb[^2] == '\r' && sb[^1] == '\n';
    }

    private static void WriteIndent(StringBuilder sb, int indent, FormattingOptions options)
    {
        if (!options.UseTabs)
        {
            sb.Append(' ', indent);
            return;
        }

        var tabCount = indent / options.IndentSize;
        var spaceCount = indent % options.IndentSize;
        if (tabCount > 0)
        {
            sb.Append('\t', tabCount);
        }

        if (spaceCount > 0)
        {
            sb.Append(' ', spaceCount);
        }
    }

    private static bool IsSingleLine(Expr e, bool preferPipelines)
    {
        return e switch
        {
            Expr.IntLit or Expr.FloatLit or Expr.StrLit or Expr.BoolLit or Expr.Var or Expr.QualifiedVar => true,
            Expr.Add a => IsSingleLine(a.Left, preferPipelines) && IsSingleLine(a.Right, preferPipelines),
            Expr.Subtract sub => IsSingleLine(sub.Left, preferPipelines) && IsSingleLine(sub.Right, preferPipelines),
            Expr.Multiply mul => IsSingleLine(mul.Left, preferPipelines) && IsSingleLine(mul.Right, preferPipelines),
            Expr.Divide div => IsSingleLine(div.Left, preferPipelines) && IsSingleLine(div.Right, preferPipelines),
            Expr.GreaterOrEqual ge => IsSingleLine(ge.Left, preferPipelines) && IsSingleLine(ge.Right, preferPipelines),
            Expr.LessOrEqual le => IsSingleLine(le.Left, preferPipelines) && IsSingleLine(le.Right, preferPipelines),
            Expr.Equal eq => IsSingleLine(eq.Left, preferPipelines) && IsSingleLine(eq.Right, preferPipelines),
            Expr.NotEqual ne => IsSingleLine(ne.Left, preferPipelines) && IsSingleLine(ne.Right, preferPipelines),
            Expr.ResultPipe pipe => (!preferPipelines || !TryCollectPipeline(pipe, out _, out _)) && IsSingleLine(pipe.Left, preferPipelines) && IsSingleLine(pipe.Right, preferPipelines),
            Expr.ResultMapErrorPipe pipe => (!preferPipelines || !TryCollectPipeline(pipe, out _, out _)) && IsSingleLine(pipe.Left, preferPipelines) && IsSingleLine(pipe.Right, preferPipelines),
            Expr.TupleLit tuple => tuple.Elements.All(x => IsSingleLine(x, preferPipelines)),
            Expr.ListLit list => list.Elements.All(x => IsSingleLine(x, preferPipelines)),
            Expr.Cons cons => IsSingleLine(cons.Head, preferPipelines) && IsSingleLine(cons.Tail, preferPipelines),
            Expr.Call c => (!preferPipelines || !TryCollectPipeline(c, out _, out _)) && IsSingleLine(c.Func, preferPipelines) && IsSingleLine(c.Arg, preferPipelines),
            Expr.Await awaitExpr => IsSingleLine(awaitExpr.Task, preferPipelines),
            _ => false
        };
    }

    private static void WriteExpr(StringBuilder sb, Expr e, int indent, int parentPrec, bool preferPipelines, FormattingOptions options)
    {
        switch (e)
        {
            case Expr.Let l:
                WriteLet(sb, l, indent, parentPrec, preferPipelines, options);
                return;

            case Expr.LetResult l:
                WriteLetResult(sb, l, indent, parentPrec, preferPipelines, options);
                return;

            case Expr.LetRec l:
                WriteLetRec(sb, l, indent, parentPrec, preferPipelines, options);
                return;

            case Expr.If i:
                WriteIf(sb, i, indent, parentPrec, preferPipelines, options);
                return;

            case Expr.Lambda lam:
                WriteLambda(sb, lam, indent, parentPrec, preferPipelines, options);
                return;

            case Expr.Match match:
                WriteMatch(sb, match, indent, parentPrec, preferPipelines, options);
                return;

            case Expr.Async asyncExpr:
                WriteAsync(sb, asyncExpr, indent, parentPrec, preferPipelines, options);
                return;

            default:
                WriteExprInline(sb, e, indent, parentPrec, preferPipelines, options);
                return;
        }
    }

    private static void WriteLet(StringBuilder sb, Expr.Let l, int indent, int parentPrec, bool preferPipelines, FormattingOptions options)
    {
        // Multiline canonical form:
        // let x = <value>
        // in <body>
        var needsParens = parentPrec > PrecLetIfLambda;
        if (needsParens)
        {
            sb.Append('(');
        }

        sb.Append("let ");
        sb.Append(l.Name);

        // ML-style sugar: let f x y = <value>
        var value = l.Value;
        if (l.SugarParams.Count > 0)
        {
            foreach (var p in l.SugarParams)
            {
                sb.Append(' ');
                sb.Append(p);
                // Unwrap the corresponding lambda layer
                if (value is Expr.Lambda lam)
                {
                    value = lam.Body;
                }
            }
        }

        sb.Append(" = ");

        if (IsSingleLine(value, preferPipelines))
        {
            WriteExprInline(sb, value, indent, 0, preferPipelines, options);
            sb.Append('\n');
        }
        else
        {
            sb.Append('\n');
            WriteIndent(sb, indent + options.IndentSize, options);
            WriteExpr(sb, value, indent + options.IndentSize, 0, preferPipelines, options);
            if (!EndsWithNewLine(sb, "\n"))
            {
                sb.Append('\n');
            }
        }

        WriteIndent(sb, indent, options);
        sb.Append("in ");
        // Body can be multiline; if so, put it on next line indented
        if (IsSingleLine(l.Body, preferPipelines))
        {
            WriteExprInline(sb, l.Body, indent, 0, preferPipelines, options);
        }
        else
        {
            sb.Append('\n');
            WriteIndent(sb, indent + options.IndentSize, options);
            WriteExpr(sb, l.Body, indent + options.IndentSize, 0, preferPipelines, options);
        }

        if (needsParens)
        {
            sb.Append(')');
        }
    }

    private static void WriteLetRec(StringBuilder sb, Expr.LetRec l, int indent, int parentPrec, bool preferPipelines, FormattingOptions options)
    {
        var needsParens = parentPrec > PrecLetIfLambda;
        if (needsParens)
        {
            sb.Append('(');
        }

        sb.Append("let rec ");
        sb.Append(l.Name);

        // ML-style sugar: let rec f x y = <value>
        var value = l.Value;
        if (l.SugarParams.Count > 0)
        {
            foreach (var p in l.SugarParams)
            {
                sb.Append(' ');
                sb.Append(p);
                // Unwrap the corresponding lambda layer
                if (value is Expr.Lambda lam)
                {
                    value = lam.Body;
                }
            }
        }

        sb.Append(" = ");

        if (IsSingleLine(value, preferPipelines))
        {
            WriteExprInline(sb, value, indent, 0, preferPipelines, options);
            sb.Append('\n');
        }
        else
        {
            sb.Append('\n');
            WriteIndent(sb, indent + options.IndentSize, options);
            WriteExpr(sb, value, indent + options.IndentSize, 0, preferPipelines, options);
            if (!EndsWithNewLine(sb, "\n"))
            {
                sb.Append('\n');
            }
        }

        WriteIndent(sb, indent, options);
        sb.Append("in ");
        if (IsSingleLine(l.Body, preferPipelines))
        {
            WriteExprInline(sb, l.Body, indent, 0, preferPipelines, options);
        }
        else
        {
            sb.Append('\n');
            WriteIndent(sb, indent + options.IndentSize, options);
            WriteExpr(sb, l.Body, indent + options.IndentSize, 0, preferPipelines, options);
        }

        if (needsParens)
        {
            sb.Append(')');
        }
    }

    private static void WriteLetResult(StringBuilder sb, Expr.LetResult l, int indent, int parentPrec, bool preferPipelines, FormattingOptions options)
    {
        var needsParens = parentPrec > PrecLetIfLambda;
        if (needsParens)
        {
            sb.Append('(');
        }

        sb.Append("let? ");
        sb.Append(l.Name);
        sb.Append(" = ");

        if (IsSingleLine(l.Value, preferPipelines))
        {
            WriteExprInline(sb, l.Value, indent, 0, preferPipelines, options);
            sb.Append('\n');
        }
        else
        {
            sb.Append('\n');
            WriteIndent(sb, indent + options.IndentSize, options);
            WriteExpr(sb, l.Value, indent + options.IndentSize, 0, preferPipelines, options);
            if (!EndsWithNewLine(sb, "\n"))
            {
                sb.Append('\n');
            }
        }

        WriteIndent(sb, indent, options);
        sb.Append("in ");
        if (IsSingleLine(l.Body, preferPipelines))
        {
            WriteExprInline(sb, l.Body, indent, 0, preferPipelines, options);
        }
        else
        {
            sb.Append('\n');
            WriteIndent(sb, indent + options.IndentSize, options);
            WriteExpr(sb, l.Body, indent + options.IndentSize, 0, preferPipelines, options);
        }

        if (needsParens)
        {
            sb.Append(')');
        }
    }

    private static void WriteAsync(StringBuilder sb, Expr.Async asyncExpr, int indent, int parentPrec, bool preferPipelines, FormattingOptions options)
    {
        var needsParens = parentPrec > PrecLetIfLambda;
        if (needsParens)
        {
            sb.Append('(');
        }

        sb.Append("async\n");
        WriteIndent(sb, indent + options.IndentSize, options);
        WriteExpr(sb, asyncExpr.Body, indent + options.IndentSize, 0, preferPipelines, options);

        if (needsParens)
        {
            sb.Append(')');
        }
    }

    private static void WriteIf(StringBuilder sb, Expr.If i, int indent, int parentPrec, bool preferPipelines, FormattingOptions options)
    {
        var needsParens = parentPrec > PrecLetIfLambda;
        if (needsParens)
        {
            sb.Append('(');
        }

        sb.Append("if ");
        WriteExprInline(sb, i.Cond, indent, 0, preferPipelines, options);
        sb.Append('\n');

        WriteIndent(sb, indent, options);
        sb.Append("then ");
        if (IsSingleLine(i.Then, preferPipelines))
        {
            WriteExprInline(sb, i.Then, indent, 0, preferPipelines, options);
            sb.Append('\n');
        }
        else
        {
            sb.Append('\n');
            WriteIndent(sb, indent + options.IndentSize, options);
            WriteExpr(sb, i.Then, indent + options.IndentSize, 0, preferPipelines, options);
            if (!EndsWithNewLine(sb, "\n"))
            {
                sb.Append('\n');
            }
        }

        WriteIndent(sb, indent, options);
        sb.Append("else ");
        if (IsSingleLine(i.Else, preferPipelines))
        {
            WriteExprInline(sb, i.Else, indent, 0, preferPipelines, options);
        }
        else
        {
            sb.Append('\n');
            WriteIndent(sb, indent + options.IndentSize, options);
            WriteExpr(sb, i.Else, indent + options.IndentSize, 0, preferPipelines, options);
        }

        if (needsParens)
        {
            sb.Append(')');
        }
    }

    private static void WriteLambda(StringBuilder sb, Expr.Lambda lam, int indent, int parentPrec, bool preferPipelines, FormattingOptions options)
    {
        var needsParens = parentPrec > PrecLetIfLambda;
        if (needsParens)
        {
            sb.Append('(');
        }

        sb.Append("fun (");
        sb.Append(lam.ParamName);
        sb.Append(") -> ");

        if (IsSingleLine(lam.Body, preferPipelines))
        {
            WriteExprInline(sb, lam.Body, indent, 0, preferPipelines, options);
        }
        else
        {
            sb.Append('\n');
            WriteIndent(sb, indent + options.IndentSize, options);
            WriteExpr(sb, lam.Body, indent + options.IndentSize, 0, preferPipelines, options);
        }

        if (needsParens)
        {
            sb.Append(')');
        }
    }

    private static void WriteMatch(StringBuilder sb, Expr.Match match, int indent, int parentPrec, bool preferPipelines, FormattingOptions options)
    {
        var needsParens = parentPrec > PrecLetIfLambda;
        if (needsParens)
        {
            sb.Append('(');
        }

        sb.Append("match ");
        WriteExprInline(sb, match.Value, indent, 0, preferPipelines, options);
        sb.Append(" with\n");

        foreach (var matchCase in match.Cases)
        {
            WriteIndent(sb, indent + options.IndentSize, options);
            sb.Append("| ");
            WritePattern(sb, matchCase.Pattern);
            sb.Append(" -> ");
            if (IsSingleLine(matchCase.Body, preferPipelines))
            {
                WriteExprInline(sb, matchCase.Body, indent + options.IndentSize, 0, preferPipelines, options);
                sb.Append('\n');
            }
            else
            {
                sb.Append('\n');
                WriteIndent(sb, indent + options.IndentSize * 2, options);
                WriteExpr(sb, matchCase.Body, indent + options.IndentSize * 2, 0, preferPipelines, options);
                if (!EndsWithNewLine(sb, "\n"))
                {
                    sb.Append('\n');
                }
            }
        }

        if (sb.Length > 0 && sb[^1] == '\n')
        {
            sb.Length--;
        }

        if (needsParens)
        {
            sb.Append(')');
        }
    }

    private static void WritePattern(StringBuilder sb, Pattern pattern)
    {
        switch (pattern)
        {
            case Pattern.EmptyList:
                sb.Append("[]");
                return;
            case Pattern.Var patVar:
                sb.Append(patVar.Name);
                return;
            case Pattern.Wildcard:
                sb.Append('_');
                return;
            case Pattern.Cons cons:
                WritePattern(sb, cons.Head);
                sb.Append(" :: ");
                WritePattern(sb, cons.Tail);
                return;
            case Pattern.Tuple tuple:
                sb.Append('(');
                for (int i = 0; i < tuple.Elements.Count; i++)
                {
                    if (i > 0)
                    {
                        sb.Append(", ");
                    }
                    WritePattern(sb, tuple.Elements[i]);
                }
                sb.Append(')');
                return;
            case Pattern.Constructor ctor:
                sb.Append(ctor.Name);
                if (ctor.Patterns.Count > 0)
                {
                    sb.Append('(');
                    for (int i = 0; i < ctor.Patterns.Count; i++)
                    {
                        if (i > 0)
                        {
                            sb.Append(", ");
                        }
                        WritePattern(sb, ctor.Patterns[i]);
                    }
                    sb.Append(')');
                }
                return;
            case Pattern.IntLit intLit:
                sb.Append(intLit.Value);
                return;
            case Pattern.StrLit strLit:
                sb.Append('"');
                sb.Append(EscapeString(strLit.Value));
                sb.Append('"');
                return;
            case Pattern.BoolLit boolLit:
                sb.Append(boolLit.Value ? "true" : "false");
                return;
        }
    }

    private static void WriteExprInline(StringBuilder sb, Expr e, int indent, int parentPrec, bool preferPipelines, FormattingOptions options)
    {
        switch (e)
        {
            case Expr.IntLit i:
                sb.Append(i.Value);
                return;

            case Expr.FloatLit f:
                sb.Append(FormatFloatLiteral(f));
                return;

            case Expr.StrLit s:
                sb.Append('"');
                sb.Append(EscapeString(s.Value));
                sb.Append('"');
                return;

            case Expr.BoolLit b:
                sb.Append(b.Value ? "true" : "false");
                return;

            case Expr.Var v:
                sb.Append(v.Name);
                return;

            case Expr.QualifiedVar qv:
                sb.Append(qv.Module);
                sb.Append('.');
                sb.Append(qv.Name);
                return;

            case Expr.TupleLit tuple:
                sb.Append('(');
                for (int i = 0; i < tuple.Elements.Count; i++)
                {
                    if (i > 0)
                    {
                        sb.Append(", ");
                    }
                    WriteExprInline(sb, tuple.Elements[i], indent, 0, preferPipelines, options);
                }
                sb.Append(')');
                return;

            case Expr.ListLit list:
                sb.Append('[');
                for (int i = 0; i < list.Elements.Count; i++)
                {
                    if (i > 0)
                    {
                        sb.Append(", ");
                    }
                    WriteExprInline(sb, list.Elements[i], indent, 0, preferPipelines, options);
                }
                sb.Append(']');
                return;

            case Expr.Cons cons:
                {
                    var needsParens = parentPrec > PrecCons;
                    if (needsParens)
                    {
                        sb.Append('(');
                    }
                    WriteExprInline(sb, cons.Head, indent, PrecCons + 1, preferPipelines, options);
                    sb.Append(" :: ");
                    WriteExprInline(sb, cons.Tail, indent, PrecCons, preferPipelines, options);
                    if (needsParens)
                    {
                        sb.Append(')');
                    }
                    return;
                }

            case Expr.Add a:
                {
                    var needsParens = parentPrec > PrecAdd;
                    if (needsParens)
                    {
                        sb.Append('(');
                    }

                    WriteExprInline(sb, a.Left, indent, PrecAdd, preferPipelines, options);
                    sb.Append(" + ");
                    WriteExprInline(sb, a.Right, indent, PrecAdd, preferPipelines, options);
                    if (needsParens)
                    {
                        sb.Append(')');
                    }

                    return;
                }

            case Expr.Subtract sub:
                {
                    if (sub.Left is Expr.IntLit { Value: 0 })
                    {
                        var unaryNeedsParens = parentPrec > PrecUnary;
                        if (unaryNeedsParens)
                        {
                            sb.Append('(');
                        }

                        sb.Append('-');
                        WriteExprInline(sb, sub.Right, indent, PrecUnary, preferPipelines, options);

                        if (unaryNeedsParens)
                        {
                            sb.Append(')');
                        }

                        return;
                    }

                    var needsParens = parentPrec > PrecAdd;
                    if (needsParens)
                    {
                        sb.Append('(');
                    }

                    WriteExprInline(sb, sub.Left, indent, PrecAdd, preferPipelines, options);
                    sb.Append(" - ");
                    WriteExprInline(sb, sub.Right, indent, PrecAdd + 1, preferPipelines, options);
                    if (needsParens)
                    {
                        sb.Append(')');
                    }

                    return;
                }

            case Expr.Multiply mul:
                {
                    var needsParens = parentPrec > PrecMul;
                    if (needsParens)
                    {
                        sb.Append('(');
                    }

                    WriteExprInline(sb, mul.Left, indent, PrecMul, preferPipelines, options);
                    sb.Append(" * ");
                    WriteExprInline(sb, mul.Right, indent, PrecMul, preferPipelines, options);
                    if (needsParens)
                    {
                        sb.Append(')');
                    }

                    return;
                }

            case Expr.Divide div:
                {
                    var needsParens = parentPrec > PrecMul;
                    if (needsParens)
                    {
                        sb.Append('(');
                    }

                    WriteExprInline(sb, div.Left, indent, PrecMul, preferPipelines, options);
                    sb.Append(" / ");
                    WriteExprInline(sb, div.Right, indent, PrecMul + 1, preferPipelines, options);
                    if (needsParens)
                    {
                        sb.Append(')');
                    }

                    return;
                }

            case Expr.GreaterOrEqual ge:
                {
                    var needsParens = parentPrec > PrecCmp;
                    if (needsParens)
                    {
                        sb.Append('(');
                    }

                    WriteExprInline(sb, ge.Left, indent, PrecCmp, preferPipelines, options);
                    sb.Append(" >= ");
                    WriteExprInline(sb, ge.Right, indent, PrecCmp, preferPipelines, options);
                    if (needsParens)
                    {
                        sb.Append(')');
                    }

                    return;
                }

            case Expr.LessOrEqual le:
                {
                    var needsParens = parentPrec > PrecCmp;
                    if (needsParens)
                    {
                        sb.Append('(');
                    }

                    WriteExprInline(sb, le.Left, indent, PrecCmp, preferPipelines, options);
                    sb.Append(" <= ");
                    WriteExprInline(sb, le.Right, indent, PrecCmp, preferPipelines, options);
                    if (needsParens)
                    {
                        sb.Append(')');
                    }

                    return;
                }

            case Expr.Equal eq:
                {
                    var needsParens = parentPrec > PrecCmp;
                    if (needsParens)
                    {
                        sb.Append('(');
                    }

                    WriteExprInline(sb, eq.Left, indent, PrecCmp, preferPipelines, options);
                    sb.Append(" == ");
                    WriteExprInline(sb, eq.Right, indent, PrecCmp, preferPipelines, options);
                    if (needsParens)
                    {
                        sb.Append(')');
                    }

                    return;
                }

            case Expr.NotEqual ne:
                {
                    var needsParens = parentPrec > PrecCmp;
                    if (needsParens)
                    {
                        sb.Append('(');
                    }

                    WriteExprInline(sb, ne.Left, indent, PrecCmp, preferPipelines, options);
                    sb.Append(" != ");
                    WriteExprInline(sb, ne.Right, indent, PrecCmp, preferPipelines, options);
                    if (needsParens)
                    {
                        sb.Append(')');
                    }

                    return;
                }

            case Expr.ResultPipe pipe:
                {
                    if (preferPipelines && parentPrec == 0 && TryWritePipeline(sb, pipe, indent, parentPrec, preferPipelines, options))
                    {
                        return;
                    }

                    var needsParens = parentPrec > PrecPipe;
                    if (needsParens)
                    {
                        sb.Append('(');
                    }

                    WriteExprInline(sb, pipe.Left, indent, PrecPipe, preferPipelines, options);
                    sb.Append(" |?> ");
                    WriteExprInline(sb, pipe.Right, indent, PrecPipe + 1, preferPipelines, options);

                    if (needsParens)
                    {
                        sb.Append(')');
                    }

                    return;
                }

            case Expr.ResultMapErrorPipe pipe:
                {
                    if (preferPipelines && parentPrec == 0 && TryWritePipeline(sb, pipe, indent, parentPrec, preferPipelines, options))
                    {
                        return;
                    }

                    var needsParens = parentPrec > PrecPipe;
                    if (needsParens)
                    {
                        sb.Append('(');
                    }

                    WriteExprInline(sb, pipe.Left, indent, PrecPipe, preferPipelines, options);
                    sb.Append(" |!> ");
                    WriteExprInline(sb, pipe.Right, indent, PrecPipe + 1, preferPipelines, options);

                    if (needsParens)
                    {
                        sb.Append(')');
                    }

                    return;
                }

            case Expr.Call c:
                {
                    if (preferPipelines && parentPrec == 0 && TryWritePipeline(sb, c, indent, parentPrec, preferPipelines, options))
                    {
                        return;
                    }

                    var needsParens = parentPrec > PrecCall;
                    if (needsParens)
                    {
                        sb.Append('(');
                    }

                    // Function position: if it's a lambda/let/if/add, parenthesize
                    var funcNeedsParens = c.Func is Expr.Lambda or Expr.Let or Expr.LetResult or Expr.LetRec or Expr.If or Expr.Add or Expr.Subtract or Expr.Multiply or Expr.Divide or Expr.GreaterOrEqual or Expr.LessOrEqual or Expr.Equal or Expr.NotEqual or Expr.Async or Expr.Await;
                    if (funcNeedsParens)
                    {
                        sb.Append('(');
                    }

                    WriteExprInline(sb, c.Func, indent, PrecCall, preferPipelines, options);
                    if (funcNeedsParens)
                    {
                        sb.Append(')');
                    }

                    if (c.IsWhitespaceApplication)
                    {
                        sb.Append(' ');
                        WriteExprInline(sb, c.Arg, indent, PrecCall + 1, preferPipelines, options);
                    }
                    else
                    {
                        sb.Append('(');
                        WriteExprInline(sb, c.Arg, indent, 0, preferPipelines, options);
                        sb.Append(')');
                    }

                    if (needsParens)
                    {
                        sb.Append(')');
                    }

                    return;
                }

            case Expr.Match match:
                WriteMatch(sb, match, indent, parentPrec, preferPipelines, options);
                return;

            case Expr.Await awaitExpr:
                {
                    sb.Append("await ");
                    WriteExprInline(sb, awaitExpr.Task, indent, PrecCall, preferPipelines, options);
                    return;
                }

            // Fallback to multiline writer (rare)
            default:
                WriteExpr(sb, e, indent, parentPrec, preferPipelines, options);
                return;
        }
    }

    private static string FormatFloatLiteral(Expr.FloatLit literal)
    {
        if (!string.IsNullOrEmpty(literal.Text))
        {
            return literal.Text;
        }

        var text = literal.Value.ToString("G17", CultureInfo.InvariantCulture);
        if (!text.Contains('.', StringComparison.Ordinal)
            && !text.Contains('e', StringComparison.OrdinalIgnoreCase))
        {
            text += ".0";
        }

        return text;
    }

    private sealed record PipelineStage(string OperatorText, Expr Func);

    private static bool TryWritePipeline(StringBuilder sb, Expr expr, int indent, int parentPrec, bool preferPipelines, FormattingOptions options)
    {
        if (!TryCollectPipeline(expr, out var value, out var funcs))
        {
            return false;
        }

        var needsParens = parentPrec > PrecPipe;
        if (needsParens)
        {
            sb.Append('(');
        }

        WriteExprInline(sb, value, indent, PrecPipe + 1, preferPipelines, options);
        foreach (var func in funcs)
        {
            sb.Append('\n');
            WriteIndent(sb, indent, options);
            sb.Append(func.OperatorText);
            sb.Append(' ');
            WriteExprInline(sb, func.Func, indent, PrecPipe + 1, preferPipelines, options);
        }

        if (needsParens)
        {
            sb.Append(')');
        }

        return true;
    }

    private static bool TryCollectPipeline(Expr expr, out Expr value, out List<PipelineStage> funcs)
    {
        funcs = [];
        var current = expr;
        while (true)
        {
            switch (current)
            {
                case Expr.Call c:
                    if (funcs.Count > 0 && c.Func is Expr.Var { Name: [>= 'A' and <= 'Z', ..] })
                    {
                        value = current;
                        funcs.Reverse();
                        return funcs.Count > 1;
                    }

                    if (!CanBePipelineFunction(c.Func))
                    {
                        value = expr;
                        funcs = [];
                        return false;
                    }

                    funcs.Add(new PipelineStage("|>", c.Func));
                    current = c.Arg;
                    continue;

                case Expr.ResultPipe pipe:
                    if (!CanBePipelineFunction(pipe.Right))
                    {
                        value = expr;
                        funcs = [];
                        return false;
                    }

                    funcs.Add(new PipelineStage("|?>", pipe.Right));
                    current = pipe.Left;
                    continue;

                case Expr.ResultMapErrorPipe pipe:
                    if (!CanBePipelineFunction(pipe.Right))
                    {
                        value = expr;
                        funcs = [];
                        return false;
                    }

                    funcs.Add(new PipelineStage("|!>", pipe.Right));
                    current = pipe.Left;
                    continue;
            }

            break;
        }

        funcs.Reverse();
        value = current;
        return funcs.Count > 1;
    }

    private static bool CanBePipelineFunction(Expr expr)
    {
        return expr is not (Expr.Let or Expr.LetResult or Expr.LetRec or Expr.If or Expr.Match);
    }

    private static string EscapeString(string s)
    {
        return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t");
    }
}
