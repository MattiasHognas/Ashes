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
            WriteDerivingClause(sb, decl, options);
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
        WriteDerivingClause(sb, decl, options);
    }

    private static void WriteDerivingClause(StringBuilder sb, TypeDecl decl, FormattingOptions options)
    {
        if (decl.Deriving.Count == 0)
        {
            return;
        }

        WriteIndent(sb, options.IndentSize, options);
        sb.Append("deriving {");
        sb.Append(string.Join(", ", decl.Deriving));
        sb.Append("}\n");
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

    private static void WriteTraitDecl(StringBuilder sb, TraitDecl declaration, bool preferPipelines, FormattingOptions options)
    {
        sb.Append("trait ");
        sb.Append(declaration.Name);
        WriteTypeParameters(sb, declaration.TypeParameters);
        WriteRequiresClause(sb, declaration.Supertraits);
        sb.Append(" =\n");
        foreach (TraitMethodDecl method in declaration.Methods)
        {
            WriteIndent(sb, options.IndentSize, options);
            sb.Append("| ");
            sb.Append(method.Name);
            sb.Append(" : ");
            WriteTypeExpr(sb, method.Signature);
            if (method.DefaultImplementation is null)
            {
                sb.Append('\n');
                continue;
            }

            WriteDeclarationImplementation(sb, method.DefaultImplementation, preferPipelines, options);
        }
    }

    private static void WriteTraitImplementationDecl(StringBuilder sb, TraitImplementationDecl declaration, bool preferPipelines, FormattingOptions options)
    {
        sb.Append("implement ");
        sb.Append(declaration.TraitName);
        WriteTypeArguments(sb, declaration.TypeArgs);
        WriteRequiresClause(sb, declaration.Requirements);
        sb.Append(" =\n");
        foreach (TraitImplementationMethodBinding binding in declaration.Bindings)
        {
            WriteIndent(sb, options.IndentSize, options);
            sb.Append("| ");
            sb.Append(binding.MethodName);
            WriteDeclarationImplementation(sb, binding.Implementation, preferPipelines, options);
        }
    }

    private static void WriteDeclarationImplementation(
        StringBuilder sb,
        Expr implementation,
        bool preferPipelines,
        FormattingOptions options)
    {
        sb.Append(" = ");
        if (IsSingleLine(implementation, preferPipelines))
        {
            WriteExprInline(sb, implementation, options.IndentSize, 0, preferPipelines, options);
            sb.Append('\n');
            return;
        }

        sb.Append('\n');
        WriteIndent(sb, options.IndentSize * 2, options);
        WriteExpr(sb, implementation, options.IndentSize * 2, 0, preferPipelines, options);
        if (!EndsWithNewLine(sb, "\n"))
        {
            sb.Append('\n');
        }
    }

    private static void WriteTopLevelItem(StringBuilder sb, TopLevelItem item, bool preferPipelines, FormattingOptions options)
    {
        switch (item)
        {
            case TopLevelItem.Export export:
                WriteExportDecl(sb, export.Decl, options);
                return;
            case TopLevelItem.Type t:
                WriteTypeDecl(sb, t.Decl, options);
                return;
            case TopLevelItem.TypeAlias alias:
                WriteTypeAliasDecl(sb, alias.Decl);
                return;
            case TopLevelItem.ZeroCostType zeroCostType:
                WriteZeroCostTypeDecl(sb, zeroCostType.Decl);
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
            case TopLevelItem.Trait trait:
                WriteTraitDecl(sb, trait.Decl, preferPipelines, options);
                return;
            case TopLevelItem.Implementation implementation:
                WriteTraitImplementationDecl(sb, implementation.Decl, preferPipelines, options);
                return;
            case TopLevelItem.LetDecl let:
                WriteLetDecl(sb, let, preferPipelines, options);
                return;
            case TopLevelItem.RecursiveGroup group:
                WriteRecursiveGroup(sb, group, preferPipelines, options);
                return;
        }
    }

    private static void WriteTypeAliasDecl(StringBuilder sb, TypeAliasDecl declaration)
    {
        sb.Append("type alias ").Append(declaration.Name);
        if (declaration.TypeParameters.Count > 0)
        {
            WriteTypeParameters(sb, declaration.TypeParameters);
        }
        sb.Append(" = ");
        WriteTypeExpr(sb, declaration.Target);
        sb.Append('\n');
    }

    private static void WriteZeroCostTypeDecl(StringBuilder sb, ZeroCostTypeDecl declaration)
    {
        sb.Append("type ").Append(declaration.Name);
        if (declaration.TypeParameters.Count > 0)
        {
            WriteTypeParameters(sb, declaration.TypeParameters);
        }
        sb.Append(" = ").Append(declaration.Constructor.Name).Append('(');
        WriteTypeExpr(sb, declaration.Constructor.Parameters[0]);
        sb.Append(")\n");
        if (declaration.Deriving.Count > 0)
        {
            sb.Append("    deriving {").Append(string.Join(", ", declaration.Deriving)).Append("}\n");
        }
    }

    private static void WriteExportDecl(StringBuilder sb, ExportDecl declaration, FormattingOptions options)
    {
        sb.Append("export (\n");
        foreach (ExportItem item in declaration.Items)
        {
            WriteIndent(sb, options.IndentSize, options);
            switch (item)
            {
                case ExportItem.Value value:
                    sb.Append("value ").Append(value.Name);
                    break;
                case ExportItem.Module module:
                    sb.Append("module ").Append(module.Name);
                    break;
                case ExportItem.Type type:
                    sb.Append("type ").Append(type.Name);
                    switch (type.Constructors)
                    {
                        case ExportConstructors.All:
                            sb.Append("(..)");
                            break;
                        case ExportConstructors.Selected selected:
                            sb.Append('(').Append(string.Join(", ", selected.Names)).Append(')');
                            break;
                    }
                    break;
            }

            sb.Append(",\n");
        }

        sb.Append(")\n");
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
            WriteRequiresClause(sb, decl.Requires);
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

            if (i < group.TypeAnnotations.Count && group.TypeAnnotations[i] is { } annotation)
            {
                sb.Append(" : ");
                WriteTypeExpr(sb, annotation);
                if (i < group.Requires.Count)
                {
                    WriteRequiresClause(sb, group.Requires[i]);
                }
            }

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
            ParsedType.Buffer buffer => $"FfiBuffer({WriteParsedType(buffer.Element)})",
            ParsedType.Out output => $"out {WriteParsedType(output.Element)}",
            ParsedType.NativeString native => FormatNativeStringType(native),
            _ => throw new InvalidOperationException($"Unexpected parsed type: {type}")
        };
    }

    private static string FormatNativeStringType(ParsedType.NativeString native)
    {
        string nullable = native.Nullable ? "nullable " : string.Empty;
        string ownership = native.Ownership == FfiStringOwnership.Borrowed
            ? "borrowed"
            : $"owned {native.DestructorName}";
        return $"FfiStr({nullable}{ownership})";
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

    private static void WriteTypeParameters(StringBuilder sb, IReadOnlyList<TypeParameter> parameters)
    {
        sb.Append('(');
        for (int i = 0; i < parameters.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(", ");
            }

            sb.Append(parameters[i].Name);
        }

        sb.Append(')');
    }

    private static void WriteTypeArguments(StringBuilder sb, IReadOnlyList<TypeExpr> arguments)
    {
        sb.Append('(');
        for (int i = 0; i < arguments.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(", ");
            }

            WriteTypeExpr(sb, arguments[i]);
        }

        sb.Append(')');
    }

    private static void WriteRequiresClause(StringBuilder sb, IReadOnlyList<TraitConstraintSyntax> constraints)
    {
        if (constraints.Count == 0)
        {
            return;
        }

        TraitConstraintSyntax[] ordered = constraints
            .OrderBy(ConstraintSortKey, StringComparer.Ordinal)
            .ToArray();
        sb.Append(" requires {");
        for (int i = 0; i < ordered.Length; i++)
        {
            if (i > 0)
            {
                sb.Append(", ");
            }

            sb.Append(ordered[i].TraitName);
            WriteTypeArguments(sb, ordered[i].TypeArgs);
        }

        sb.Append('}');
    }

    private static string ConstraintSortKey(TraitConstraintSyntax constraint)
    {
        StringBuilder key = new();
        key.Append(constraint.TraitName);
        WriteTypeArguments(key, constraint.TypeArgs);
        return key.ToString();
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
                if (opaqueType.DestructorName is not null)
                {
                    sb.Append(" resource destructor ");
                    sb.Append(opaqueType.DestructorName);
                }
                sb.Append('\n');
                break;
            case ExternalDecl.Function func:
                sb.Append("external ");
                sb.Append(func.Name);
                sb.Append('(');
                sb.Append(string.Join(", ", func.ParameterTypes.Select((type, index) =>
                    WriteExternalParameter(
                        type,
                        index < func.ParameterOwnerships.Count
                            ? func.ParameterOwnerships[index]
                            : ExternalParameterOwnership.Unspecified))));
                sb.Append(") -> ");
                sb.Append(WriteParsedType(func.ReturnType));
                if (func.Needs is not null)
                {
                    WriteNeedsRow(sb, func.Needs);
                }
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

    private static string WriteExternalParameter(
        ParsedType type,
        ExternalParameterOwnership ownership)
    {
        string prefix = ownership switch
        {
            ExternalParameterOwnership.Borrow => "borrow ",
            ExternalParameterOwnership.Consume => "consume ",
            _ => string.Empty,
        };
        return prefix + WriteParsedType(type);
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
            Expr.IntLit or Expr.UIntLit or Expr.BigIntLit or Expr.FloatLit or Expr.StrLit or Expr.RuneLit or Expr.BoolLit or Expr.Var or Expr.QualifiedVar => true,
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
            Expr.LogicalNot logicalNot => IsSingleLine(logicalNot.Operand, preferPipelines),
            Expr.GreaterThan gt => IsSingleLine(gt.Left, preferPipelines) && IsSingleLine(gt.Right, preferPipelines),
            Expr.GreaterOrEqual ge => IsSingleLine(ge.Left, preferPipelines) && IsSingleLine(ge.Right, preferPipelines),
            Expr.LessThan lt => IsSingleLine(lt.Left, preferPipelines) && IsSingleLine(lt.Right, preferPipelines),
            Expr.LessOrEqual le => IsSingleLine(le.Left, preferPipelines) && IsSingleLine(le.Right, preferPipelines),
            Expr.Equal eq => IsSingleLine(eq.Left, preferPipelines) && IsSingleLine(eq.Right, preferPipelines),
            Expr.NotEqual ne => IsSingleLine(ne.Left, preferPipelines) && IsSingleLine(ne.Right, preferPipelines),
            Expr.ResultPipe pipe => (!preferPipelines || !TryCollectPipeline(pipe, out _, out _)) && IsSingleLine(pipe.Left, preferPipelines) && IsSingleLine(pipe.Right, preferPipelines),
            Expr.ResultMapErrorPipe pipe => (!preferPipelines || !TryCollectPipeline(pipe, out _, out _)) && IsSingleLine(pipe.Left, preferPipelines) && IsSingleLine(pipe.Right, preferPipelines),
            Expr.TupleLit tuple => tuple.Elements.All(x => IsSingleLine(x, preferPipelines)),
            Expr.ListLit list => (list.Elements.Count == 0 || !list.IsMultiline)
                && list.Elements.All(x => IsSingleLine(x, preferPipelines)),
            Expr.Cons cons => IsSingleLine(cons.Head, preferPipelines) && IsSingleLine(cons.Tail, preferPipelines),
            Expr.Call c => c.ArgumentListLayout == CallArgumentListLayout.Inline
                && (!preferPipelines || !TryCollectPipeline(c, out _, out _))
                && IsSingleLine(c.Func, preferPipelines)
                && IsSingleLine(c.Arg, preferPipelines),
            Expr.Await awaitExpr => IsSingleLine(awaitExpr.Task, preferPipelines),
            Expr.Perform perform => IsSingleLine(perform.Operation, preferPipelines),
            Expr.RecordLit rl => !rl.IsMultiline && rl.Fields.All(f => IsSingleLine(f.Value, preferPipelines)),
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
            WriteRequiresClause(sb, l.Requires);
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
            WriteRequiresClause(sb, l.Requires);
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
        int valuePrecedence = ContainsRecordUpdate(match.Value) ? PrecWith + 1 : 0;
        WriteExprInline(sb, match.Value, indent, valuePrecedence, preferPipelines, options);
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
            WriteArmBody(sb, matchCase.Body, indent, preferPipelines, options);
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

    private static bool ContainsBitwiseOr(Expr expression) => ContainsExpression(expression, static candidate => candidate is Expr.BitwiseOr);

    private static bool ContainsRecordUpdate(Expr expression) => ContainsExpression(expression, static candidate => candidate is Expr.RecordUpdate);

    // Whether a record update sits at the unparenthesized right edge of an expression: the update
    // itself, or the trailing body of a let, lambda, conditional, match, or handler. `with` takes
    // every following `name = value` pair as one of its own fields, so such an expression must be
    // parenthesized wherever a comma-separated `name = value` (a record-literal field) or another
    // comma-separated operand follows it. A binary operator, call, or bracket already closes its
    // right edge, and a pipeline stage's lambda is parenthesized as a stage, so those are left in
    // their usual form.
    private static bool EndsWithRecordUpdate(Expr expression) => expression switch
    {
        Expr.RecordUpdate => true,
        Expr.Let let => EndsWithRecordUpdate(let.Body),
        Expr.LetRecursive let => EndsWithRecordUpdate(let.Body),
        Expr.LetResult let => EndsWithRecordUpdate(let.Body),
        Expr.Lambda lambda => EndsWithRecordUpdate(lambda.Body),
        Expr.If conditional => EndsWithRecordUpdate(conditional.Else),
        Expr.Match match => match.Cases.Count > 0 && EndsWithRecordUpdate(match.Cases[^1].Body),
        Expr.Handle handle => handle.Arms.Count > 0 && EndsWithRecordUpdate(handle.Arms[^1].Body),
        _ => false,
    };

    private static bool ContainsExpression(Expr expression, Func<Expr, bool> predicate) => predicate(expression) || expression switch
    {
        Expr.Add binary => ContainsExpression(binary.Left, predicate) || ContainsExpression(binary.Right, predicate),
        Expr.Subtract binary => ContainsExpression(binary.Left, predicate) || ContainsExpression(binary.Right, predicate),
        Expr.Multiply binary => ContainsExpression(binary.Left, predicate) || ContainsExpression(binary.Right, predicate),
        Expr.Divide binary => ContainsExpression(binary.Left, predicate) || ContainsExpression(binary.Right, predicate),
        Expr.Modulo binary => ContainsExpression(binary.Left, predicate) || ContainsExpression(binary.Right, predicate),
        Expr.BitwiseAnd binary => ContainsExpression(binary.Left, predicate) || ContainsExpression(binary.Right, predicate),
        Expr.BitwiseOr binary => ContainsExpression(binary.Left, predicate) || ContainsExpression(binary.Right, predicate),
        Expr.BitwiseXor binary => ContainsExpression(binary.Left, predicate) || ContainsExpression(binary.Right, predicate),
        Expr.ShiftLeft binary => ContainsExpression(binary.Left, predicate) || ContainsExpression(binary.Right, predicate),
        Expr.ShiftRight binary => ContainsExpression(binary.Left, predicate) || ContainsExpression(binary.Right, predicate),
        Expr.BitwiseNot unary => ContainsExpression(unary.Operand, predicate),
        Expr.LogicalNot unary => ContainsExpression(unary.Operand, predicate),
        Expr.GreaterThan binary => ContainsExpression(binary.Left, predicate) || ContainsExpression(binary.Right, predicate),
        Expr.LessThan binary => ContainsExpression(binary.Left, predicate) || ContainsExpression(binary.Right, predicate),
        Expr.GreaterOrEqual binary => ContainsExpression(binary.Left, predicate) || ContainsExpression(binary.Right, predicate),
        Expr.LessOrEqual binary => ContainsExpression(binary.Left, predicate) || ContainsExpression(binary.Right, predicate),
        Expr.Equal binary => ContainsExpression(binary.Left, predicate) || ContainsExpression(binary.Right, predicate),
        Expr.NotEqual binary => ContainsExpression(binary.Left, predicate) || ContainsExpression(binary.Right, predicate),
        Expr.ResultPipe binary => ContainsExpression(binary.Left, predicate) || ContainsExpression(binary.Right, predicate),
        Expr.ResultMapErrorPipe binary => ContainsExpression(binary.Left, predicate) || ContainsExpression(binary.Right, predicate),
        Expr.Let let => ContainsExpression(let.Value, predicate) || ContainsExpression(let.Body, predicate),
        Expr.LetRecursive let => ContainsExpression(let.Value, predicate) || ContainsExpression(let.Body, predicate),
        Expr.LetResult let => ContainsExpression(let.Value, predicate) || ContainsExpression(let.Body, predicate),
        Expr.Lambda lambda => ContainsExpression(lambda.Body, predicate),
        Expr.If conditional => ContainsExpression(conditional.Cond, predicate) || ContainsExpression(conditional.Then, predicate) || ContainsExpression(conditional.Else, predicate),
        Expr.Call call => ContainsExpression(call.Func, predicate) || ContainsExpression(call.Arg, predicate),
        Expr.TupleLit tuple => tuple.Elements.Any(element => ContainsExpression(element, predicate)),
        Expr.ListLit list => list.Elements.Any(element => ContainsExpression(element, predicate)),
        Expr.Cons cons => ContainsExpression(cons.Head, predicate) || ContainsExpression(cons.Tail, predicate),
        Expr.Match match => ContainsExpression(match.Value, predicate) || match.Cases.Any(matchCase => ContainsExpression(matchCase.Body, predicate) || (matchCase.Guard is not null && ContainsExpression(matchCase.Guard, predicate))),
        Expr.Await awaitExpression => ContainsExpression(awaitExpression.Task, predicate),
        Expr.RecordLit record => record.Fields.Any(field => ContainsExpression(field.Value, predicate)),
        Expr.RecordUpdate update => ContainsExpression(update.Target, predicate) || update.Updates.Any(field => ContainsExpression(field.Value, predicate)),
        Expr.Perform perform => ContainsExpression(perform.Operation, predicate),
        Expr.Handle handle => ContainsExpression(handle.Body, predicate) || handle.Arms.Any(arm => ContainsExpression(arm.Body, predicate)),
        _ => false,
    };

    private static void WriteHandle(StringBuilder sb, Expr.Handle handle, int indent, int parentPrec, bool preferPipelines, FormattingOptions options)
    {
        var needsParens = parentPrec > PrecLetIfLambda;
        if (needsParens)
        {
            sb.Append('(');
        }

        sb.Append("handle ");
        WriteExprInline(sb, handle.Body, indent, handle.Body is Expr.Handle or Expr.Match ? PrecLetIfLambda + 1 : 0, preferPipelines, options);
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
            WriteArmBody(sb, arm.Body, indent, preferPipelines, options);
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

    private static void WriteArmBody(StringBuilder sb, Expr body, int indent, bool preferPipelines, FormattingOptions options)
    {
        bool wrap = ContainsBitwiseOr(body);
        if (IsSingleLine(body, preferPipelines))
        {
            if (wrap) sb.Append('(');
            WriteExprInline(sb, body, indent + options.IndentSize, 0, preferPipelines, options);
            if (wrap) sb.Append(')');
            sb.Append('\n');
            return;
        }

        sb.Append('\n');
        WriteIndent(sb, indent + options.IndentSize * 2, options);
        if (wrap) sb.Append('(');
        WriteExpr(sb, body, indent + options.IndentSize * 2, 0, preferPipelines, options);
        if (wrap) sb.Append(')');
        if (!EndsWithNewLine(sb, "\n")) sb.Append('\n');
    }

    private static void WritePattern(StringBuilder sb, Pattern pattern, int parentPrecedence = 0)
    {
        int precedence = pattern switch
        {
            Pattern.Or => 1,
            Pattern.As => 2,
            Pattern.Cons => 3,
            _ => 4,
        };
        bool wrap = precedence < parentPrecedence;
        if (wrap)
        {
            sb.Append('(');
        }

        WritePatternCore(sb, pattern);

        if (wrap)
        {
            sb.Append(')');
        }
    }

    private static void WritePatternCore(StringBuilder sb, Pattern pattern)
    {
        switch (pattern)
        {
            case Pattern.EmptyList: sb.Append("[]"); break;
            case Pattern.Var variable: sb.Append(variable.Name); break;
            case Pattern.Wildcard: sb.Append('_'); break;
            case Pattern.IntLit integer: sb.Append(integer.Value); break;
            case Pattern.StrLit text: sb.Append('"').Append(EscapeString(text.Value)).Append('"'); break;
            case Pattern.RuneLit rune: sb.Append(FormatRuneLiteral(rune.Value)); break;
            case Pattern.BoolLit boolean: sb.Append(boolean.Value ? "true" : "false"); break;
            case Pattern.Cons cons: WriteConsPattern(sb, cons); break;
            case Pattern.Tuple tuple: WritePatternSequence(sb, tuple.Elements, "(", ", ", ")", 0); break;
            case Pattern.Constructor constructor: WriteConstructorPattern(sb, constructor); break;
            case Pattern.Record record: WriteRecordPattern(sb, record); break;
            case Pattern.As asPattern: WriteAsPattern(sb, asPattern); break;
            case Pattern.Or orPattern: WritePatternSequence(sb, orPattern.Alternatives, "", " | ", "", 2); break;
        }
    }

    private static void WriteConsPattern(StringBuilder sb, Pattern.Cons cons)
    {
        WritePattern(sb, cons.Head, 4);
        sb.Append(" :: ");
        WritePattern(sb, cons.Tail, 3);
    }

    private static void WriteConstructorPattern(StringBuilder sb, Pattern.Constructor constructor)
    {
        sb.Append(constructor.Name);
        if (constructor.Patterns.Count > 0)
        {
            WritePatternSequence(sb, constructor.Patterns, "(", ", ", ")", 0);
        }
    }

    private static void WriteRecordPattern(StringBuilder sb, Pattern.Record record)
    {
        sb.Append(record.TypeName).Append(" { ");
        for (int i = 0; i < record.Fields.Count; i++)
        {
            if (i > 0) sb.Append(", ");
            sb.Append(record.Fields[i].Name).Append(" = ");
            WritePattern(sb, record.Fields[i].Pattern);
        }
        sb.Append(" }");
    }

    private static void WriteAsPattern(StringBuilder sb, Pattern.As asPattern)
    {
        WritePattern(sb, asPattern.Inner, 2);
        sb.Append(" as ").Append(asPattern.Name);
    }

    private static void WritePatternSequence(
        StringBuilder sb,
        IReadOnlyList<Pattern> patterns,
        string prefix,
        string separator,
        string suffix,
        int precedence)
    {
        sb.Append(prefix);
        for (int i = 0; i < patterns.Count; i++)
        {
            if (i > 0) sb.Append(separator);
            WritePattern(sb, patterns[i], precedence);
        }
        sb.Append(suffix);
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

            case Expr.RuneLit rune:
                sb.Append(FormatRuneLiteral(rune.Value));
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

    private static string FormatRuneLiteral(int value)
    {
        string body = value switch
        {
            '\\' => "\\\\",
            '\'' => "\\'",
            '\n' => "\\n",
            '\r' => "\\r",
            '\t' => "\\t",
            0 => "\\0",
            _ when value >= 0x20 && value != 0x7F => char.ConvertFromUtf32(value),
            _ => $"\\u{{{value:X}}}",
        };
        return $"'{body}'";
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
                WriteListLiteral(sb, list, indent, preferPipelines, options);
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
                WriteRecordLit(sb, rl, indent, preferPipelines, options);
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

            case Expr.LogicalNot logicalNot:
                WriteUnaryPrefixInline(sb, '!', logicalNot.Operand, indent, parentPrec, preferPipelines, options);
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
        if (c.ArgumentListLayout != CallArgumentListLayout.Inline)
        {
            WriteMultilineCall(sb, c, indent, parentPrec, preferPipelines, options);
            return;
        }

        if (preferPipelines && parentPrec == 0 && TryWritePipeline(sb, c, indent, parentPrec, preferPipelines, options))
        {
            return;
        }

        var needsParens = parentPrec > PrecCall;
        if (needsParens)
        {
            sb.Append('(');
        }

        WriteCallFunction(sb, c.Func, indent, preferPipelines, options);

        if (c.IsWhitespaceApplication)
        {
            sb.Append(' ');
            WriteExprInline(sb, c.Arg, indent, PrecCall + 1, preferPipelines, options);
        }
        else
        {
            sb.Append('(');
            int argumentPrecedence = ContainsRecordUpdate(c.Arg) ? PrecWith + 1 : 0;
            WriteExprInline(sb, c.Arg, indent, argumentPrecedence, preferPipelines, options);
            sb.Append(')');
        }

        if (needsParens)
        {
            sb.Append(')');
        }
    }

    private static void WriteMultilineCall(StringBuilder sb, Expr.Call call, int indent, int parentPrec, bool preferPipelines, FormattingOptions options)
    {
        List<Expr> reversedArguments = [];
        Expr.Call current = call;
        while (true)
        {
            reversedArguments.Add(current.Arg);
            if (current.ArgumentListLayout != CallArgumentListLayout.MultilineContinuation
                || current.Func is not Expr.Call previous)
            {
                break;
            }

            current = previous;
        }

        reversedArguments.Reverse();
        bool needsParens = parentPrec > PrecCall;
        if (needsParens)
        {
            sb.Append('(');
        }

        WriteCallFunction(sb, current.Func, indent, preferPipelines, options);
        sb.Append('(');
        sb.Append(options.NewLine);
        for (int index = 0; index < reversedArguments.Count; index++)
        {
            WriteIndent(sb, indent + options.IndentSize, options);
            bool hasFollowingArgument = index < reversedArguments.Count - 1;
            int argumentPrecedence = hasFollowingArgument && EndsWithRecordUpdate(reversedArguments[index]) ? PrecWith + 1 : 0;
            WriteExpr(sb, reversedArguments[index], indent + options.IndentSize, argumentPrecedence, preferPipelines, options);
            if (hasFollowingArgument)
            {
                sb.Append(',');
            }

            sb.Append(options.NewLine);
        }

        WriteIndent(sb, indent, options);
        sb.Append(')');
        if (needsParens)
        {
            sb.Append(')');
        }
    }

    private static void WriteCallFunction(StringBuilder sb, Expr function, int indent, bool preferPipelines, FormattingOptions options)
    {
        bool needsParens = function is Expr.Lambda or Expr.Let or Expr.LetResult or Expr.LetRecursive or Expr.If
            or Expr.Add or Expr.Subtract or Expr.Multiply or Expr.Divide or Expr.Modulo
            or Expr.BitwiseAnd or Expr.BitwiseOr or Expr.BitwiseXor or Expr.ShiftLeft or Expr.ShiftRight
            or Expr.GreaterThan or Expr.GreaterOrEqual or Expr.LessThan or Expr.LessOrEqual or Expr.Equal or Expr.NotEqual or Expr.Await or Expr.Perform or Expr.Handle;
        if (needsParens)
        {
            sb.Append('(');
        }

        WriteExprInline(sb, function, indent, PrecCall, preferPipelines, options);
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
            int elementPrecedence = ContainsRecordUpdate(elements[i]) ? PrecWith + 1 : 0;
            WriteExprInline(sb, elements[i], indent, elementPrecedence, preferPipelines, options);
        }
        sb.Append(close);
    }

    private static void WriteListLiteral(StringBuilder sb, Expr.ListLit list, int indent, bool preferPipelines, FormattingOptions options)
    {
        if (list.Elements.Count == 0 || !list.IsMultiline)
        {
            WriteDelimitedElementsInline(sb, '[', list.Elements, ']', indent, preferPipelines, options);
            return;
        }

        sb.Append('[');
        sb.Append(options.NewLine);
        for (int index = 0; index < list.Elements.Count; index++)
        {
            WriteIndent(sb, indent + options.IndentSize, options);
            bool hasFollowingElement = index < list.Elements.Count - 1;
            int elementPrecedence = hasFollowingElement && EndsWithRecordUpdate(list.Elements[index]) ? PrecWith + 1 : 0;
            WriteExpr(sb, list.Elements[index], indent + options.IndentSize, elementPrecedence, preferPipelines, options);
            if (hasFollowingElement)
            {
                sb.Append(',');
            }

            sb.Append(options.NewLine);
        }

        WriteIndent(sb, indent, options);
        sb.Append(']');
    }

    private static void WriteRecordLit(StringBuilder sb, Expr.RecordLit rl, int indent, bool preferPipelines, FormattingOptions options)
    {
        // Brace-free construction: TypeName(field = value, ...). A field value that ends in a
        // record update must keep its parentheses when another field follows: `with` takes every
        // following `name = value` pair as one of its own fields, so an unparenthesized update
        // would otherwise absorb the literal's remaining fields. The last field needs no such
        // protection — its own closing `)` already ends the update unambiguously.
        sb.Append(rl.TypeName);
        sb.Append('(');
        if (rl.IsMultiline)
        {
            sb.Append(options.NewLine);
            for (int index = 0; index < rl.Fields.Count; index++)
            {
                WriteIndent(sb, indent + options.IndentSize, options);
                sb.Append(rl.Fields[index].Name);
                sb.Append(" = ");
                bool hasFollowingField = index < rl.Fields.Count - 1;
                int fieldPrecedence = hasFollowingField && EndsWithRecordUpdate(rl.Fields[index].Value) ? PrecWith + 1 : 0;
                WriteExpr(sb, rl.Fields[index].Value, indent + options.IndentSize, fieldPrecedence, preferPipelines, options);
                if (hasFollowingField)
                {
                    sb.Append(',');
                }

                sb.Append(options.NewLine);
            }

            WriteIndent(sb, indent, options);
            sb.Append(')');
            return;
        }

        for (int i = 0; i < rl.Fields.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(", ");
            }
            sb.Append(rl.Fields[i].Name);
            sb.Append(" = ");
            bool hasFollowingField = i < rl.Fields.Count - 1;
            int fieldPrecedence = hasFollowingField && EndsWithRecordUpdate(rl.Fields[i].Value) ? PrecWith + 1 : 0;
            WriteExprInline(sb, rl.Fields[i].Value, indent, fieldPrecedence, preferPipelines, options);
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
