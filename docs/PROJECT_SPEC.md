# Ashes Project Specification (v0.x)

This document defines the `ashes.json` project file format and the rules the Ashes CLI and compiler use to discover sources, resolve imports, and choose defaults.

The goal is to keep the project model minimal, deterministic, and easy to implement.

---

## 1. File name and discovery

### 1.1 Default project file name
The default project file name is:

- `ashes.json`

### 1.2 Discovery rules
When the user runs `ashes` commands without specifying a project file:

- The CLI searches for `ashes.json` starting from the current working directory and walking up parent directories.
- The first `ashes.json` found is used.
- If none is found, the CLI behaves as "single-file mode" (existing behavior) and requires an explicit `.ash` file or `--expr`.

### 1.3 Explicit project selection
The CLI supports selecting a project file explicitly:

- `--project <path/to/ashes.json>`

The path may be relative or absolute.

---

## 2. Project file format (`ashes.json`)

### 2.1 JSON shape
`ashes.json` is a UTF-8 JSON object with the following fields:

Required:
- `entry` (string)

Optional:
- `name` (string)
- `sourceRoots` (string array)
- `include` (string array)
- `outDir` (string)
- `target` (string)
- `defaults` (object)

Unknown fields:
- Unknown fields must be ignored for forward compatibility.

### 2.2 Path resolution
All paths in `ashes.json` are resolved relative to the directory containing `ashes.json`.

Example:

If `ashes.json` is at `C:\proj\ashes.json` and contains `"entry": "src/Main.ash"`,
then the entry file path is `C:\proj\src\Main.ash`.

---

## 3. Fields

### 3.1 `entry` (required)
The entry file to compile and/or run.

- Must point to an existing `.ash` file.
- Example: `"entry": "src/Main.ash"`

### 3.2 `name` (optional)
A human-readable project name.

- Example: `"name": "hello-ashes"`

### 3.3 `sourceRoots` (optional)
A list of directories that form the primary module search roots.

- Default: `["."]`
- Example: `"sourceRoots": ["src"]`

### 3.4 `include` (optional)
Additional module search roots. Intended for:
- `lib`
- vendor folders

These are still project-local roots. Compiler-shipped libraries under the compiler installation `lib/` folder are resolved separately and do not need to be listed here.

- Default: `[]`
- Example: `"include": ["stdlib", "lib"]`

### 3.5 `outDir` (optional)
Default directory for compiler outputs.

- Default: `"out"`
- Example: `"outDir": "out"`

### 3.6 `target` (optional)
Default backend target.

- Example: `"target": "linux-x64"`

If omitted, the CLI may choose a reasonable default based on the host OS.

### 3.7 `dependencies` (optional)
A map of package names to version strings. Each key is a package identifier and each value is a SemVer-compatible version constraint.

- Default: `{}`
- Example:
```json
"dependencies": {
  "json-parser": "1.0.0",
  "http-utils": "2.3.1"
}
```

In v0.x, dependencies are recorded in the manifest but not yet resolved or fetched automatically. Future versions will add a registry, lock file, and automatic download.

### 3.8 `defaults` (optional)
A future-facing object for CLI defaults. In v0.x this is allowed but not required to be used.

Example:
```json
"defaults": {
  "optimize": true
}
```

---

## 4. Imports and Module Resolution

### 4.1 Import syntax

Imports are written in source as:

- `import Foo`
- `import Foo.Bar`

### 4.2 Module name to relative path mapping

A module name maps to a relative `.ash` file path:

- `Foo` → `Foo.ash`
- `Foo.Bar` → `Foo/Bar.ash`

Path separators are normalized for the host platform.

### 4.3 Search order

To resolve an import, the compiler searches in two stages:

1. Project-local roots: every directory in `sourceRoots`, then every directory in `include`
2. Compiler-shipped libraries in the compiler installation `lib/` folder

For each root directory, the compiler checks:

- `<root>/<modulePath>.ash`

Project-local resolution must produce exactly one match. If a module exists in multiple project roots, compilation fails with an ambiguity error. If no project-local module exists, the compiler checks the shipped `lib/` folder.

### 4.4 Ambiguity

If a module exists in multiple project roots, compilation fails.

For non-reserved modules, shipped libraries are only considered after project-local
resolution fails to find any match. Reserved `Ashes.*` standard-library modules are
compiler-provided and are not overridable by project-local modules.

### 4.5 Cycles

Import cycles are not allowed.

- The compiler must detect cycles and report a clear diagnostic, showing the cycle chain.

### 4.6 Exported values and imported names

Project modules are single-value modules.

- A module exports the final value produced by its body.
- When a module body has the shape `let name = expr in name` or `let rec name = expr in name`,
  `import Module` also brings `name` into local scope for the importing module.
- Qualified access `Module.name` resolves to that exported name.
- For multi-segment imports such as `import Foo.Bar`, short qualification `Bar.name`
  also resolves when `Bar` is the unique imported leaf module qualifier.
- If two imported modules would introduce the same unqualified exported name,
  compilation fails with an import-name collision diagnostic.
- If two imported modules would use the same short leaf qualifier, short
  qualification is ambiguous and compilation must require full qualification.

### 4.7 Reserved module names

- `Ashes` is reserved for compiler-provided standard-library modules.
- User projects cannot define or import a user module named `Ashes`.
- Compiler-shipped libraries also cannot define a top-level module named `Ashes`; `Ashes.*` remains reserved for compiler-provided standard-library modules.

### 4.8 Qualified names

- Qualified identifiers use `Module.name` syntax.
- Multi-segment module paths such as `Foo.Bar.value` are supported.
- If `Foo.Bar` is imported and `Bar` is unique among imported leaf qualifiers,
  `Bar.value` is also valid.
- Referring to a module that is not imported is a compile-time error.
- Referring to a name that the imported module does not export is a compile-time error.

---

## 5. Compilation unit and ordering

### 5.1 Unit of compilation

In project mode, the compilation unit is:

- the transitive closure of the entry file + all imported modules

### 5.2 Ordering

Source files must be processed in a deterministic order that respects imports:

- a module is processed before any module that depends on it

If a cycle exists, compilation fails.

---

## 6. CLI project management commands

### 6.1 ashes init

Creates a new Ashes project in the current directory.

- Creates `ashes.json` with `name`, `entry`, and `sourceRoots` fields.
- Creates `src/Main.ash` with a hello-world program.
- The project name defaults to the current directory name.
- Fails if `ashes.json` already exists.

### 6.2 ashes add \<package\>

Adds a dependency to the project manifest.

- Locates `ashes.json` by walking upward from the current directory.
- Adds the package name with version `"*"` to the `dependencies` map.
- Fails if no `ashes.json` is found.

---

## 7. CLI behavior in project mode

### 7.1 ashes run

- Compiles the project entry and all imports
- Runs the produced executable
- Passes command-line args after `--` to the executable

Example:

```
ashes run -- hello world
```

### 7.2 ashes compile

- Compiles the project entry and all imports
- Writes output to:
  - `<outDir>/<projectName or entryName>` by default, or
  - `-o <path>` if specified

### 7.3 ashes test

- Uses existing `.ash` test runner behavior
- Tests may still run outside project mode; v0.x does not require tests to use `ashes.json`

---

## 8. Versioning

v0.x:

- The project schema is intentionally minimal.
- Unknown fields are ignored.
- Future versions may add:
  - dependencies/packages
  - multiple entry points
  - per-target settings
  - optimization flags

No explicit version field is required in v0.x.
