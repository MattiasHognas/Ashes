# Package Registry Website: Browse & Discovery UI

## Goal

A crates.io/npmjs-style front end for browsing, searching, and inspecting packages, built on top of
the registry API that already exists in `src/Ashes.Registry`.

## Why

The registry *server* is done (read API, publish pipeline, token auth, ownership, ~37 tests). The
missing half is discovery: humans need to find packages, read their docs, and see what they pull in
before running `ashes add`. This is the last piece of the package-manager story.

## Current state

`src/Ashes.Registry` is an ASP.NET Core minimal-API app with EF storage (Postgres full-text search
migration in place), a filesystem blob store, and OpenAPI/Scalar docs auto-mapped. Relevant
endpoints already exist:

- Read: `/api/v1/index`, `/packages` (list), `/search`, `/packages/{ns}`,
  `/packages/{ns}/{version}`, `/packages/{ns}/{version}/source`.
- Write: token creation, `PUT` publish, yank/unyank, owner management.

The publish pipeline already runs `CompilerCapabilityExtractor`, so the capabilities each package
requires are known server-side.

## Recommended approach

**Server-render inside `Ashes.Registry`** (Razor Pages or Blazor static SSR) rather than a separate
SPA. The UI can call `IMetadataStore` / `ISearchIndex` directly — no second HTTP hop, no CORS, no
separate JS build — keeping it one deployable in one language, consistent with the rest of the
project. Registry pages are read-mostly and benefit from shareable server-rendered URLs.

## What we should do

Pages, roughly in value order (do 1–2 first, they are ~80% of the value):

1. **Package detail** — version list, README rendered from the source archive, owners, the
   `ashes add {ns}@{version}` install snippet, and **the required capabilities surfaced prominently**
   (see below).
2. **Search results** — wired straight to `/search` (full-text index already exists).
3. **Home** — total package count, recently published, most-downloaded.
4. **Version detail** — dependencies, published date, yanked badge.
5. **(Optional, defer)** account/token management — tokens are already created via the CLI.

New work vs. reuse:

- **Reuse**: all data access (storage interfaces), search, capability extraction.
- **New**: Razor/Blazor views, a Markdown renderer for READMEs (source pulled from the archive), light
  styling, routing.

## The differentiator to lean into

Show each package's **required capabilities** (`needs Net`, `IO`, …) as a first-class element of the
package page. This is a real supply-chain/trust signal that npm and crates.io do not have, and it is
exactly on-brand for a language whose identity is compile-time-verified safety. Make it prominent, not
an afterthought.

## Watch out for

- Keep it one deployable — resist pulling in a separate SPA toolchain.
- Sanitize rendered README Markdown (untrusted, user-published content).
- The docs site uses VitePress/Vue; the registry site is a *different* app and should not be conflated
  with it.
