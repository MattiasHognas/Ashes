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
//   * #4: stations are ordered with Ashes.String.compare (a correct UTF-8 total
//     order), so multibyte names sort correctly and never collide/merge -- the
//     90-line hand-rolled ASCII comparator this file used to carry is gone.
//   * #6: Ashes.Map/Ashes.String are called directly inside functions; the local-
//     alias workaround they used to need is gone.
// Input is streamed in fixed-size chunks with Ashes.File.open / readChunk / close (constant
// file-memory). Each chunk is scanned by an integer byte index (Ashes.Bytes.indexOf finds the
// delimiters, Ashes.Bytes.subText slices out a line) rather than a shrinking Str view, so the
// per-line fold loop carries only copy-type args and the Ashes.Map accumulator's in-place reuse
// fires. A line may straddle a chunk boundary, so scanChunk carries the trailing partial into the
// next chunk.

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

let processLine line map = 
    (let sep = Ashes.String.indexOf(line)(";")
    in 
        if sep <= -1
        then map
        else 
            if Ashes.String.substring(line)(0)(1) == "#"
            then map
            else 
                let name = Ashes.String.substring(line)(0)(sep)
                in 
                    let rest = Ashes.String.substring(line)(sep + 1)(64)
                    in 
                        let tenths = parseTenths(rest)
                        in 
                            match Ashes.Map.get(Ashes.String.compare)(name)(map) with
                                | None -> Ashes.Map.set(Ashes.String.compare)(name)((tenths, tenths, tenths, 1))(map)
                                | Some(existing) -> Ashes.Map.set(Ashes.String.compare)(name)(updateStats(existing)(tenths))(map))

let scanChunk chunk carry map = 
    (let bytes = Ashes.Bytes.fromText(chunk)
    in 
        let len = Ashes.Bytes.length(bytes)
        in 
            let nl0 = Ashes.Bytes.indexOf(bytes)(10)(0)
            in 
                if nl0 < 0
                then (carry + chunk, map)
                else 
                    let firstLine = carry + Ashes.Bytes.subText(bytes)(0)(nl0)
                    in 
                        let map1 = processLine(firstLine)(map)
                        in 
                            let rec go pos m = 
                                let nl = Ashes.Bytes.indexOf(bytes)(10)(pos)
                                in 
                                    if nl < 0
                                    then (Ashes.Bytes.subText(bytes)(pos)(len - pos), m)
                                    else 
                                        let sep = Ashes.Bytes.indexOf(bytes)(59)(pos)
                                        in 
                                            if sep < 0
                                            then go(nl + 1)(m)
                                            else 
                                                if sep >= nl
                                                then go(nl + 1)(m)
                                                else 
                                                    if Ashes.Bytes.subText(bytes)(pos)(1) == "#"
                                                    then go(nl + 1)(m)
                                                    else 
                                                        let name = Ashes.Bytes.subText(bytes)(pos)(sep - pos)
                                                        in 
                                                            let rest = Ashes.Bytes.subText(bytes)(sep + 1)(nl - sep - 1)
                                                            in 
                                                                let tenths = parseTenths(rest)
                                                                in 
                                                                    match Ashes.Map.get(Ashes.String.compare)(name)(m) with
                                                                        | None -> go(nl + 1)(Ashes.Map.set(Ashes.String.compare)(name)((tenths, tenths, tenths, 1))(m))
                                                                        | Some(existing) -> go(nl + 1)(Ashes.Map.set(Ashes.String.compare)(name)(updateStats(existing)(tenths))(m))
                            in go(nl0 + 1)(map1))

let rec streamLoop handle lineAcc map = 
    match Ashes.File.readChunk(handle)(65536) with
        | Error(_e) -> 
            if lineAcc == ""
            then map
            else processLine(lineAcc)(map)
        | Ok(chunk) -> 
            if chunk == ""
            then 
                if lineAcc == ""
                then map
                else processLine(lineAcc)(map)
            else 
                match scanChunk(chunk)(lineAcc)(map) with
                    | (nextLineAcc, nextMap) -> streamLoop(handle)(nextLineAcc)(nextMap)

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
                    let result = streamLoop(handle)("")(Ashes.Map.empty)
                    in 
                        let _closed = Ashes.File.close(handle)
                        in result
        | [] -> Ashes.Map.empty

let entries = Ashes.List.reverse(Ashes.Map.foldLeft(collectEntry)([])(final))

Ashes.IO.writeLine("{" + Ashes.String.join(", ")(entries) + "}")
