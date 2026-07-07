using System.Runtime.CompilerServices;

// The storage-integration tests construct the real stores and DbContext directly.
[assembly: InternalsVisibleTo("Ashes.Registry.Tests")]
