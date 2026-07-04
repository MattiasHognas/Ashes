using Ashes.Frontend;
using System.Globalization;
using System.Text;

namespace Ashes.Formatter;

public static class Formatter
{
    // Precedence: larger = binds tighter
    private const int PrecLetIfLambda = 1;
    private const int PrecWith = 2;  // record update `e with f = e` (looser than pipe)
    private const int PrecPipe = 3;
    private const int PrecCmp = 4;   // ==, !=, >=, <=  (lower than bitwise, + and ::)
    private const int PrecBitOr = 5;
    private const int PrecBitXor = 6;
    private const int PrecBitAnd = 7;
    private const int PrecCons = 8;
    private const int PrecShift = 9;
    private const int PrecAdd = 10;
    private const int PrecMul = 11;
    private const int PrecUnary = 12;
    private const int PrecCall = 13;

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

        // A file is a flat sequence of top-level items in source order, followed by an optional
        // trailing expression. Exactly one blank line separates adjacent items, and one blank line
        // separates the last item from the trailing expression. Each writer below ends the item with
        // a single newline; the blank line is the extra '\n' inserted before each subsequent block.
        // Consecutive `extern` declarations are the one exception: they stay grouped as a block with
        // no blank line between them (matching established formatting of FFI declaration blocks).
        TopLevelItem? previous = null;
        foreach (var item in program.Items)
        {
            if (previous is not null && !(previous is TopLevelItem.Extern && item is TopLevelItem.Extern))
            {
                sb.Append('\n');
            }
            WriteTopLevelItem(sb, item, preferPipelines, formattingOptions);
            previous = item;
        }

        if (program.Body is not null)
        {
            if (previous is not null)
            {
                sb.Append('\n');
            }
            WriteExpr(sb, program.Body, indent: 0, parentPrec: 0, preferPipelines, formattingOptions);
        }

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

        if (decl.IsRecord && decl.Constructors.Count == 1)
        {
            // Brace-free record syntax: one field per `|` line, mirroring ADT constructors.
            //     type Point =
            //         | x: Int
            //         | y: Int
            var ctor = decl.Constructors[0];
            sb.Append(" =\n");
            for (int i = 0; i < ctor.FieldNames.Count; i++)
            {
                WriteIndent(sb, options.IndentSize, options);
                sb.Append("| ");
                sb.Append(ctor.FieldNames[i]);
                sb.Append(": ");
                sb.Append(ctor.Parameters[i]);
                sb.Append('\n');
            }
            return;
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
    }

    private static void WriteEffectDecl(StringBuilder sb, EffectDecl decl, FormattingOptions options)
    {
        sb.Append("effect ");
        sb.Append(decl.Name);
        if (decl.TypeParameters.Count > 0)
        {
            sb.Append('(');
            sb.Append(string.Join(", ", decl.TypeParameters.Select(p => p.Name)));
            sb.Append(')');
        }

        sb.Append(" =\n");
        foreach (var operation in decl.Operations)
        {
            WriteIndent(sb, options.IndentSize, options);
            sb.Append("| ");
            sb.Append(operation.Name);
            if (operation.Signature is not null)
            {
                sb.Append(" : ");
                WriteTypeExpr(sb, operation.Signature);
            }

            sb.Append('\n');
        }
    }

    private static void WriteTopLevelItem(StringBuilder sb, TopLevelItem item, bool preferPipelines, FormattingOptions options)
    {
        switch (item)
        {
            case TopLevelItem.Type t:
                WriteTypeDecl(sb, t.Decl, options);
                return;
            case TopLevelItem.Extern e:
                WriteExternDecl(sb, e.Decl);
                return;
            case TopLevelItem.Effect eff:
                WriteEffectDecl(sb, eff.Decl, options);
                return;
            case TopLevelItem.LetDecl let:
                WriteLetDecl(sb, let, preferPipelines, options);
                return;
            case TopLevelItem.RecGroup group:
                WriteRecGroup(sb, group, preferPipelines, options);
                return;
        }
    }

