---
# https://vitepress.dev/reference/default-theme-home-page
layout: home

hero:
  name: "Ashes"
  text: "A pure functional language, compiled to native binaries"
  tagline: No GC. No runtime. Just a native executable.
  image:
    src: /logo.png
    alt: Ashes
  actions:
    - theme: brand
      text: Get Started
      link: /guide/getting-started
    - theme: alt
      text: Language Reference
      link: /reference/language
    - theme: alt
      text: Standard Library
      link: /reference/standard-library

features:
  - title: Pure and immutable
    details: No mutation, no reassignment, no statements, no null. Everything is an expression; iteration is recursion and pattern matching.
  - title: Native executables
    details: Compiles .ash source straight to standalone ELF and PE binaries via LLVM for linux-x64, linux-arm64, and win-x64 — zero runtime dependencies.
  - title: Hindley-Milner types
    details: Full type inference with let-polymorphism. Annotate at module boundaries; let the compiler do the rest.
  - title: Capabilities
    details: Functions declare the operations they need — Clock, Log, State — satisfied by scoped handlers or static providers, checked at compile time.
  - title: Async and parallelism
    details: async/await lowered to state machines, structured parallelism across cores — with deterministic destruction instead of a garbage collector.
  - title: Batteries included
    details: One CLI for compile, run, test, fmt, and projects — plus a language server, debugger, and VS Code extension.
---
