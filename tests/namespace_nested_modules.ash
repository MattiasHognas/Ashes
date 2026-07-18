// Submodules that live under a real module (Ashes.Text.Json under Ashes.Text, Ashes.IO.Console
// under Ashes.IO) resolve as modules in whole-module, aliased, and selector-import forms alike,
// and the parent module keeps working alongside them. (Ashes.Task.Parallel nesting is covered by
// namespace_task_parallel.ash, which carries the parallel-runtime platform profile.)
// expect: 12 HI {"n":1} 9 30
import Ashes.Collection.Map as map
import Ashes.IO as io
import Ashes.IO.Console.monotonicMillis
import Ashes.Text as text
import Ashes.Text.Json as json
import Ashes.Collection.List as list
let doubled =
    list.map(given (x) -> x * 2)([1, 2, 3])

let summed =
    list.foldLeft(given (acc) ->
        given (x) -> acc + x)(0)(doubled)

let stored = map.getStr("k")(map.setStr("k")(30)(map.empty))

let jsonText =
    match json.parse("{\"n\": 1}") with
        | Error(_e) -> "bad"
        | Ok(v) -> json.stringify(v)

let clockNonNegative =
    if monotonicMillis(Unit) >= 0
    then 9
    else 0

let storedText =
    match stored with
        | None -> "none"
        | Some(v) -> text.fromInt(v)

io.print(text.fromInt(summed) + " " + text.asciiUpper("hi") + " " + jsonText + " " + text.fromInt(clockNonNegative) + " " + storedText)
