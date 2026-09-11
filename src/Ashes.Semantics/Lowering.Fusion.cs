using System.Diagnostics.CodeAnalysis;
using Ashes.Frontend;

namespace Ashes.Semantics;

public sealed partial class Lowering
{
    // Bounds the alias chase in ResolveCalleeQualifiedName: a pathologically long chain of
    // let-bound aliases-of-aliases gives up and declines rather than looping or recursing deeply.
    private const int CalleeIdentityChaseDepthLimit = 16;

    /// <summary>
    /// Resolves an arbitrary call root to the qualified source name of the declaration it names —
    /// a stdlib module member (<c>"Ashes.Collection.List.reverse"</c>) or an entry-module top-level
    /// binding — independent of whatever alias the call site actually spells. A qualified reference
    /// resolves algorithmically through <see cref="ResolveModuleAlias"/> (any module alias); a bare
    /// name resolves directly when it already IS the canonical stitched compiler name (the shape an
    /// unaliased qualified reference, or a reference from inside another stitched module's own body,
    /// takes), and otherwise chases through <see cref="_letBindingValues"/> — the import stitcher's
    /// own <c>let name = target in ...</c> aliases, and any alias the user's own source wrote, look
    /// identical here, both are just a let binding whose recorded value is resolved in turn. A
    /// shadowing binding is not a hazard: <see cref="Lookup"/> only ever returns what is actually in
    /// lexical scope at this call site, so a user's own same-named binding resolves through ITS OWN
    /// value, never the stdlib declaration it happens to share a spelling with.
    /// </summary>
    private string? ResolveCalleeQualifiedName(Expr root, int depth = 0)
    {
        if (depth > CalleeIdentityChaseDepthLimit)
        {
            return null;
        }

        switch (root)
        {
            case Expr.QualifiedVar qv:
                {
                    string resolvedModule = ResolveModuleAlias(qv.Module);
                    if (BuiltinRegistry.TryGetModule(resolvedModule, out var module)
                        && module.Members.ContainsKey(qv.Name))
                    {
                        // A compiler intrinsic, not a stitched .ash declaration — it has no
                        // qualified source name to compare against.
                        return null;
                    }

                    string compilerName = ProjectSupport.SanitizeModuleBindingName(resolvedModule) + "_" + qv.Name;
                    return _functionSourceNames is not null
                        && _functionSourceNames.TryGetValue(compilerName, out SourceFunctionName? mapped)
                            ? mapped.QualifiedName
                            : null;
                }

            case Expr.Var v:
                {
                    if (_functionSourceNames is not null
                        && _functionSourceNames.TryGetValue(v.Name, out SourceFunctionName? direct))
                    {
                        return direct.QualifiedName;
                    }

                    int? slot = Lookup(v.Name) switch
                    {
                        Binding.Local local => local.Slot,
                        Binding.Scheme scheme => scheme.Slot,
                        _ => null,
                    };
                    if (slot is int aliasSlot
                        && _letBindingValues.TryGetValue(aliasSlot, out Expr? aliasedValue)
                        && !ReferenceEquals(aliasedValue, root))
                    {
                        return ResolveCalleeQualifiedName(aliasedValue, depth + 1);
                    }

                    return null;
                }

            default:
                return null;
        }
    }

    /// <summary>
    /// Whether <paramref name="expr"/> is a saturated call — curried or constructor-shaped — whose
    /// resolved callee identity is exactly <paramref name="qualifiedName"/>, and if so its arguments
    /// in application order. Used by fusions that must recognize a specific stdlib function by what
    /// it resolves to rather than by the source text spelling the call site happens to use.
    /// </summary>
    private bool TryMatchCallToQualifiedFunction(
        Expr expr,
        string qualifiedName,
        int expectedArgCount,
        [NotNullWhen(true)] out List<Expr>? arguments)
    {
        var collected = new List<Expr>();
        Expr root = CollectCallArgs(expr, collected);
        if (collected.Count == expectedArgCount
            && string.Equals(ResolveCalleeQualifiedName(root), qualifiedName, StringComparison.Ordinal))
        {
            arguments = collected;
            return true;
        }

        arguments = null;
        return false;
    }
}
