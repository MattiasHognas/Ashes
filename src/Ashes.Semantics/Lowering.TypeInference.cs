using System.Diagnostics;
using Ashes.Frontend;

namespace Ashes.Semantics;

public sealed partial class Lowering
{
    // ---------------- Type vars + unification ----------------

    private TypeRef NewTypeVar()
    {
        return new TypeRef.TVar(_nextTypeVar++);
    }

    // Collect the IDs of all unbound type variables in t.
    private void FtvType(TypeRef t, HashSet<int> result)
    {
        t = Prune(t);
        switch (t)
        {
            case TypeRef.TVar v:
                result.Add(v.Id);
                break;
            case TypeRef.TFun f:
                FtvType(f.Arg, result);
                FtvType(f.Ret, result);
                if (f.Row is not null)
                {
                    FtvType(f.Row, result);
                }

                break;
            case TypeRef.TRow row:
                foreach (var effect in row.Effects)
                {
                    FtvType(effect, result);
                }

                if (row.Tail is not null)
                {
                    FtvType(row.Tail, result);
                }

                break;
            case TypeRef.TEffect effect:
                foreach (var arg in effect.Args)
                {
                    FtvType(arg, result);
                }

                break;
            case TypeRef.TPtr p:
                FtvType(p.Pointee, result);
                break;
            case TypeRef.TList l:
                FtvType(l.Element, result);
                break;
            case TypeRef.TTuple tuple:
                foreach (var e in tuple.Elements)
                {
                    FtvType(e, result);
                }
                break;
            case TypeRef.TNamedType n:
                foreach (var a in n.TypeArgs)
                {
                    FtvType(a, result);
                }

                break;
        }
    }

    // Collect the IDs of all free (non-quantified) type variables across all bindings in the current scope.
    private void FtvEnv(HashSet<int> result)
    {
        // The ambient effect row is part of the environment: a row variable unified with it must
        // stay monomorphic (generalizing it would detach the binding from the effects its context
        // actually performs — the row analog of the value restriction).
        if (_ambientRow is not null)
        {
            FtvType(_ambientRow, result);
        }

        foreach (var binding in _scopes.Peek().Values)
        {
            if (binding is Binding.Scheme s)
            {
                // Free vars of a scheme are ftv(body) minus the quantified var IDs.
                var bodyFtv = new HashSet<int>();
                FtvType(s.S.Body, bodyFtv);
                foreach (var qv in s.S.Quantified)
                {
                    bodyFtv.Remove(qv.Id);
                }

                result.UnionWith(bodyFtv);
            }
            else if (binding is Binding.EnvScheme es)
            {
                AddSchemeFtv(es.S, result);
            }
            else if (binding is Binding.Intrinsic intrinsic)
            {
                AddSchemeFtv(intrinsic.S, result);
            }
            else if (binding is Binding.PreludeValue preludeValue)
            {
                AddSchemeFtv(preludeValue.S, result);
            }
            else
            {
                FtvType(binding.Type, result);
            }
        }
    }

    private void AddSchemeFtv(TypeScheme scheme, HashSet<int> result)
    {
        var bodyFtv = new HashSet<int>();
        FtvType(scheme.Body, bodyFtv);
        foreach (var qv in scheme.Quantified)
        {
            bodyFtv.Remove(qv.Id);
        }

        result.UnionWith(bodyFtv);
    }

    // Generalize t over free type variables not fixed by the current environment.
    private TypeScheme Generalize(TypeRef t)
    {
        var typeFtv = new HashSet<int>();
        FtvType(t, typeFtv);
        var envFtv = new HashSet<int>();
        FtvEnv(envFtv);
        typeFtv.ExceptWith(envFtv);
        // A type var used as a '+' operand must be Int/Float/Str (no type classes), so it cannot be
        // generalized — a single IR function can't be both AddInt and ConcatStr. Keep it monomorphic
        // so its uses resolve it; ResolveDeferredAdds defaults any that stay unbound to Int. Compare
        // by current representative, since the '+' var may have since been unified (e.g. with a
        // lambda's result var).
        typeFtv.ExceptWith(ConstrainedAddVarRepIds());

        var quantified = typeFtv
            .OrderBy(id => id)
            .Select(id => new TypeVar(id, $"t{id}"))
            .ToList();
        return new TypeScheme(quantified, t);
    }

