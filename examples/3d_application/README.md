# Raylib 3D Terrain Experiment

This is a small Ashes FFI experiment against the official raylib 6.0 Linux x64
shared library vendored under `vendor/raylib/linux-x64/lib`.

Raylib's usual 3D API passes structs such as `Vector3`, `Color`, and `Camera3D`
by value, so this example uses raylib's lower-level `rlgl` functions instead.
That keeps the drawing boundary primitive-only.

The example also declares raylib's `LoadedFileText` as an affine external
resource. `LoadFileText` returns an owned value and `UnloadFileText` is its
destructor, so `Main.ash` loads `Scene.ash` and lets deterministic scope cleanup
release it. No manual unload call is needed; an explicit unload would use the
same `consume` contract and prevent a second automatic cleanup.

Expected run shape:

```sh
cd examples/3d_application
dotnet run --project ../../src/Ashes.Cli -- compile --project ashes.json
./out/raylib-3d-terrain
```

The source imports raylib through an `$ORIGIN`-relative path, so the compiled
binary can find the vendored shared library from `out/` without `LD_LIBRARY_PATH`.
Use `compile` rather than `run`: `run` executes a temporary binary under
`/tmp/ashes`, where `$ORIGIN` no longer points at this example directory.
Running still requires a graphical X11/Wayland session that raylib/GLFW can open.

## Run the tests

```sh
cd examples/3d_application
dotnet run --project ../../src/Ashes.Cli -- test --project ashes-test.json
```
