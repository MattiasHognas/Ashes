# Raylib 3D Terrain Experiment

This is a small Ashes FFI experiment against the official raylib 6.0 Linux x64
shared library vendored under `vendor/raylib/linux-x64/lib`.

Current Ashes external declarations support primitives, strings, opaque handles,
and pointers. Raylib's usual 3D API passes structs such as `Vector3`, `Color`,
and `Camera3D` by value, so this example uses raylib's lower-level `rlgl`
functions instead. That keeps the call boundary primitive-only.

Expected run shape:

```sh
cd examples/3d
dotnet run --project ../../src/Ashes.Cli -- compile --project ashes.json
./out/raylib-3d-terrain
```

The source imports raylib through an `$ORIGIN`-relative path, so the compiled
binary can find the vendored shared library from `out/` without `LD_LIBRARY_PATH`.
Use `compile` rather than `run`: `run` executes a temporary binary under
`/tmp/ashes`, where `$ORIGIN` no longer points at this example directory.
Running still requires a graphical X11/Wayland session that raylib/GLFW can open.
