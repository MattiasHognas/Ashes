// The One Billion Row Challenge (1BRC), in Ashes.
//
// Reads `Station;Temperature` lines from a file and prints, per station, the
// min/mean/max temperature sorted by station name, in the canonical
//   {Abha=-23.0/18.0/59.2, Abidjan=-16.2/26.0/67.3, ...}
// form. The file path is the first argument:
//   ./challenges/brc challenges/measurements.txt
//
// This program is a flaw-finding exercise (see challenges/FLAWS.md). Several of
// the flaws it originally exposed have since been fixed in the compiler, and this
// version uses those fixes:
//   * #4: stations are ordered by UTF-8 byte order (Ashes.Map.getStr/setStr compare keys
//     with the Ashes.Bytes.compare memcmp intrinsic), so multibyte names sort correctly
//     and never collide/merge.
//   * #6: Ashes.Map/Ashes.String are called directly inside functions.
//
// The whole file is folded in a SINGLE loop over Ashes.File.readLine, which reads one line at a
// time through a buffered module-global (constant file memory; a line straddling a 64 KiB read is
// reassembled inside readLine). Because it is one loop, the accumulator map is made unique by a
// deep copy exactly ONCE at loop entry, and the get+set on `map` are inlined into the loop body so
// the map's in-place reuse fires -- together these keep peak memory constant regardless of row
// count (a per-chunk-re-entry structure would re-deep-copy the growing tree every chunk; see FLAWS).
// Each line is parsed by integer byte index (Ashes.Bytes.indexOf/subText); ';' is ASCII so
// byte-slicing the name never splits a multibyte codepoint.

import Ashes.IO
import Ashes.File
import Ashes.List
import Ashes.Map
import Ashes.Text
import Ashes.String
import Ashes.Bytes
let absInt n = 
    if n < 0
    then -n
    else n

let fmtTenths v = 
    (let sign = 
        if v < 0
        then "-"
        else ""
    in 
        let a = absInt(v)
        in 
            let whole = a / 10
            in 
                let frac = a - whole * 10
                in sign + Ashes.Text.fromInt(whole) + "." + Ashes.Text.fromInt(frac))

let fmtMean sum count = 
    (let rounded = 
        if sum >= 0
        then (sum + count / 2) / count
        else -((-sum + count / 2) / count)
    in fmtTenths(rounded))

let parseTenths rest = 
    (let dot = Ashes.String.indexOf(rest)(".")
    in 
        if dot <= -1
        then 
            match Ashes.Text.parseInt(rest) with
                | Ok(n) -> n * 10
                | Error(_) -> 0
        else 
            let intPart = Ashes.String.substring(rest)(0)(dot)
            in 
                let fracPart = Ashes.String.substring(rest)(dot + 1)(1)
                in 
                    match Ashes.Text.parseInt(intPart + fracPart) with
                        | Ok(n) -> n
                        | Error(_) -> 0)

let updateStats existing tenths = 
    match existing with
        | (mn, mx, sm, ct) -> 
            let newMin = 
                if tenths < mn
                then tenths
                else mn
            in 
                let newMax = 
                    if tenths > mx
                    then tenths
                    else mx
                in (newMin, newMax, sm + tenths, ct + 1)

let rec streamLoop handle map = 
    match Ashes.File.readLine(handle) with
        | None -> map
        | Some(line) -> 
            let bytes = Ashes.Bytes.fromText(line)
            in 
                let sep = Ashes.Bytes.indexOf(bytes)(59)(0)
                in 
                    if sep < 0
                    then streamLoop(handle)(map)
                    else 
                        if Ashes.Bytes.subText(bytes)(0)(1) == "#"
                        then streamLoop(handle)(map)
                        else 
                            let name = Ashes.Bytes.subText(bytes)(0)(sep)
                            in 
                                let len = Ashes.Bytes.length(bytes)
                                in 
                                    let rest = Ashes.Bytes.subText(bytes)(sep + 1)(len - sep - 1)
                                    in 
                                        let tenths = parseTenths(rest)
                                        in 
                                            match Ashes.Map.getStr(name)(map) with
                                                | None -> streamLoop(handle)(Ashes.Map.setStr(name)((tenths, tenths, tenths, 1))(map))
                                                | Some(existing) -> streamLoop(handle)(Ashes.Map.setStr(name)(updateStats(existing)(tenths))(map))

let collectEntry acc key value = 
    match value with
        | (mn, mx, sm, ct) -> 
            let entry = key + "=" + fmtTenths(mn) + "/" + fmtMean(sm)(ct) + "/" + fmtTenths(mx)
            in entry :: acc

let final = 
    match Ashes.IO.args with
        | path :: _ -> 
            match Ashes.File.open(path) with
                | Error(_e) -> Ashes.Map.empty
                | Ok(handle) -> 
                    let result = streamLoop(handle)(Ashes.Map.empty)
                    in 
                        let _closed = Ashes.File.close(handle)
                        in result
        | [] -> Ashes.Map.empty

let entries = Ashes.List.reverse(Ashes.Map.foldLeft(collectEntry)([])(final))

Ashes.IO.writeLine("{" + Ashes.String.join(", ")(entries) + "}")
