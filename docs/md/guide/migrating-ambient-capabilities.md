# Migrating to Ambient-Authority Capabilities

The ambient-authority capability release makes existing runtime access visible in inferred function
types. Open-row and unannotated programs continue to infer their requirements. Only a function that
wrote an incomplete closed `needs` row needs a source change.

For example, a function that reads a file can no longer claim to be capability-free:

```ash
// Before: rejected because the closed row omits FileRead.
let load : Str -> Result(Str, Str) needs {} = given path -> Ashes.IO.File.readText(path)

// After: the authority is explicit.
let load : Str -> Result(Str, Str) needs {FileRead} = given path -> Ashes.IO.File.readText(path)
```

The same rule applies transitively through higher-order functions, trait methods, recursive helpers,
and imported package APIs. Add every capability reported by `ASH018` to the closed row, or use an open
row when the function deliberately forwards its caller's effects.

User externals default to `UnsafeFfi`. Classify a binding with an explicit closed runtime row when its
authority is known:

```ash
external readConfig(Str) -> Str needs {FileRead} = "read_config@libconfig"
external hashWord(Int) -> Int needs {} = "hash_word@libhash"
```

An empty external row is a trusted assertion about native behavior. Declared resource destructors
default to an empty row because releasing an owned handle is possession-based. Other external calls,
including resource borrows and consumes, remain `UnsafeFfi` unless explicitly classified.

Use `ashes compile ... --explain authority` to inspect inferred function rows and external
classifications. Published packages expose the same public capability set through `ashes info`.