    // Rename a scheme's quantified variables to brand-new, unbound type-variable IDs (rewriting the
    // body to match). The result is self-contained: its quantified vars are referenced nowhere else, so
    // no later unification of the original vars can leak into the scheme. Needed when a scheme is stashed
    // for reuse across independent instantiations while the type it was generalized from is still live in
    // the inference state (e.g. a top-level recursive helper whose own binding keeps its vars mutable).
    private TypeScheme FreshenScheme(TypeScheme scheme)
    {
        if (scheme.Quantified.Count == 0)
        {
            return scheme;
        }

        var subst = new Dictionary<int, TypeRef>(scheme.Quantified.Count);
        var freshQuantified = new List<TypeVar>(scheme.Quantified.Count);
        foreach (var qv in scheme.Quantified)
        {
            var fresh = (TypeRef.TVar)NewTypeVar();
            subst[qv.Id] = fresh;
            freshQuantified.Add(new TypeVar(fresh.Id, qv.Name));
        }

        return new TypeScheme(freshQuantified, ApplyInstSubst(scheme.Body, subst));
    }

    // Instantiate a scheme: replace each quantified variable with a fresh type variable.
    private TypeRef Instantiate(TypeScheme scheme)
    {
        if (scheme.Quantified.Count == 0)
        {
            return scheme.Body;
        }

        var subst = new Dictionary<int, TypeRef>(scheme.Quantified.Count);
        foreach (var tv in scheme.Quantified)
        {
            subst[tv.Id] = NewTypeVar();
        }

        return ApplyInstSubst(scheme.Body, subst);
    }

    // Apply an instantiation substitution (mapping old TVar IDs to fresh TypeRefs) to a type.
    private TypeRef ApplyInstSubst(TypeRef t, Dictionary<int, TypeRef> subst)
    {
        t = Prune(t);
        return t switch
        {
            TypeRef.TVar v => subst.TryGetValue(v.Id, out var r) ? r : t,
            TypeRef.TFun f => new TypeRef.TFun(ApplyInstSubst(f.Arg, subst), ApplyInstSubst(f.Ret, subst))
            {
                Row = f.Row is null ? null : ApplyInstSubst(f.Row, subst)
            },
            TypeRef.TRow row => new TypeRef.TRow(
                row.Effects.Select(e => (TypeRef.TEffect)ApplyInstSubst(e, subst)).ToList(),
                row.Tail is null ? null : ApplyInstSubst(row.Tail, subst)),
            TypeRef.TEffect effect => new TypeRef.TEffect(effect.Symbol, effect.Args.Select(a => ApplyInstSubst(a, subst)).ToList()),
            TypeRef.TPtr p => new TypeRef.TPtr(ApplyInstSubst(p.Pointee, subst)),
            TypeRef.TList l => new TypeRef.TList(ApplyInstSubst(l.Element, subst)),
            TypeRef.TTuple tuple => new TypeRef.TTuple(tuple.Elements.Select(e => ApplyInstSubst(e, subst)).ToList()),
            TypeRef.TNamedType n => new TypeRef.TNamedType(n.Symbol, n.TypeArgs.Select(a => ApplyInstSubst(a, subst)).ToList()),
            TypeRef.TOpaque => t,
            _ => t
        };
    }

    private TypeRef Prune(TypeRef t)
    {
        if (t is TypeRef.TVar v && _subst.TryGetValue(v.Id, out var r))
        {
            var pr = Prune(r);
            _subst[v.Id] = pr;
            return pr;
        }
        return t;
    }

