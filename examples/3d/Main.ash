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

let vertex x y z = rlVertex3f(x)(y)(z)

let tri x1 y1 z1 x2 y2 z2 x3 y3 z3 r g b =
    (let _color = rlColor4ub(r)(g)(b)(255u8)
    in
        let _a = vertex(x1)(y1)(z1)
        in
            let _b = vertex(x2)(y2)(z2)
            in
                let _c = vertex(x3)(y3)(z3)
                in Unit)

let quad x1 y1 z1 x2 y2 z2 x3 y3 z3 x4 y4 z4 r g b =
    (let _a = tri(x1)(y1)(z1)(x2)(y2)(z2)(x3)(y3)(z3)(r)(g)(b)
    in tri(x1)(y1)(z1)(x3)(y3)(z3)(x4)(y4)(z4)(r)(g)(b))

let drawTerrain _ =
    (let _begin = rlBegin(rlTriangles)
    in
        let _grass = quad(-14.0)(0.0)(-12.0)(14.0)(0.0)(-12.0)(14.0)(0.0)(12.0)(-14.0)(0.0)(12.0)(56u8)(124u8)(74u8)
        in
            let _grassShade = tri(-14.0)(0.02)(-12.0)(14.0)(0.02)(-12.0)(-14.0)(0.02)(12.0)(46u8)(106u8)(66u8)
            in
                let _water0 = quad(-4.0)(0.06)(-12.0)(-1.6)(0.06)(-12.0)(-3.0)(0.06)(-6.2)(-5.5)(0.06)(-6.2)(49u8)(142u8)(179u8)
                in
                    let _water1 = quad(-5.5)(0.06)(-6.2)(-3.0)(0.06)(-6.2)(-0.6)(0.06)(-1.2)(-3.2)(0.06)(-1.2)(61u8)(158u8)(195u8)
                    in
                        let _water2 = quad(-3.2)(0.06)(-1.2)(-0.6)(0.06)(-1.2)(2.7)(0.06)(4.2)(0.2)(0.06)(4.2)(45u8)(139u8)(179u8)
                        in
                            let _water3 = quad(0.2)(0.06)(4.2)(2.7)(0.06)(4.2)(1.2)(0.06)(12.0)(-1.4)(0.06)(12.0)(70u8)(174u8)(210u8)
                            in
                                let _end = rlEnd(Unit)
                                in Unit)

let drawMountains _ =
    (let _begin = rlBegin(rlTriangles)
    in
        let _m0Front = tri(-11.0)(0.0)(-5.5)(-5.0)(0.0)(-5.5)(-8.0)(5.4)(-2.2)(84u8)(96u8)(102u8)
        in
            let _m0Right = tri(-5.0)(0.0)(-5.5)(-5.0)(0.0)(1.0)(-8.0)(5.4)(-2.2)(96u8)(110u8)(118u8)
            in
                let _m0Back = tri(-5.0)(0.0)(1.0)(-11.0)(0.0)(1.0)(-8.0)(5.4)(-2.2)(72u8)(84u8)(92u8)
                in
                    let _m0Left = tri(-11.0)(0.0)(1.0)(-11.0)(0.0)(-5.5)(-8.0)(5.4)(-2.2)(108u8)(122u8)(128u8)
                    in
                        let _snow0 = tri(-8.0)(5.4)(-2.2)(-7.2)(3.7)(-3.1)(-8.9)(3.7)(-3.0)(188u8)(197u8)(202u8)
                        in
                            let _m1Front = tri(2.6)(0.0)(-7.0)(9.4)(0.0)(-7.0)(6.2)(4.8)(-3.6)(78u8)(90u8)(98u8)
                            in
                                let _m1Right = tri(9.4)(0.0)(-7.0)(9.6)(0.0)(-0.4)(6.2)(4.8)(-3.6)(96u8)(109u8)(116u8)
                                in
                                    let _m1Back = tri(9.6)(0.0)(-0.4)(2.8)(0.0)(-0.4)(6.2)(4.8)(-3.6)(68u8)(80u8)(88u8)
                                    in
                                        let _m1Left = tri(2.8)(0.0)(-0.4)(2.6)(0.0)(-7.0)(6.2)(4.8)(-3.6)(116u8)(128u8)(132u8)
                                        in
                                            let _snow1 = tri(6.2)(4.8)(-3.6)(7.1)(3.2)(-4.4)(5.4)(3.2)(-4.5)(182u8)(192u8)(198u8)
                                            in
                                                let _m2Front = tri(-2.6)(0.0)(4.8)(2.6)(0.0)(4.8)(0.0)(3.6)(7.2)(82u8)(96u8)(102u8)
                                                in
                                                    let _m2Right = tri(2.6)(0.0)(4.8)(2.8)(0.0)(9.4)(0.0)(3.6)(7.2)(100u8)(114u8)(120u8)
                                                    in
                                                        let _m2Back = tri(2.8)(0.0)(9.4)(-2.8)(0.0)(9.4)(0.0)(3.6)(7.2)(70u8)(82u8)(90u8)
                                                        in
                                                            let _m2Left = tri(-2.8)(0.0)(9.4)(-2.6)(0.0)(4.8)(0.0)(3.6)(7.2)(112u8)(126u8)(130u8)
                                                            in
                                                                let _end = rlEnd(Unit)
                                                                in Unit)

let setupCamera _ =
    (let _projection = rlMatrixMode(rlProjection)
    in
        let _projectionIdentity = rlLoadIdentity(Unit)
        in
            let _projectionBounds = rlOrtho(-18.0)(18.0)(-10.125)(10.125)(-100.0)(100.0)
            in
                let _modelview = rlMatrixMode(rlModelview)
                in
                    let _modelviewIdentity = rlLoadIdentity(Unit)
                    in
                        let _center = rlTranslatef(0.0)(-2.6)(0.0)
                        in
                            let _lookDown = rlRotatef(42.0)(1.0)(0.0)(0.0)
                            in
                                let _turn = rlRotatef(-38.0)(0.0)(1.0)(0.0)
                                in rlScalef(0.52)(0.52)(0.52))

let renderFrame _ =
    (let _begin = BeginDrawing(Unit)
    in
        let _clearColor = rlClearColor(112u8)(168u8)(210u8)(255u8)
        in
            let _clear = rlClearScreenBuffers(Unit)
            in
                let _depth = rlEnableDepthTest(Unit)
                in
                    let _culling = rlDisableBackfaceCulling(Unit)
                    in
                        let _camera = setupCamera(Unit)
                        in
                            let _terrain = drawTerrain(Unit)
                            in
                                let _mountains = drawMountains(Unit)
                                in EndDrawing(Unit))

let recursive loop _ =
    if WindowShouldClose(Unit)
    then Unit
    else
        let _frame = renderFrame(Unit)
        in loop(Unit)

let _window = InitWindow(960)(540)("Ashes + raylib terrain")
in
    let _fps = SetTargetFPS(60)
    in
        let _loop = loop(Unit)
        in CloseWindow(Unit)
