namespace Ashes.Backend.Backends;

/// <summary>
/// The LLVM optimization pipeline the backend runs, mirroring clang's <c>-O</c> levels. Selected
/// through <see cref="BackendCompileOptions.OptimizationLevel"/>.
/// </summary>
public enum BackendOptimizationLevel
{
    /// <summary>No optimization; fastest to build and closest to the source, used for debug builds.</summary>
    O0,
    /// <summary>Light optimization balancing build time against runtime performance.</summary>
    O1,
    /// <summary>The default release level, applying the full standard optimization pipeline.</summary>
    O2,
    /// <summary>Most aggressive optimization, trading longer build time for peak runtime performance.</summary>
    O3,
}