    private static void WriteLetDecl(StringBuilder sb, TopLevelItem.LetDecl decl, bool preferPipelines, FormattingOptions options)
    {
        sb.Append("let ");
        if (decl.IsRecursive)
        {
            sb.Append("recursive ");
        }
        sb.Append(decl.Name);

        // Type annotation: let f : Type = ... (mutually exclusive with parameter sugar).
        if (decl.TypeAnnotation is { } typeAnnotation)
        {
            sb.Append(" : ");
            WriteTypeExpr(sb, typeAnnotation);
        }

        // ML-style sugar: let f x y = <value>, unwrapping one lambda layer per parameter.
        var value = decl.Value;
        foreach (var p in decl.SugarParams)
        {
            sb.Append(' ');
            sb.Append(p);
            if (value is Expr.Lambda lam)
            {
                value = lam.Body;
            }
        }

        sb.Append(" = ");
        WriteTopLevelValue(sb, value, preferPipelines, options);
    }

    private static void WriteRecGroup(StringBuilder sb, TopLevelItem.RecGroup group, bool preferPipelines, FormattingOptions options)
    {
        // `let rec NAME0 = <value0>` followed by one `and NAMEi = <valuei>` line per remaining
        // binding, each at the same indentation column as `let`. The whole group is one block with
        // no blank lines between members.
        for (int i = 0; i < group.Bindings.Count; i++)
        {
            sb.Append(i == 0 ? "let recursive " : "and ");
            sb.Append(group.Bindings[i].Name);

            // ML-style sugar: let rec f x y = <value>, unwrapping one lambda layer per parameter.
            var value = group.Bindings[i].Value;
            if (i < group.SugarParams.Count)
            {
                foreach (var p in group.SugarParams[i])
                {
                    sb.Append(' ');
                    sb.Append(p);
                    if (value is Expr.Lambda lam)
                    {
                        value = lam.Body;
                    }
                }
            }

            sb.Append(" = ");
            WriteTopLevelValue(sb, value, preferPipelines, options);
        }
    }

    /// <summary>
    /// Writes the right-hand side of a top-level <c>let</c>/<c>and</c> binding (no trailing <c>in</c>),
    /// following the same single-line/multiline rules as a nested <c>let</c> value. Always ends the
    /// binding with a single newline.
    /// </summary>
    private static void WriteTopLevelValue(StringBuilder sb, Expr value, bool preferPipelines, FormattingOptions options)
    {
        if (IsSingleLine(value, preferPipelines))
        {
            WriteExprInline(sb, value, indent: 0, parentPrec: 0, preferPipelines, options);
            sb.Append('\n');
            return;
        }

        sb.Append('\n');
        WriteIndent(sb, options.IndentSize, options);

        // A bare `let`-leading value would re-parse as a nested `let ... in` expression (the trailing
        // body) rather than this flat declaration's value. Parenthesize it — the higher parent
        // precedence makes the let-writer wrap itself in `(...)` — so the round-trip stays a flat
        // declaration and formatting is idempotent.
        var parentPrec = value is Expr.Let or Expr.LetResult or Expr.LetRec ? PrecCall : 0;
        WriteExpr(sb, value, options.IndentSize, parentPrec, preferPipelines, options);
        if (!EndsWithNewLine(sb, "\n"))
        {
            sb.Append('\n');
        }
    }

    private static string WriteParsedType(ParsedType type)
    {
        return type switch
        {
            ParsedType.Named named => named.Name,
            ParsedType.Pointer pointer => $"*{WriteParsedType(pointer.Pointee)}",
            _ => throw new InvalidOperationException($"Unexpected parsed type: {type}")
        };
    }

    private static void WriteTypeExpr(StringBuilder sb, TypeExpr typeExpr)
    {
        switch (typeExpr)
        {
            case TypeExpr.UnitType:
                sb.Append("()");
                return;
            case TypeExpr.Named n:
                sb.Append(n.Name);
                return;
            case TypeExpr.Applied a:
                sb.Append(a.Name);
                sb.Append('(');
                for (int i = 0; i < a.Args.Count; i++)
                {
                    if (i > 0) sb.Append(", ");
                    WriteTypeExpr(sb, a.Args[i]);
                }
                sb.Append(')');
                return;
            case TypeExpr.Arrow arr:
                // An arrow in parameter position must be parenthesized to keep the arrow
                // right-associative on re-parse: (A -> B) -> C.
                if (arr.From is TypeExpr.Arrow)
                {
                    sb.Append('(');
                    WriteTypeExpr(sb, arr.From);
                    sb.Append(')');
                }
                else
                {
                    WriteTypeExpr(sb, arr.From);
                }

                sb.Append(" -> ");
                // When this arrow carries a row and its result is itself an arrow, the result must
                // be parenthesized or the row would re-attach to the inner arrow on re-parse
                // (`uses` binds to the innermost arrow whose result it follows).
                if (arr.Uses is not null && arr.To is TypeExpr.Arrow)
                {
                    sb.Append('(');
                    WriteTypeExpr(sb, arr.To);
                    sb.Append(')');
                }
                else
                {
                    WriteTypeExpr(sb, arr.To);
                }

                if (arr.Uses is { } uses)
                {
                    WriteUsesRow(sb, uses);
                }

                return;
            case TypeExpr.TupleType t:
                sb.Append('(');
                for (int i = 0; i < t.Elements.Count; i++)
                {
                    if (i > 0) sb.Append(", ");
                    WriteTypeExpr(sb, t.Elements[i]);
                }
                sb.Append(')');
                return;
        }
    }

