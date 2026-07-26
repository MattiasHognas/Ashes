# Package Registry Website: Follow-up Work

The public package-discovery website has shipped. Its current architecture and behavior are documented
in the [package registry section of the architecture reference](../internals/architecture.md#package-registry).
This page tracks deliberately deferred product work rather than the implemented contract.

## Current foundation

- Vue 3 and TypeScript SPA under `src/Ashes.Registry/Web`, built with Vite.
- Automatic frontend build and publish through `Ashes.Registry.csproj`.
- Same-origin hosting from the ASP.NET Core registry process.
- Home, browse/search, and package/version routes.
- Sanitized version-specific README rendering.
- Prominent capability metadata, direct dependencies, owners, hashes, source size, and release history.
- Copyable `ashes add <namespace>` command and immutable source downloads.

The website is public and read-first. Publishing, yanking, token creation, and owner management remain
CLI/API workflows.

## Follow-up priorities

1. Make capability extraction tri-state so the UI can distinguish verified-empty capabilities from an
   audit that could not be completed.
2. Add structured `license`, `repository`, `homepage`, and documentation metadata to published packages.
3. Define and implement download-count semantics before displaying popularity rankings.
4. Add server-generated package metadata for search crawlers and social link previews if public web
   discovery warrants it.
5. Design authenticated owner workflows only after the public registry has a production identity model;
   the self-host token-bootstrap endpoint is not that model.

## Design invariants

- Keep the SPA and API in one deployable and on one origin.
- Treat `/api/v1` as the shared contract for the SPA, CLI, and third-party clients.
- Keep capability requirements prominent, but describe them as compiler-inferred public API metadata,
  not as a generic security scan.
- Sanitize all publisher-controlled Markdown.
- Do not display popularity, verification, or trust claims that the registry cannot substantiate.
- Keep the registry website independent from the VitePress documentation application.
