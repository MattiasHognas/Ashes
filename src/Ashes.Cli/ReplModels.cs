internal sealed record ReplBinding(string Name, string ValueSource, bool IsRecursive);

internal sealed record ReplSubmissionAnalysis(string TypeDisplay, bool IsPrintable, string? BindingName);
