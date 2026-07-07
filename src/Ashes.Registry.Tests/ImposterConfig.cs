using Ashes.Registry.Publish;
using Imposter.Abstractions;

// Generate an Imposter mock for the namespace-lint seam.
[assembly: GenerateImposter(typeof(IManifestValidator))]
