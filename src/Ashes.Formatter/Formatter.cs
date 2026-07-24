using Ashes.Frontend;
using System.Globalization;
using System.Text;

namespace Ashes.Formatter;

/// <summary>
/// The canonical source formatter: renders a parsed <see cref="Program"/> or <see cref="Expr"/> back
/// to Ashes source text in the one canonical layout. Because the AST carries no trivia, standalone
/// comments are reattached separately by <see cref="CommentReinserter"/>.
/// </summary>
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

    /// <summary>Formats a whole <paramref name="program"/> with default options and no pipeline
    /// rewriting.</summary>
    public static string Format(Program program)
    {
        return Format(program, preferPipelines: false, options: null);
    }

    /// <summary>Formats a whole <paramref name="program"/> using the given whitespace
    /// <paramref name="options"/>, with no pipeline rewriting.</summary>
    public static string Format(Program program, FormattingOptions options)
    {
        return Format(program, preferPipelines: false, options);
    }

    /// <summary>
    /// Formats a whole <paramref name="program"/>. When <paramref name="preferPipelines"/> is true,
    /// eligible call/pipe chains render as multiline <c>|&gt;</c> pipelines. <paramref name="options"/>
    /// selects the whitespace conventions; null uses the defaults.
    /// </summary>
    public static string Format(Program program, bool preferPipelines, FormattingOptions? options = null)
    {
        var formattingOptions = (options ?? new FormattingOptions()).Normalize();
        var sb = new StringBuilder();

        // A file is a flat sequence of top-level items in source order, followed by an optional
        // trailing expression. Exactly one blank line separates adjacent items, and one blank line
        // separates the last item from the trailing expression. Each writer below ends the item with
        // a single newline; the blank line is the extra '\n' inserted before each subsequent block.
        // Consecutive `external` declarations are the one exception: they stay grouped as a block with
        // no blank line between them (matching established formatting of FFI declaration blocks).
        TopLevelItem? previous = null;
        foreach (var item in program.Items)
        {
            if (previous is not null && !(previous is TopLevelItem.External && item is TopLevelItem.External))
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
        return FinishOutput(sb, formattingOptions);
    }

    /// <summary>Formats a single <paramref name="expr"/> with default options and no pipeline
    /// rewriting.</summary>
    public static string Format(Expr expr)
    {
        return Format(expr, preferPipelines: false, options: null);
    }

    /// <summary>Formats a single <paramref name="expr"/> using the given whitespace
    /// <paramref name="options"/>, with no pipeline rewriting.</summary>
    public static string Format(Expr expr, FormattingOptions options)
    {
        return Format(expr, preferPipelines: false, options);
    }

    /// <summary>
    /// Formats a single <paramref name="expr"/>. When <paramref name="preferPipelines"/> is true,
    /// eligible call/pipe chains render as multiline <c>|&gt;</c> pipelines. <paramref name="options"/>
    /// selects the whitespace conventions; null uses the defaults.
    /// </summary>
    public static string Format(Expr expr, bool preferPipelines, FormattingOptions? options = null)
    {
        var formattingOptions = (options ?? new FormattingOptions()).Normalize();
        var sb = new StringBuilder();
        WriteExpr(sb, expr, indent: 0, parentPrec: 0, preferPipelines, formattingOptions);
        if (sb.Length == 0 || sb[^1] != '\n')
        {
            sb.Append('\n');
        }

        return FinishOutput(sb, formattingOptions);
    }

    /// <summary>
    /// Strips trailing spaces/tabs from every line, then applies the configured newline. The tree
    /// writers append structural padding (e.g. the space after <c>=</c> or <c>-&gt;</c>) before
    /// deciding to break the line, which would otherwise leave trailing whitespace. Trimming at the
    /// line level is safe because string literals are emitted on a single line with escaped
    /// <c>\n</c>, so a physical line never ends inside a literal.
    /// </summary>
    private static string FinishOutput(StringBuilder sb, FormattingOptions options)
    {
        var result = new StringBuilder(sb.Length);
        int lineStart = 0;
        for (int i = 0; i < sb.Length; i++)
        {
            if (sb[i] != '\n')
            {
                continue;
            }

            int lineEnd = i;
            while (lineEnd > lineStart && (sb[lineEnd - 1] == ' ' || sb[lineEnd - 1] == '\t'))
            {
                lineEnd--;
            }

            for (int j = lineStart; j < lineEnd; j++)
            {
                result.Append(sb[j]);
            }

            result.Append('\n');
            lineStart = i + 1;
        }

        for (int j = lineStart; j < sb.Length; j++)
        {
            result.Append(sb[j]);
        }

        string text = result.ToString();
        return string.Equals(options.NewLine, "\n", StringComparison.Ordinal) ? text : text.Replace("\n", options.NewLine, StringComparison.Ordinal);
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
                WriteTypeExpr(sb, ctor.Parameters[i]);
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
                for (int i = 0; i < ctor.Parameters.Count; i++)
                {
                    if (i > 0)
                    {
                        sb.Append(", ");
                    }

                    WriteTypeExpr(sb, ctor.Parameters[i]);
                }
                sb.Append(')');
            }
            sb.Append('\n');
        }
    }

    private static void WriteCapabilityDecl(StringBuilder sb, CapabilityDecl decl, FormattingOptions options)
    {
        sb.Append("capability ");
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

    private static void WriteProvideDecl(StringBuilder sb, ProvideDecl decl, bool preferPipelines, FormattingOptions options)
    {
        sb.Append("provide ");
        sb.Append(decl.CapabilityName);
        if (decl.TypeArgs.Count > 0)
        {
            sb.Append('(');
            for (int i = 0; i < decl.TypeArgs.Count; i++)
            {
                if (i > 0)
                {
                    sb.Append(", ");
                }

                WriteTypeExpr(sb, decl.TypeArgs[i]);
            }

            sb.Append(')');
        }

        sb.Append(" =\n");
        foreach (var binding in decl.Bindings)
        {
            WriteIndent(sb, options.IndentSize, options);
            sb.Append("| ");
            sb.Append(binding.OperationName);
            sb.Append(" = ");
            if (IsSingleLine(binding.Implementation, preferPipelines))
            {
                WriteExprInline(sb, binding.Implementation, options.IndentSize, 0, preferPipelines, options);
                sb.Append('\n');
            }
            else
            {
                sb.Append('\n');
                WriteIndent(sb, options.IndentSize * 2, options);
                WriteExpr(sb, binding.Implementation, options.IndentSize * 2, 0, preferPipelines, options);
                if (!EndsWithNewLine(sb, "\n"))
                {
                    sb.Append('\n');
                }
            }
        }
    }

    private static void WriteTopLevelItem(StringBuilder sb, TopLevelItem item, bool preferPipelines, FormattingOptions options)
    {
        switch (item)
        {
            case TopLevelItem.Type t:
                WriteTypeDecl(sb, t.Decl, options);
                return;
            case TopLevelItem.External e:
                WriteExternalDecl(sb, e.Decl);
                return;
            case TopLevelItem.Capability eff:
                WriteCapabilityDecl(sb, eff.Decl, options);
                return;
            case TopLevelItem.Provide prov:
                WriteProvideDecl(sb, prov.Decl, preferPipelines, options);
                return;
            case TopLevelItem.LetDecl let:
                WriteLetDecl(sb, let, preferPipelines, options);
                return;
            case TopLevelItem.RecursiveGroup group:
                WriteRecursiveGroup(sb, group, preferPipelines, options);
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
            value = AppendSugarParam(sb, p, value);
        }

        sb.Append(" = ");
        WriteTopLevelValue(sb, value, preferPipelines, options);
    }

    /// <summary>
    /// Renders one ML-style sugar parameter and unwraps its lambda layer. An annotated parameter
    /// (the desugared lambda carries <see cref="Expr.Lambda.ParamAnnotation"/>) renders
    /// parenthesized as <c>(name: Type)</c>; a plain one renders bare.
    /// </summary>
    private static Expr AppendSugarParam(StringBuilder sb, string name, Expr value)
    {
        sb.Append(' ');
        if (value is Expr.Lambda lam)
        {
            if (lam.ParamAnnotation is { } annotation)
            {
                sb.Append('(');
                sb.Append(name);
                sb.Append(": ");
                WriteTypeExpr(sb, annotation);
                sb.Append(')');
            }
            else
            {
                sb.Append(name);
            }

            return lam.Body;
        }

        sb.Append(name);
        return value;
    }

    private static void WriteRecursiveGroup(StringBuilder sb, TopLevelItem.RecursiveGroup group, bool preferPipelines, FormattingOptions options)
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
                    value = AppendSugarParam(sb, p, value);
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
        var parentPrec = value is Expr.Let or Expr.LetResult or Expr.LetRecursive ? PrecCall : 0;
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
                WriteArrowTypeExpr(sb, arr);
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

    private static void WriteArrowTypeExpr(StringBuilder sb, TypeExpr.Arrow arr)
    {
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
        if (arr.Needs is not null && arr.To is TypeExpr.Arrow)
        {
            sb.Append('(');
            WriteTypeExpr(sb, arr.To);
            sb.Append(')');
        }
        else
        {
            WriteTypeExpr(sb, arr.To);
        }

        if (arr.Needs is { } needs)
        {
            WriteNeedsRow(sb, needs);
        }
    }

    private static void WriteNeedsRow(StringBuilder sb, NeedsRowSyntax row)
    {
        sb.Append(" needs ");
        if (row.Capabilities.Count == 0 && row.TailVar is not null)
        {
            // Bare row variable: `uses e`.
            sb.Append(row.TailVar);
            return;
        }

        sb.Append('{');
        for (int i = 0; i < row.Capabilities.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(", ");
            }

            sb.Append(row.Capabilities[i].Name);
            if (row.Capabilities[i].Args.Count > 0)
            {
                sb.Append('(');
                for (int j = 0; j < row.Capabilities[i].Args.Count; j++)
                {
                    if (j > 0)
                    {
                        sb.Append(", ");
                    }

                    WriteTypeExpr(sb, row.Capabilities[i].Args[j]);
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

    private static void WriteExternalDecl(StringBuilder sb, ExternalDecl decl)
    {
        switch (decl)
        {
            case ExternalDecl.OpaqueType opaqueType:
                sb.Append("external type ");
                sb.Append(opaqueType.Name);
                sb.Append('\n');
                break;
            case ExternalDecl.Function func:
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
        if (string.Equals(newLine, "\n", StringComparison.Ordinal))
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
            Expr.IntLit or Expr.UIntLit or Expr.BigIntLit or Expr.FloatLit or Expr.StrLit or Expr.BoolLit or Expr.Var or Expr.QualifiedVar => true,
            Expr.Add a => IsSingleLine(a.Left, preferPipelines) && IsSingleLine(a.Right, preferPipelines),
            Expr.Subtract sub => IsSingleLine(sub.Left, preferPipelines) && IsSingleLine(sub.Right, preferPipelines),
            Expr.Multiply mul => IsSingleLine(mul.Left, preferPipelines) && IsSingleLine(mul.Right, preferPipelines),
            Expr.Divide div => IsSingleLine(div.Left, preferPipelines) && IsSingleLine(div.Right, preferPipelines),
            Expr.Modulo modExpr => IsSingleLine(modExpr.Left, preferPipelines) && IsSingleLine(modExpr.Right, preferPipelines),
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

            case Expr.LetRecursive l:
                WriteLetRecursive(sb, l, indent, parentPrec, preferPipelines, options);
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
                value = AppendSugarParam(sb, p, value);
            }
        }

        sb.Append(" = ");
        WriteLetValueAndBody(sb, value, l.Body, indent, preferPipelines, options);

        if (needsParens)
        {
            sb.Append(')');
        }
    }

    /// <summary>
    /// Writes the shared <c>= value ... in body</c> tail of a nested let form: the value inline or
    /// indented on its own line, then <c>in</c> and the body (also inline or indented when multiline).
    /// </summary>
    private static void WriteLetValueAndBody(StringBuilder sb, Expr value, Expr body, int indent, bool preferPipelines, FormattingOptions options)
    {
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
        if (IsSingleLine(body, preferPipelines))
        {
            WriteExprInline(sb, body, indent, 0, preferPipelines, options);
        }
        else
        {
            sb.Append('\n');
            WriteIndent(sb, indent + options.IndentSize, options);
            WriteExpr(sb, body, indent + options.IndentSize, 0, preferPipelines, options);
        }
    }

    private static void WriteLetRecursive(StringBuilder sb, Expr.LetRecursive l, int indent, int parentPrec, bool preferPipelines, FormattingOptions options)
    {
        var needsParens = parentPrec > PrecLetIfLambda;
        if (needsParens)
        {
            sb.Append('(');
        }

        sb.Append("let recursive ");
        sb.Append(l.Name);

        // Type annotation: let rec x : Type = ...
        if (l.TypeAnnotation is { } letRecursiveTypeAnnotation)
        {
            sb.Append(" : ");
            WriteTypeExpr(sb, letRecursiveTypeAnnotation);
        }

        // ML-style sugar: let rec f x y = <value>
        var value = l.Value;
        if (l.SugarParams.Count > 0)
        {
            foreach (var p in l.SugarParams)
            {
                value = AppendSugarParam(sb, p, value);
            }
        }

        sb.Append(" = ");
        WriteLetValueAndBody(sb, value, l.Body, indent, preferPipelines, options);

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
        if (lam.ParamAnnotation is { } paramAnnotation)
        {
            sb.Append(": ");
            WriteTypeExpr(sb, paramAnnotation);
        }

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
            if (arm.CapabilityName is not null)
            {
                sb.Append(arm.CapabilityName);
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
        if (TryWriteAtomInline(sb, e))
        {
            return;
        }

        if (TryWriteArithmeticInline(sb, e, indent, parentPrec, preferPipelines, options))
        {
            return;
        }

        if (TryWriteComparisonOrPipeInline(sb, e, indent, parentPrec, preferPipelines, options))
        {
            return;
        }

        WriteStructuralExprInline(sb, e, indent, parentPrec, preferPipelines, options);
    }

    /// <summary>Writes leaf expressions that render without recursion: literals and variables.</summary>
    private static bool TryWriteAtomInline(StringBuilder sb, Expr e)
    {
        switch (e)
        {
            case Expr.IntLit i:
                sb.Append(i.Value);
                return true;

            case Expr.UIntLit u:
                sb.Append(u.Value);
                sb.Append('u');
                sb.Append(u.Bits);
                return true;

            case Expr.BigIntLit big:
                sb.Append(big.Digits);
                sb.Append('N');
                return true;

            case Expr.FloatLit f:
                sb.Append(FormatFloatLiteral(f));
                return true;

            case Expr.StrLit s:
                sb.Append('"');
                sb.Append(EscapeString(s.Value));
                sb.Append('"');
                return true;

            case Expr.BoolLit b:
                sb.Append(b.Value ? "true" : "false");
                return true;

            case Expr.Var v:
                sb.Append(v.Name);
                return true;

            case Expr.QualifiedVar qv:
                sb.Append(qv.Module);
                sb.Append('.');
                sb.Append(qv.Name);
                return true;

            default:
                return false;
        }
    }

    /// <summary>Writes the remaining structured forms: collections, calls, records, and keyword prefixes.</summary>
    private static void WriteStructuralExprInline(StringBuilder sb, Expr e, int indent, int parentPrec, bool preferPipelines, FormattingOptions options)
    {
        switch (e)
        {

            case Expr.TupleLit tuple:
                WriteDelimitedElementsInline(sb, '(', tuple.Elements, ')', indent, preferPipelines, options);
                return;

            case Expr.ListLit list:
                WriteDelimitedElementsInline(sb, '[', list.Elements, ']', indent, preferPipelines, options);
                return;

            case Expr.Call c:
                WriteCallInline(sb, c, indent, parentPrec, preferPipelines, options);
                return;

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
                WriteRecordLitInline(sb, rl, indent, preferPipelines, options);
                return;

            case Expr.RecordUpdate ru:
                WriteRecordUpdateInline(sb, ru, indent, parentPrec, preferPipelines, options);
                return;

            // Fallback to multiline writer (rare)
            default:
                WriteExpr(sb, e, indent, parentPrec, preferPipelines, options);
                return;
        }
    }

    /// <summary>Writes the arithmetic, bitwise, and cons operator forms.</summary>
    private static bool TryWriteArithmeticInline(StringBuilder sb, Expr e, int indent, int parentPrec, bool preferPipelines, FormattingOptions options)
    {
        switch (e)
        {
            case Expr.Cons cons:
                WriteBinaryInline(sb, cons.Head, " :: ", cons.Tail, PrecCons, PrecCons + 1, PrecCons, indent, parentPrec, preferPipelines, options);
                return true;

            case Expr.Add a:
                WriteBinaryInline(sb, a.Left, " + ", a.Right, PrecAdd, PrecAdd, PrecAdd, indent, parentPrec, preferPipelines, options);
                return true;

            case Expr.Subtract sub:
                WriteSubtractInline(sb, sub, indent, parentPrec, preferPipelines, options);
                return true;

            case Expr.Multiply mul:
                WriteBinaryInline(sb, mul.Left, " * ", mul.Right, PrecMul, PrecMul, PrecMul, indent, parentPrec, preferPipelines, options);
                return true;

            case Expr.Divide div:
                WriteBinaryInline(sb, div.Left, " / ", div.Right, PrecMul, PrecMul, PrecMul + 1, indent, parentPrec, preferPipelines, options);
                return true;

            case Expr.Modulo modExpr:
                WriteBinaryInline(sb, modExpr.Left, " % ", modExpr.Right, PrecMul, PrecMul, PrecMul + 1, indent, parentPrec, preferPipelines, options);
                return true;

            case Expr.BitwiseAnd bitAnd:
                WriteLeftAssociativeBinary(sb, bitAnd.Left, "&", bitAnd.Right, PrecBitAnd, indent, parentPrec, preferPipelines, options);
                return true;

            case Expr.BitwiseOr bitOr:
                WriteLeftAssociativeBinary(sb, bitOr.Left, "|", bitOr.Right, PrecBitOr, indent, parentPrec, preferPipelines, options);
                return true;

            case Expr.BitwiseXor bitXor:
                WriteLeftAssociativeBinary(sb, bitXor.Left, "^", bitXor.Right, PrecBitXor, indent, parentPrec, preferPipelines, options);
                return true;

            case Expr.ShiftLeft shiftLeft:
                WriteLeftAssociativeBinary(sb, shiftLeft.Left, "<<", shiftLeft.Right, PrecShift, indent, parentPrec, preferPipelines, options);
                return true;

            case Expr.ShiftRight shiftRight:
                WriteLeftAssociativeBinary(sb, shiftRight.Left, ">>", shiftRight.Right, PrecShift, indent, parentPrec, preferPipelines, options);
                return true;

            case Expr.BitwiseNot bitwiseNot:
                WriteUnaryPrefixInline(sb, '~', bitwiseNot.Operand, indent, parentPrec, preferPipelines, options);
                return true;

            default:
                return false;
        }
    }

    /// <summary>Writes the comparison operators and the result-pipe operators.</summary>
    private static bool TryWriteComparisonOrPipeInline(StringBuilder sb, Expr e, int indent, int parentPrec, bool preferPipelines, FormattingOptions options)
    {
        switch (e)
        {
            case Expr.GreaterThan gt:
                WriteBinaryInline(sb, gt.Left, " > ", gt.Right, PrecCmp, PrecCmp, PrecCmp, indent, parentPrec, preferPipelines, options);
                return true;

            case Expr.GreaterOrEqual ge:
                WriteBinaryInline(sb, ge.Left, " >= ", ge.Right, PrecCmp, PrecCmp, PrecCmp, indent, parentPrec, preferPipelines, options);
                return true;

            case Expr.LessThan lt:
                WriteBinaryInline(sb, lt.Left, " < ", lt.Right, PrecCmp, PrecCmp, PrecCmp, indent, parentPrec, preferPipelines, options);
                return true;

            case Expr.LessOrEqual le:
                WriteBinaryInline(sb, le.Left, " <= ", le.Right, PrecCmp, PrecCmp, PrecCmp, indent, parentPrec, preferPipelines, options);
                return true;

            case Expr.Equal eq:
                WriteBinaryInline(sb, eq.Left, " == ", eq.Right, PrecCmp, PrecCmp, PrecCmp, indent, parentPrec, preferPipelines, options);
                return true;

            case Expr.NotEqual ne:
                WriteBinaryInline(sb, ne.Left, " != ", ne.Right, PrecCmp, PrecCmp, PrecCmp, indent, parentPrec, preferPipelines, options);
                return true;

            case Expr.ResultPipe pipe:
                WritePipeOperatorInline(sb, pipe, pipe.Left, " |?> ", pipe.Right, indent, parentPrec, preferPipelines, options);
                return true;

            case Expr.ResultMapErrorPipe pipe:
                WritePipeOperatorInline(sb, pipe, pipe.Left, " |!> ", pipe.Right, indent, parentPrec, preferPipelines, options);
                return true;

            default:
                return false;
        }
    }

    /// <summary>
    /// Writes an infix operator expression, parenthesizing when the surrounding context binds
    /// tighter than the operator itself. Left/right operand precedences are passed explicitly so
    /// each operator keeps its exact associativity (e.g. cons is right-associative, subtract's
    /// right operand binds one tighter).
    /// </summary>
    private static void WriteBinaryInline(StringBuilder sb, Expr left, string op, Expr right, int ownPrec, int leftPrec, int rightPrec, int indent, int parentPrec, bool preferPipelines, FormattingOptions options)
    {
        var needsParens = parentPrec > ownPrec;
        if (needsParens)
        {
            sb.Append('(');
        }

        WriteExprInline(sb, left, indent, leftPrec, preferPipelines, options);
        sb.Append(op);
        WriteExprInline(sb, right, indent, rightPrec, preferPipelines, options);
        if (needsParens)
        {
            sb.Append(')');
        }
    }

    // `0 - x` is the parser's encoding of unary minus; it renders back as `-x`.
    private static void WriteSubtractInline(StringBuilder sb, Expr.Subtract sub, int indent, int parentPrec, bool preferPipelines, FormattingOptions options)
    {
        if (sub.Left is Expr.IntLit { Value: 0 })
        {
            WriteUnaryPrefixInline(sb, '-', sub.Right, indent, parentPrec, preferPipelines, options);
            return;
        }

        WriteBinaryInline(sb, sub.Left, " - ", sub.Right, PrecAdd, PrecAdd, PrecAdd + 1, indent, parentPrec, preferPipelines, options);
    }

    private static void WriteUnaryPrefixInline(StringBuilder sb, char op, Expr operand, int indent, int parentPrec, bool preferPipelines, FormattingOptions options)
    {
        var needsParens = parentPrec > PrecUnary;
        if (needsParens)
        {
            sb.Append('(');
        }

        sb.Append(op);
        WriteExprInline(sb, operand, indent, PrecUnary, preferPipelines, options);

        if (needsParens)
        {
            sb.Append(')');
        }
    }

    // A pipe chain at statement level prefers the multiline pipeline form when enabled.
    private static void WritePipeOperatorInline(StringBuilder sb, Expr pipe, Expr left, string op, Expr right, int indent, int parentPrec, bool preferPipelines, FormattingOptions options)
    {
        if (preferPipelines && parentPrec == 0 && TryWritePipeline(sb, pipe, indent, parentPrec, preferPipelines, options))
        {
            return;
        }

        WriteBinaryInline(sb, left, op, right, PrecPipe, PrecPipe, PrecPipe + 1, indent, parentPrec, preferPipelines, options);
    }

    private static void WriteCallInline(StringBuilder sb, Expr.Call c, int indent, int parentPrec, bool preferPipelines, FormattingOptions options)
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
        var funcNeedsParens = c.Func is Expr.Lambda or Expr.Let or Expr.LetResult or Expr.LetRecursive or Expr.If
            or Expr.Add or Expr.Subtract or Expr.Multiply or Expr.Divide or Expr.Modulo
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
    }

    private static void WriteDelimitedElementsInline(StringBuilder sb, char open, IReadOnlyList<Expr> elements, char close, int indent, bool preferPipelines, FormattingOptions options)
    {
        sb.Append(open);
        for (int i = 0; i < elements.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(", ");
            }
            WriteExprInline(sb, elements[i], indent, 0, preferPipelines, options);
        }
        sb.Append(close);
    }

    private static void WriteRecordLitInline(StringBuilder sb, Expr.RecordLit rl, int indent, bool preferPipelines, FormattingOptions options)
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
    }

    private static void WriteRecordUpdateInline(StringBuilder sb, Expr.RecordUpdate ru, int indent, int parentPrec, bool preferPipelines, FormattingOptions options)
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
        return expr is not (Expr.Let or Expr.LetResult or Expr.LetRecursive or Expr.If or Expr.Match or Expr.Handle);
    }

    private static string EscapeString(string s)
    {
        return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t");
    }
}