    /// <summary>
    /// Unifies two type references by updating the type-variable substitution
    /// table in place. Compound types recurse into their children; open
    /// variables are bound after an occurs check to reject recursive types.
    /// </summary>
    private void Unify(TypeRef a, TypeRef b)
    {
        a = Prune(a);
        b = Prune(b);

        if (a is TypeRef.TNever || b is TypeRef.TNever)
        {
            return;
        }

        if (a.Equals(b))
        {
            return;
        }

        if (a is TypeRef.TVar va)
        {
            if (Occurs(va.Id, b))
            {
                ReportDiagnostic(0, "Occurs check failed (recursive type).");
                return;
            }
            _subst[va.Id] = b;
            return;
        }

        if (b is TypeRef.TVar vb)
        {
            Unify(b, a);
            return;
        }

        if (a is TypeRef.TFun fa && b is TypeRef.TFun fb)
        {
            Unify(fa.Arg, fb.Arg);
            Unify(fa.Ret, fb.Ret);
            if (fa.Row is not null || fb.Row is not null)
            {
                UnifyRows(fa.Row, fb.Row);
            }

            return;
        }

        if (a is TypeRef.TRow || b is TypeRef.TRow)
        {
            if (a is TypeRef.TRow && b is TypeRef.TRow)
            {
                UnifyRows(a, b);
                return;
            }

            // A row meeting a non-row is ill-kinded; fall through to the base mismatch report.
        }

        if (a is TypeRef.TPtr pa && b is TypeRef.TPtr pb)
        {
            Unify(pa.Pointee, pb.Pointee);
            return;
        }

        if (a is TypeRef.TList la && b is TypeRef.TList lb)
        {
            Unify(la.Element, lb.Element);
            return;
        }

        if (a is TypeRef.TTuple ta && b is TypeRef.TTuple tb)
        {
            if (ta.Elements.Count != tb.Elements.Count)
            {
                var tupleArityMismatch = PrettyPair(a, b);
                ReportDiagnostic(0, $"Type mismatch: {tupleArityMismatch.Left} vs {tupleArityMismatch.Right}.", DiagnosticCodes.TypeMismatch);
                return;
            }

            for (int i = 0; i < ta.Elements.Count; i++)
            {
                Unify(ta.Elements[i], tb.Elements[i]);
            }
            return;
        }

        if (a is TypeRef.TNamedType na && b is TypeRef.TNamedType nb)
        {
            if (!string.Equals(na.Symbol.Name, nb.Symbol.Name, StringComparison.Ordinal))
            {
                var namedTypeMismatch = PrettyPair(a, b);
                ReportDiagnostic(0, $"Type mismatch: {namedTypeMismatch.Left} vs {namedTypeMismatch.Right}.", DiagnosticCodes.TypeMismatch);
                return;
            }

            if (na.TypeArgs.Count != nb.TypeArgs.Count)
            {
                var namedTypeArityMismatch = PrettyPair(a, b);
                ReportDiagnostic(0, $"Type mismatch: {namedTypeArityMismatch.Left} vs {namedTypeArityMismatch.Right}.", DiagnosticCodes.TypeMismatch);
                return;
            }

            for (int i = 0; i < na.TypeArgs.Count; i++)
            {
                Unify(na.TypeArgs[i], nb.TypeArgs[i]);
            }

            return;
        }

        // base mismatch
        var typeMismatch = PrettyPair(a, b);
        ReportDiagnostic(0, $"Type mismatch: {typeMismatch.Left} vs {typeMismatch.Right}.", DiagnosticCodes.TypeMismatch);
    }

    private bool Occurs(int id, TypeRef t)
    {
        t = Prune(t);
        return t switch
        {
            TypeRef.TVar v => v.Id == id,
            TypeRef.TFun f => Occurs(id, f.Arg) || Occurs(id, f.Ret) || (f.Row is not null && Occurs(id, f.Row)),
            TypeRef.TRow row => row.Effects.Any(e => Occurs(id, e)) || (row.Tail is not null && Occurs(id, row.Tail)),
            TypeRef.TEffect effect => effect.Args.Any(arg => Occurs(id, arg)),
            TypeRef.TPtr p => Occurs(id, p.Pointee),
            TypeRef.TList l => Occurs(id, l.Element),
            TypeRef.TTuple tuple => tuple.Elements.Any(e => Occurs(id, e)),
            TypeRef.TNamedType n => n.TypeArgs.Any(a => Occurs(id, a)),
            TypeRef.TOpaque => false,
            _ => false
        };
    }

    private string Pretty(TypeRef t)
    {
        return Pretty(t, new Dictionary<int, string>(), parentPrecedence: 0);
    }

    private string Pretty(TypeRef t, Dictionary<int, string> typeVarNames, int parentPrecedence)
    {
        const int precArrow = 1;
        const int precAtom = 2;

        t = Prune(t);

        var (rendered, precedence) = t switch
        {
            TypeRef.TInt => ("Int", precAtom),
            TypeRef.TUInt { Bits: 8 } => ("u8", precAtom),
            TypeRef.TUInt { Bits: 16 } => ("u16", precAtom),
            TypeRef.TUInt { Bits: 32 } => ("u32", precAtom),
            TypeRef.TUInt { Bits: 64 } => ("u64", precAtom),
            TypeRef.TUInt u => ($"u{u.Bits}", precAtom),
            TypeRef.TFloat => ("Float", precAtom),
            TypeRef.TStr => ("Str", precAtom),
            TypeRef.TBytes => ("Bytes", precAtom),
            TypeRef.TBool => ("Bool", precAtom),
            TypeRef.TNever => ("Never", precAtom),
            TypeRef.TList l => ($"List<{Pretty(l.Element, typeVarNames, parentPrecedence: precAtom)}>", precAtom),
            TypeRef.TTuple tuple => ($"({string.Join(", ", tuple.Elements.Select(e => Pretty(e, typeVarNames, parentPrecedence: 0)))})", precAtom),
            TypeRef.TVar v => (GetTypeVarName(v.Id, typeVarNames), precAtom),
            TypeRef.TFun f => (
                $"{Pretty(f.Arg, typeVarNames, parentPrecedence: precAtom)} -> {Pretty(f.Ret, typeVarNames, parentPrecedence: precArrow)}{PrettyRowSuffix(f.Row, typeVarNames)}",
                precArrow
            ),
            TypeRef.TRow row => (PrettyRow(row, typeVarNames), precAtom),
            TypeRef.TEffect effect => (PrettyEffect(effect), precAtom),
            TypeRef.TPtr p => ($"*{Pretty(p.Pointee, typeVarNames, parentPrecedence: precAtom)}", precAtom),
            TypeRef.TNamedType n => n.TypeArgs.Count == 0
                ? (n.Symbol.Name, precAtom)
                : ($"{n.Symbol.Name}<{string.Join(", ", n.TypeArgs.Select(a => Pretty(a, typeVarNames, parentPrecedence: precAtom)))}>", precAtom),
            TypeRef.TTypeParam tp => (tp.Symbol.Name, precAtom),
            TypeRef.TOpaque opaque => (opaque.Name, precAtom),
            _ => (t.GetType().Name, precAtom)
        };

        return precedence < parentPrecedence ? $"({rendered})" : rendered;
    }

