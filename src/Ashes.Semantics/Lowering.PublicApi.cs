namespace Ashes.Semantics;

public sealed partial class Lowering
{
    /// <summary>
    /// The capability names appearing in the types of the package's exported (top-level) bindings after
    /// lowering — the static, inference-based capability audit surface. Call after
    /// <see cref="Lower(Ashes.Frontend.Program)"/>. Reads the inferred types, not
    /// the bodies, so a capability discharged by an in-body handler is correctly absent; it is
    /// over-approximation-safe in the other direction (a capability anywhere in an exported binding's
    /// resolved type is reported), which is the safe bias for a supply-chain audit.
    /// </summary>
    public IReadOnlyList<string> PublicApiCapabilities()
    {
        var names = new SortedSet<string>(StringComparer.Ordinal);
        foreach (PublicAuthorityRecord binding in CapturePublicAuthority())
        {
            names.UnionWith(binding.Capabilities);
        }

        return names.ToList();
    }

    private IReadOnlyList<PublicAuthorityRecord> CapturePublicAuthority()
    {
        var capabilitiesByBinding = new SortedDictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        foreach (HoverTypeInfo hover in _hoverTypes)
        {
            // Definitions only: a use site records that use's instantiated type, whose open row
            // has been unified with the CALLER's context and so also lists every capability the
            // caller performs — reading those would charge a helper with its callers' authority.
            if (!hover.IsDefinition || hover.Name is not { } name || !_topLevelBindingNames.Contains(name))
            {
                continue;
            }

            if (!capabilitiesByBinding.TryGetValue(name, out SortedSet<string>? capabilities))
            {
                capabilities = new SortedSet<string>(StringComparer.Ordinal);
                capabilitiesByBinding.Add(name, capabilities);
            }

            CollectCapabilityNames(hover.Type, capabilities, 0);
        }

        return [.. capabilitiesByBinding.Select(pair => new PublicAuthorityRecord(pair.Key, [.. pair.Value]))];
    }

    private void CollectCapabilityNames(TypeRef type, SortedSet<string> acc, int depth)
    {
        if (depth > 128)
        {
            return;
        }

        switch (Prune(type))
        {
            case TypeRef.TCapability cap:
                acc.Add(cap.Symbol.Name);
                foreach (var arg in cap.Args)
                {
                    CollectCapabilityNames(arg, acc, depth + 1);
                }

                break;
            case TypeRef.TFun fun:
                CollectCapabilityNames(fun.Arg, acc, depth + 1);
                CollectCapabilityNames(fun.Ret, acc, depth + 1);
                if (fun.Row is not null)
                {
                    CollectCapabilityNames(fun.Row, acc, depth + 1);
                }

                break;
            case TypeRef.TRow row:
                foreach (var cap in row.Capabilities)
                {
                    CollectCapabilityNames(cap, acc, depth + 1);
                }

                if (row.Tail is not null)
                {
                    CollectCapabilityNames(row.Tail, acc, depth + 1);
                }

                break;
            case TypeRef.TList list:
                CollectCapabilityNames(list.Element, acc, depth + 1);
                break;
            case TypeRef.TTuple tuple:
                foreach (var element in tuple.Elements)
                {
                    CollectCapabilityNames(element, acc, depth + 1);
                }

                break;
            case TypeRef.TNamedType named:
                foreach (var arg in named.TypeArgs)
                {
                    CollectCapabilityNames(arg, acc, depth + 1);
                }

                break;
            case TypeRef.TPtr ptr:
                CollectCapabilityNames(ptr.Pointee, acc, depth + 1);
                break;
            default:
                break;
        }
    }
}