    private static void WriteUsesRow(StringBuilder sb, UsesRowSyntax row)
    {
        sb.Append(" uses ");
        if (row.Effects.Count == 0 && row.TailVar is not null)
        {
            // Bare row variable: `uses e`.
            sb.Append(row.TailVar);
            return;
        }

        sb.Append('{');
        for (int i = 0; i < row.Effects.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(", ");
            }

            sb.Append(row.Effects[i].Name);
            if (row.Effects[i].Args.Count > 0)
            {
                sb.Append('(');
                for (int j = 0; j < row.Effects[i].Args.Count; j++)
                {
                    if (j > 0)
                    {
                        sb.Append(", ");
                    }

                    WriteTypeExpr(sb, row.Effects[i].Args[j]);
                }

                sb.Append(')');
            }
        }

        if (row.TailVar is not null)
        {
            sb.Append(" | ");
            sb.Append(row.TailVar);
        }

        sb.Append('}');
    }

    private static void WriteExternDecl(StringBuilder sb, ExternDecl decl)
    {
        switch (decl)
        {
            case ExternDecl.OpaqueType opaqueType:
                sb.Append("external type ");
                sb.Append(opaqueType.Name);
                sb.Append('\n');
                break;
            case ExternDecl.Function func:
                sb.Append("external ");
                sb.Append(func.Name);
                sb.Append('(');
                sb.Append(string.Join(", ", func.ParameterTypes.Select(WriteParsedType)));
                sb.Append(") -> ");
                sb.Append(WriteParsedType(func.ReturnType));
                if (func.SymbolName is not null)
                {
                    sb.Append(" = \"");
                    sb.Append(EscapeString(func.SymbolName));
                    sb.Append('"');
                }
                sb.Append('\n');
                break;
        }
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

    private static void WriteLeftAssociativeBinary(StringBuilder sb, Expr left, string op, Expr right, int precedence, int indent, int parentPrec, bool preferPipelines, FormattingOptions options)
    {
        var needsParens = parentPrec > precedence;
        if (needsParens)
        {
            sb.Append('(');
        }

        WriteExprInline(sb, left, indent, precedence, preferPipelines, options);
        sb.Append(' ');
        sb.Append(op);
        sb.Append(' ');
        WriteExprInline(sb, right, indent, precedence + 1, preferPipelines, options);

        if (needsParens)
        {
            sb.Append(')');
        }
    }

    private static bool IsSingleLine(Expr e, bool preferPipelines)
    {
        return e switch
        {
            Expr.IntLit or Expr.UIntLit or Expr.FloatLit or Expr.StrLit or Expr.BoolLit or Expr.Var or Expr.QualifiedVar => true,
            Expr.Add a => IsSingleLine(a.Left, preferPipelines) && IsSingleLine(a.Right, preferPipelines),
            Expr.Subtract sub => IsSingleLine(sub.Left, preferPipelines) && IsSingleLine(sub.Right, preferPipelines),
            Expr.Multiply mul => IsSingleLine(mul.Left, preferPipelines) && IsSingleLine(mul.Right, preferPipelines),
            Expr.Divide div => IsSingleLine(div.Left, preferPipelines) && IsSingleLine(div.Right, preferPipelines),
            Expr.BitwiseAnd bitAnd => IsSingleLine(bitAnd.Left, preferPipelines) && IsSingleLine(bitAnd.Right, preferPipelines),
            Expr.BitwiseOr bitOr => IsSingleLine(bitOr.Left, preferPipelines) && IsSingleLine(bitOr.Right, preferPipelines),
            Expr.BitwiseXor bitXor => IsSingleLine(bitXor.Left, preferPipelines) && IsSingleLine(bitXor.Right, preferPipelines),
            Expr.ShiftLeft shiftLeft => IsSingleLine(shiftLeft.Left, preferPipelines) && IsSingleLine(shiftLeft.Right, preferPipelines),
            Expr.ShiftRight shiftRight => IsSingleLine(shiftRight.Left, preferPipelines) && IsSingleLine(shiftRight.Right, preferPipelines),
            Expr.BitwiseNot bitwiseNot => IsSingleLine(bitwiseNot.Operand, preferPipelines),
            Expr.GreaterThan gt => IsSingleLine(gt.Left, preferPipelines) && IsSingleLine(gt.Right, preferPipelines),
            Expr.GreaterOrEqual ge => IsSingleLine(ge.Left, preferPipelines) && IsSingleLine(ge.Right, preferPipelines),
            Expr.LessThan lt => IsSingleLine(lt.Left, preferPipelines) && IsSingleLine(lt.Right, preferPipelines),
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
            Expr.Perform perform => IsSingleLine(perform.Operation, preferPipelines),
            Expr.RecordLit rl => rl.Fields.All(f => IsSingleLine(f.Value, preferPipelines)),
            Expr.RecordUpdate ru => IsSingleLine(ru.Target, preferPipelines) && ru.Updates.All(f => IsSingleLine(f.Value, preferPipelines)),
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

            case Expr.Handle handle:
                WriteHandle(sb, handle, indent, parentPrec, preferPipelines, options);
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

        // Type annotation: let x : Type = ...
        if (l.TypeAnnotation is { } letTypeAnnotation)
        {
            sb.Append(" : ");
            WriteTypeExpr(sb, letTypeAnnotation);
        }

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

        sb.Append("let recursive ");
        sb.Append(l.Name);

        // Type annotation: let rec x : Type = ...
        if (l.TypeAnnotation is { } letRecTypeAnnotation)
        {
            sb.Append(" : ");
            WriteTypeExpr(sb, letRecTypeAnnotation);
        }

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

        sb.Append("given (");
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
            if (matchCase.Guard is not null)
            {
                sb.Append(" when ");
                WriteExprInline(sb, matchCase.Guard, indent + options.IndentSize, 0, preferPipelines, options);
            }
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

    private static void WriteHandle(StringBuilder sb, Expr.Handle handle, int indent, int parentPrec, bool preferPipelines, FormattingOptions options)
    {
        var needsParens = parentPrec > PrecLetIfLambda;
        if (needsParens)
        {
            sb.Append('(');
        }

        sb.Append("handle ");
        WriteExprInline(sb, handle.Body, indent, 0, preferPipelines, options);
        sb.Append(" with\n");

        foreach (var arm in handle.Arms)
        {
            WriteIndent(sb, indent + options.IndentSize, options);
            sb.Append("| ");
            if (arm.EffectName is not null)
            {
                sb.Append(arm.EffectName);
                sb.Append('.');
            }

            sb.Append(arm.OperationName);
            sb.Append('(');
            for (int i = 0; i < arm.Parameters.Count; i++)
            {
                if (i > 0)
                {
                    sb.Append(", ");
                }

                WritePattern(sb, arm.Parameters[i]);
            }

            sb.Append(") -> ");
            if (IsSingleLine(arm.Body, preferPipelines))
            {
                WriteExprInline(sb, arm.Body, indent + options.IndentSize, 0, preferPipelines, options);
                sb.Append('\n');
            }
            else
            {
                sb.Append('\n');
                WriteIndent(sb, indent + options.IndentSize * 2, options);
                WriteExpr(sb, arm.Body, indent + options.IndentSize * 2, 0, preferPipelines, options);
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

            case Expr.UIntLit u:
                sb.Append(u.Value);
                sb.Append('u');
                sb.Append(u.Bits);
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

            case Expr.BitwiseAnd bitAnd:
                WriteLeftAssociativeBinary(sb, bitAnd.Left, "&", bitAnd.Right, PrecBitAnd, indent, parentPrec, preferPipelines, options);
                return;

            case Expr.BitwiseOr bitOr:
                WriteLeftAssociativeBinary(sb, bitOr.Left, "|", bitOr.Right, PrecBitOr, indent, parentPrec, preferPipelines, options);
                return;

            case Expr.BitwiseXor bitXor:
                WriteLeftAssociativeBinary(sb, bitXor.Left, "^", bitXor.Right, PrecBitXor, indent, parentPrec, preferPipelines, options);
                return;

            case Expr.ShiftLeft shiftLeft:
                WriteLeftAssociativeBinary(sb, shiftLeft.Left, "<<", shiftLeft.Right, PrecShift, indent, parentPrec, preferPipelines, options);
                return;

            case Expr.ShiftRight shiftRight:
                WriteLeftAssociativeBinary(sb, shiftRight.Left, ">>", shiftRight.Right, PrecShift, indent, parentPrec, preferPipelines, options);
                return;

            case Expr.BitwiseNot bitwiseNot:
                {
                    var needsParens = parentPrec > PrecUnary;
                    if (needsParens)
                    {
                        sb.Append('(');
                    }

                    sb.Append('~');
                    WriteExprInline(sb, bitwiseNot.Operand, indent, PrecUnary, preferPipelines, options);

                    if (needsParens)
                    {
                        sb.Append(')');
                    }

                    return;
                }

            case Expr.GreaterThan gt:
                {
                    var needsParens = parentPrec > PrecCmp;
                    if (needsParens)
                    {
                        sb.Append('(');
                    }

                    WriteExprInline(sb, gt.Left, indent, PrecCmp, preferPipelines, options);
                    sb.Append(" > ");
                    WriteExprInline(sb, gt.Right, indent, PrecCmp, preferPipelines, options);
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

            case Expr.LessThan lt:
                {
                    var needsParens = parentPrec > PrecCmp;
                    if (needsParens)
                    {
                        sb.Append('(');
                    }

                    WriteExprInline(sb, lt.Left, indent, PrecCmp, preferPipelines, options);
                    sb.Append(" < ");
                    WriteExprInline(sb, lt.Right, indent, PrecCmp, preferPipelines, options);
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
                    var funcNeedsParens = c.Func is Expr.Lambda or Expr.Let or Expr.LetResult or Expr.LetRec or Expr.If
                        or Expr.Add or Expr.Subtract or Expr.Multiply or Expr.Divide
                        or Expr.BitwiseAnd or Expr.BitwiseOr or Expr.BitwiseXor or Expr.ShiftLeft or Expr.ShiftRight
                        or Expr.GreaterThan or Expr.GreaterOrEqual or Expr.LessThan or Expr.LessOrEqual or Expr.Equal or Expr.NotEqual or Expr.Await or Expr.Perform or Expr.Handle;
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

            case Expr.Perform perform:
                {
                    sb.Append("perform ");
                    WriteExprInline(sb, perform.Operation, indent, PrecCall, preferPipelines, options);
                    return;
                }

            case Expr.RecordLit rl:
                {
                    // Brace-free construction: TypeName(field = value, ...)
                    sb.Append(rl.TypeName);
                    sb.Append('(');
                    for (int i = 0; i < rl.Fields.Count; i++)
                    {
                        if (i > 0)
                        {
                            sb.Append(", ");
                        }
                        sb.Append(rl.Fields[i].Name);
                        sb.Append(" = ");
                        WriteExprInline(sb, rl.Fields[i].Value, indent, 0, preferPipelines, options);
                    }
                    sb.Append(')');
                    return;
                }

            case Expr.RecordUpdate ru:
                {
                    // Brace-free update: base with field = value, ...
                    // `with` binds looser than application and the binary operators, so parenthesise
                    // when the surrounding context binds tighter than a pipe.
                    var needsParens = parentPrec > PrecWith;
                    if (needsParens)
                    {
                        sb.Append('(');
                    }
                    // `with` is left-associative: a chained update in target position renders
                    // without parentheses (PrecWith), while field values (right position) get
                    // PrecWith + 1 so a nested update there is parenthesised.
                    WriteExprInline(sb, ru.Target, indent, PrecWith, preferPipelines, options);
                    sb.Append(" with ");
                    for (int i = 0; i < ru.Updates.Count; i++)
                    {
                        if (i > 0)
                        {
                            sb.Append(", ");
                        }
                        sb.Append(ru.Updates[i].Name);
                        sb.Append(" = ");
                        WriteExprInline(sb, ru.Updates[i].Value, indent, PrecWith + 1, preferPipelines, options);
                    }
                    if (needsParens)
                    {
                        sb.Append(')');
                    }
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
        return expr is not (Expr.Let or Expr.LetResult or Expr.LetRec or Expr.If or Expr.Match or Expr.Handle);
    }

    private static string EscapeString(string s)
    {
        return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t");
    }
}
