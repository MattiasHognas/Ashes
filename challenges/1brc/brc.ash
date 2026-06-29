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
// Input is still read whole-file with Ashes.File.readText (flaw #1 at full scale)
// and split with the pure `uncons` builtin; per-line streaming via readLine is
// now crash-free (#1b fixed) but remains unbuffered (#1).

import Ashes.IO
import Ashes.File
import Ashes.Map
import Ashes.Text
import Ashes.String
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

let rec loop remaining lineAcc map = 
    match Ashes.Text.uncons(remaining) with
        | None -> 
            if lineAcc == ""
            then map
            else processLine(lineAcc)(map)
        | Some((c, rest)) -> 
            if c == "\n"
            then loop(rest)("")(processLine(lineAcc)(map))
            else loop(rest)(lineAcc + c)(map)

let renderEntry acc key value = 
    match value with
        | (mn, mx, sm, ct) -> 
            let entry = key + "=" + fmtTenths(mn) + "/" + fmtMean(sm)(ct) + "/" + fmtTenths(mx)
            in 
                if acc == ""
                then entry
                else acc + ", " + entry

let body = 
    match Ashes.IO.args with
        | path :: _ -> 
            match Ashes.File.readText(path) with
                | Ok(text) -> text
                | Error(_) -> ""
        | [] -> ""

let final = loop(body)("")(Ashes.Map.empty)

Ashes.IO.writeLine("{" + Ashes.Map.foldLeft(renderEntry)("")(final) + "}")
