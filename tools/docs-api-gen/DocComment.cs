using System.Text;
using System.Xml;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;

namespace DocsApiGen;

/// <summary>
/// Extracts human-readable summary/param text from a symbol's XML doc comment, resolving
/// <c>&lt;inheritdoc/&gt;</c> by walking overridden and interface members.
/// </summary>
internal static class DocComment
{
    internal static string ResolveSummary(ISymbol symbol)
    {
        HashSet<ISymbol> visited = new(SymbolEqualityComparer.Default);
        ISymbol? current = symbol;
        for (int hop = 0; hop < 8 && current is not null && visited.Add(current); hop++)
        {
            string? xml = current.GetDocumentationCommentXml(expandIncludes: true);
            if (!NeedsInheritdoc(xml))
            {
                return ExtractSummary(xml);
            }

            current = FindInheritedDocSource(current);
        }

        return string.Empty;
    }

    internal static IReadOnlyList<(string Name, string Text)> ExtractParams(ISymbol symbol)
    {
        string? xml = symbol.GetDocumentationCommentXml(expandIncludes: true);
        XElement? root = ParseOrNull(xml);
        if (root is null)
        {
            return [];
        }

        List<(string, string)> result = [];
        foreach (XElement paramElement in root.Elements("param"))
        {
            string name = paramElement.Attribute("name")?.Value ?? string.Empty;
            if (name.Length > 0)
            {
                result.Add((name, RenderInline(paramElement).Trim()));
            }
        }

        return result;
    }

    private static bool NeedsInheritdoc(string? xml)
    {
        if (string.IsNullOrWhiteSpace(xml))
        {
            return true;
        }

        bool hasInheritdoc = xml.Contains("<inheritdoc", StringComparison.Ordinal);
        bool hasSummary = xml.Contains("<summary", StringComparison.Ordinal);
        return hasInheritdoc && !hasSummary;
    }

    private static ISymbol? FindInheritedDocSource(ISymbol symbol)
    {
        ISymbol? overridden = symbol switch
        {
            IMethodSymbol method => method.OverriddenMethod,
            IPropertySymbol property => property.OverriddenProperty,
            IEventSymbol evt => evt.OverriddenEvent,
            _ => null,
        };
        if (overridden is not null)
        {
            return overridden;
        }

        ISymbol? interfaceSource = FindInterfaceDocSource(symbol);
        if (interfaceSource is not null)
        {
            return interfaceSource;
        }

        if (symbol is INamedTypeSymbol { BaseType.SpecialType: SpecialType.None } withBase)
        {
            return withBase.BaseType;
        }

        if (symbol is INamedTypeSymbol { AllInterfaces: [INamedTypeSymbol firstInterface, ..] })
        {
            return firstInterface;
        }

        return null;
    }

    private static ISymbol? FindInterfaceDocSource(ISymbol symbol)
    {
        INamedTypeSymbol? containingType = symbol.ContainingType;
        if (containingType is null)
        {
            return null;
        }

        foreach (INamedTypeSymbol iface in containingType.AllInterfaces)
        {
            foreach (ISymbol member in iface.GetMembers())
            {
                ISymbol? implementation = containingType.FindImplementationForInterfaceMember(member);
                if (SymbolEqualityComparer.Default.Equals(implementation, symbol))
                {
                    return member;
                }
            }
        }

        return null;
    }

    private static string ExtractSummary(string? xml)
    {
        XElement? root = ParseOrNull(xml);
        XElement? summary = root?.Element("summary");
        return summary is null ? string.Empty : RenderInline(summary).Trim();
    }

    private static XElement? ParseOrNull(string? xml)
    {
        if (string.IsNullOrWhiteSpace(xml))
        {
            return null;
        }

        try
        {
            return XElement.Parse(xml);
        }
        catch (XmlException)
        {
            return null;
        }
    }

    private static string RenderInline(XElement element)
    {
        StringBuilder builder = new();
        foreach (XNode node in element.Nodes())
        {
            RenderNode(node, builder);
        }

        return CollapseWhitespace(builder.ToString());
    }

    private static void RenderNode(XNode node, StringBuilder builder)
    {
        if (node is XText text)
        {
            // Plain prose lands outside any backtick span, so stray '<'/'>'/'&' must be escaped
            // here — left raw, they can be misread as HTML/Vue template syntax by the docs site's
            // markdown-it-to-Vue-SFC pipeline. Code spans below are NOT escaped: backtick-wrapped
            // content is escaped by the markdown renderer itself, and escaping it here too would
            // double-escape (producing a literal "&amp;lt;" in the rendered page).
            builder.Append(EscapeHtml(text.Value));
            return;
        }

        if (node is not XElement element)
        {
            return;
        }

        switch (element.Name.LocalName)
        {
            case "see":
            case "seealso":
                builder.Append('`').Append(FormatCref(element.Attribute("cref")?.Value)).Append('`');
                break;
            case "paramref":
            case "typeparamref":
                builder.Append('`').Append(element.Attribute("name")?.Value).Append('`');
                break;
            case "c":
                builder.Append('`').Append(element.Value).Append('`');
                break;
            default:
                foreach (XNode child in element.Nodes())
                {
                    RenderNode(child, builder);
                }

                builder.Append(' ');
                break;
        }
    }

    private static string FormatCref(string? cref)
    {
        if (string.IsNullOrEmpty(cref))
        {
            return string.Empty;
        }

        string trimmed = cref.Length > 2 && cref[1] == ':' ? cref[2..] : cref;
        int parenIndex = trimmed.IndexOf('(', StringComparison.Ordinal);
        string withoutParams = parenIndex >= 0 ? trimmed[..parenIndex] : trimmed;
        int lastDot = withoutParams.LastIndexOf('.');
        return lastDot >= 0 ? withoutParams[(lastDot + 1)..] : withoutParams;
    }

    private static string EscapeHtml(string text)
    {
        return text.Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal);
    }

    private static string CollapseWhitespace(string text)
    {
        return string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }
}