    /// <summary>
    /// Renders an arrow's effect-row suffix. Rows with no concrete effect (pure, or a bare row
    /// variable) render as nothing, keeping ordinary function types unchanged in diagnostics.
    /// </summary>
    private string PrettyRowSuffix(TypeRef? row, Dictionary<int, string> typeVarNames)
    {
        if (row is null)
        {
            return "";
        }

        var (effects, tail) = NormalizeRow(row);
        if (effects.Count == 0)
        {
            return "";
        }

        return " " + PrettyRow(new TypeRef.TRow(effects, tail), typeVarNames, includeUsesKeyword: true);
    }

    private string PrettyRow(TypeRef.TRow row, Dictionary<int, string> typeVarNames, bool includeUsesKeyword = false)
    {
        var (effects, tail) = NormalizeRow(row);
        var items = string.Join(
            ", ",
            effects
                .Select(e => e.Args.Count == 0
                    ? e.Symbol.Name
                    : $"{e.Symbol.Name}({string.Join(", ", e.Args.Select(a => Pretty(a, typeVarNames, parentPrecedence: 0)))})")
                .OrderBy(n => n, StringComparer.Ordinal));
        var rendered = tail is null
            ? $"{{{items}}}"
            : $"{{{items} | {GetTypeVarName(tail.Id, typeVarNames)}}}";
        return includeUsesKeyword ? $"uses {rendered}" : rendered;
    }

    private void RecordExprHoverType(Expr expr, TypeRef type)
    {
        RecordHoverType(GetSpan(expr), null, type);
    }

    private void RecordHoverType(TextSpan span, string? name, TypeRef type)
    {
        if (!IsValidSpan(span))
        {
            return;
        }

        _hoverTypes.Add(new HoverTypeInfo(span, name, type));
    }

    private static bool IsValidSpan(TextSpan span)
    {
        return span.Start >= 0 && span.End >= span.Start;
    }

    private static bool ContainsPosition(TextSpan span, int position)
    {
        if (span.Start == span.End)
        {
            return position == span.Start;
        }

        return position >= span.Start && position < span.End;
    }

    private static bool IsBetterHoverCandidate(HoverTypeInfo candidate, HoverTypeInfo current)
    {
        var candidateWidth = candidate.Span.End - candidate.Span.Start;
        var currentWidth = current.Span.End - current.Span.Start;

        if (candidateWidth != currentWidth)
        {
            return candidateWidth < currentWidth;
        }

        var candidateHasName = !string.IsNullOrEmpty(candidate.Name);
        var currentHasName = !string.IsNullOrEmpty(current.Name);
        if (candidateHasName != currentHasName)
        {
            return candidateHasName;
        }

        return candidate.Span.Start >= current.Span.Start;
    }

    private (string Left, string Right) PrettyPair(TypeRef left, TypeRef right)
    {
        var typeVarNames = new Dictionary<int, string>();
        return (
            Pretty(left, typeVarNames, parentPrecedence: 0),
            Pretty(right, typeVarNames, parentPrecedence: 0)
        );
    }

    private static string GetTypeVarName(int id, Dictionary<int, string> typeVarNames)
    {
        if (typeVarNames.TryGetValue(id, out var existing))
        {
            return existing;
        }

        var index = typeVarNames.Count;
        var typeVarName = "";
        do
        {
            // Generate a, b, ..., z, aa, ab, ... using spreadsheet-style base-26 naming.
            typeVarName = (char)('a' + (index % 26)) + typeVarName;
            index = (index / 26) - 1;
        } while (index >= 0);

        typeVarNames[id] = typeVarName;
        return typeVarName;
    }
}
