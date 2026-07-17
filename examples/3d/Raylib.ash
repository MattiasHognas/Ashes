external InitWindow(Int, Int, Str) -> void = "InitWindow@$ORIGIN/../vendor/raylib/linux-x64/lib/libraylib.so"
external CloseWindow() -> void = "CloseWindow@$ORIGIN/../vendor/raylib/linux-x64/lib/libraylib.so"
external WindowShouldClose() -> Bool = "WindowShouldClose@$ORIGIN/../vendor/raylib/linux-x64/lib/libraylib.so"
external SetTargetFPS(Int) -> void = "SetTargetFPS@$ORIGIN/../vendor/raylib/linux-x64/lib/libraylib.so"
external BeginDrawing() -> void = "BeginDrawing@$ORIGIN/../vendor/raylib/linux-x64/lib/libraylib.so"
external EndDrawing() -> void = "EndDrawing@$ORIGIN/../vendor/raylib/linux-x64/lib/libraylib.so"
external rlMatrixMode(Int) -> void = "rlMatrixMode@$ORIGIN/../vendor/raylib/linux-x64/lib/libraylib.so"
external rlLoadIdentity() -> void = "rlLoadIdentity@$ORIGIN/../vendor/raylib/linux-x64/lib/libraylib.so"
external rlFrustum(Float, Float, Float, Float, Float, Float) -> void = "rlFrustum@$ORIGIN/../vendor/raylib/linux-x64/lib/libraylib.so"
external rlOrtho(Float, Float, Float, Float, Float, Float) -> void = "rlOrtho@$ORIGIN/../vendor/raylib/linux-x64/lib/libraylib.so"
external rlTranslatef(f32, f32, f32) -> void = "rlTranslatef@$ORIGIN/../vendor/raylib/linux-x64/lib/libraylib.so"
external rlRotatef(f32, f32, f32, f32) -> void = "rlRotatef@$ORIGIN/../vendor/raylib/linux-x64/lib/libraylib.so"
external rlScalef(f32, f32, f32) -> void = "rlScalef@$ORIGIN/../vendor/raylib/linux-x64/lib/libraylib.so"
external rlBegin(Int) -> void = "rlBegin@$ORIGIN/../vendor/raylib/linux-x64/lib/libraylib.so"
external rlEnd() -> void = "rlEnd@$ORIGIN/../vendor/raylib/linux-x64/lib/libraylib.so"
external rlColor4ub(u8, u8, u8, u8) -> void = "rlColor4ub@$ORIGIN/../vendor/raylib/linux-x64/lib/libraylib.so"
external rlVertex3f(f32, f32, f32) -> void = "rlVertex3f@$ORIGIN/../vendor/raylib/linux-x64/lib/libraylib.so"
external rlClearColor(u8, u8, u8, u8) -> void = "rlClearColor@$ORIGIN/../vendor/raylib/linux-x64/lib/libraylib.so"
external rlClearScreenBuffers() -> void = "rlClearScreenBuffers@$ORIGIN/../vendor/raylib/linux-x64/lib/libraylib.so"
external rlEnableDepthTest() -> void = "rlEnableDepthTest@$ORIGIN/../vendor/raylib/linux-x64/lib/libraylib.so"
external rlDisableDepthTest() -> void = "rlDisableDepthTest@$ORIGIN/../vendor/raylib/linux-x64/lib/libraylib.so"
external rlDisableBackfaceCulling() -> void = "rlDisableBackfaceCulling@$ORIGIN/../vendor/raylib/linux-x64/lib/libraylib.so"

let rlTriangles = 4

let rlModelview = 5888

let rlProjection = 5889

let vertex x y z _ = rlVertex3f x y z

let color r g b _ = rlColor4ub r g b 255u8

let tri x1 y1 z1 x2 y2 z2 x3 y3 z3 r g b _ =
    Unit
    |> color r g b
    |> vertex x1 y1 z1
    |> vertex x2 y2 z2
    |> vertex x3 y3 z3

let quad x1 y1 z1 x2 y2 z2 x3 y3 z3 x4 y4 z4 r g b _ =
    Unit
    |> tri x1 y1 z1 x2 y2 z2 x3 y3 z3 r g b
    |> tri x1 y1 z1 x3 y3 z3 x4 y4 z4 r g b

let beginTriangles _ = rlBegin(rlTriangles)

let endShape _ = rlEnd(Unit)

let loadIdentity _ = rlLoadIdentity(Unit)

let beginDrawing _ = BeginDrawing(Unit)

let endDrawing _ = EndDrawing(Unit)

let clearScreen _ = rlClearScreenBuffers(Unit)

let enableDepth _ = rlEnableDepthTest(Unit)

let disableDepth _ = rlDisableDepthTest(Unit)

let disableBackfaceCulling _ = rlDisableBackfaceCulling(Unit)

let initWindow width height title _ = InitWindow width height title

let setTargetFps fps _ = SetTargetFPS fps

let closeWindow _ = CloseWindow(Unit)

let windowShouldClose _ = WindowShouldClose(Unit)

let projectionMode _ = rlMatrixMode rlProjection

let modelviewMode _ = rlMatrixMode rlModelview

let projectionBounds _ = rlOrtho(-18.0)(18.0)(-10.125)(10.125)(-100.0)(100.0)

let cameraCenter _ = rlTranslatef(0.0)(-2.6)(0.0)

let cameraLookDown _ = rlRotatef(42.0)(1.0)(0.0)(0.0)

let cameraTurn _ = rlRotatef(-38.0)(0.0)(1.0)(0.0)

let cameraScale _ = rlScalef(0.52)(0.52)(0.52)

let skyClear _ = rlClearColor(112u8)(168u8)(210u8)(255u8)
