import Raylib
import Scene
let drawTerrain _ =
    Unit
    |> beginTriangles
    |> grass
    |> grassShade
    |> water0
    |> water1
    |> water2
    |> water3
    |> endShape

let drawMountains _ =
    Unit
    |> beginTriangles
    |> mountain0Front
    |> mountain0Right
    |> mountain0Back
    |> mountain0Left
    |> mountain1Front
    |> mountain1Right
    |> mountain1Back
    |> mountain1Left
    |> mountain2Front
    |> mountain2Right
    |> mountain2Back
    |> mountain2Left
    |> endShape
    |> disableDepth
    |> beginTriangles
    |> snow0
    |> snow1
    |> endShape
    |> enableDepth

let setupCamera _ =
    Unit
    |> projectionMode
    |> loadIdentity
    |> projectionBounds
    |> modelviewMode
    |> loadIdentity
    |> cameraCenter
    |> cameraLookDown
    |> cameraTurn
    |> cameraScale

let renderFrame _ =
    Unit
    |> beginDrawing
    |> skyClear
    |> clearScreen
    |> enableDepth
    |> disableBackfaceCulling
    |> setupCamera
    |> drawTerrain
    |> drawMountains
    |> endDrawing

let recursive loop _ =
    if windowShouldClose Unit
    then Unit
    else
        Unit
        |> renderFrame
        |> loop

Unit
|> initWindow 960 540 "Ashes + raylib terrain"
|> setTargetFps 60
|> loop
|> closeWindow
