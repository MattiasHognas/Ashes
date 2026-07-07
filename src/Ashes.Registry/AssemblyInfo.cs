using System.Runtime.CompilerServices;

// The storage-integration tests (REGISTRY_API §8) construct the real stores and DbContext directly.
[assembly: InternalsVisibleTo("Ashes.Registry.Tests")]
