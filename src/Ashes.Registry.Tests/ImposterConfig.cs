using Ashes.Registry.Publish;
using Imposter.Abstractions;

// Generate an Imposter mock for the namespace-lint seam (REGISTRY_API §8, pipeline unit tests).
[assembly: GenerateImposter(typeof(IManifestValidator))]
