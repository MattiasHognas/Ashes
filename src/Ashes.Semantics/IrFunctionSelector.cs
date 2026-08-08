namespace Ashes.Semantics;

/// <summary>
/// Matches a function against a user-supplied selector, shared by every facility that reports on
/// compilation so one selector means the same thing everywhere.
/// </summary>
/// <remarks>
/// Matching is by case-insensitive substring across every name a function is known by, including the
/// source function a generated one came from. A caller naming <c>loop</c> therefore also selects the
/// reuse specializations, droppers, and coroutines generated for it — nobody should need to know a
/// generated label to ask about their own code.
/// </remarks>
internal static class IrFunctionSelector
{
    /// <summary>Whether a source-level function is selected. A null selector selects everything.</summary>
    internal static bool MatchesSource(SourceFunctionOrigin? origin, string function, string? filter)
        => filter is null
            || Contains(function, filter)
            || Contains(origin?.SourceName, filter)
            || Contains(origin?.QualifiedName, filter);

    /// <summary>Whether a generated function is selected. A null selector selects everything.</summary>
    internal static bool Matches(IrFunctionOrigin? origin, string label, string? filter)
        => filter is null
            || Contains(label, filter)
            || Contains(origin?.GeneratedLabel, filter)
            || Contains(origin?.Source?.SourceName, filter)
            || Contains(origin?.Source?.QualifiedName, filter)
            || Contains(origin?.ParentGeneratedLabel, filter);

    private static bool Contains(string? candidate, string filter)
        => !string.IsNullOrEmpty(candidate)
            && candidate.Contains(filter, StringComparison.OrdinalIgnoreCase);
}
