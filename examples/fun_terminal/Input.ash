import Ashes.Byte as bytes
import Ashes.Collection.List as list
import Ashes.Core.Maybe as maybe
import Ashes.Number.UInt as uint
type Event =
    | Up
    | Down
    | MouseRow(Int)
    | Quit

let byteAt data index =
    index
    |> bytes.get(data)
    |> uint.toInt

let recursive parseNumber data len index acc =
    if index >= len
    then (acc, index)
    else
        let b = byteAt(data)(index)
        in
            if b >= 48
            then
                if b <= 57
                then parseNumber(data)(len)(index + 1)(acc * 10 + b - 48)
                else (acc, index)
            else (acc, index)

let expectByte data len wanted cursor =
    match cursor with
        | (row, index) ->
            if index >= len
            then None
            else
                if byteAt(data)(index) == wanted
                then Some((row, index + 1))
                else None

let skipNumber data len cursor =
    match cursor with
        | (row, index) ->
            match parseNumber(data)(len)(index)(0) with
                | (_value, next) -> Some((row, next))

let readNumber data len cursor =
    match cursor with
        | (_row, index) ->
            0
            |> parseNumber(data)(len)(index)
            |> Some

let isMouseFinal b =
    if b == 77
    then true
    else b == 109

let finishMouse data len rowNext =
    match rowNext with
        | (row, next) ->
            if next >= len
            then None
            else
                if next
                |> byteAt(data)
                |> isMouseFinal
                then Some((row, next + 1))
                else Some((-1, next + 1))

let mouseRowAt data len index =
    Some((0, index))
    |> maybe.flatMap(skipNumber(data)(len))
    |> maybe.flatMap(expectByte(data)(len)(59))
    |> maybe.flatMap(skipNumber(data)(len))
    |> maybe.flatMap(expectByte(data)(len)(59))
    |> maybe.flatMap(readNumber(data)(len))
    |> maybe.flatMap(finishMouse(data)(len))

let isFinalByte b =
    if b >= 64
    then b <= 126
    else false

let recursive skipSequence data len index =
    if index >= len
    then None
    else
        if index
        |> byteAt(data)
        |> isFinalByte
        then Some(index + 1)
        else skipSequence(data)(len)(index + 1)

let mouseRowEvent rowNext =
    match rowNext with
        | (row, next) ->
            if row >= 0
            then (Some(MouseRow(row)), next)
            else (None, next)

let mouseEvent data len index =
    match mouseRowAt(data)(len)(index) with
        | None -> None
        | Some(rowNext) ->
            rowNext
            |> mouseRowEvent
            |> Some

let csiAt data len index =
    (let kind = byteAt(data)(index)
    in
        if kind == 65
        then Some((Some(Up), index + 1))
        else
            if kind == 66
            then Some((Some(Down), index + 1))
            else
                if kind == 60
                then mouseEvent(data)(len)(index + 1)
                else
                    match skipSequence(data)(len)(index) with
                        | None -> None
                        | Some(next) -> Some((None, next)))

let escapeAt data len index =
    if index + 1 >= len
    then None
    else
        if byteAt(data)(index + 1) == 91
        then
            if index + 2 >= len
            then None
            else csiAt(data)(len)(index + 2)
        else Some((None, index + 1))

let keyBindings = [(113, Quit), (3, Quit), (4, Quit), (119, Up), (87, Up), (115, Down), (83, Down)]

let recursive lookupKey bindings b =
    match bindings with
        | [] -> None
        | (code, event) :: rest ->
            if code == b
            then Some(event)
            else lookupKey(rest)(b)

let consMaybe maybeEvent events =
    match maybeEvent with
        | Some(event) -> event :: events
        | None -> events

let recursive scan data len index events =
    if index >= len
    then (events, index)
    else
        let b = byteAt(data)(index)
        in
            if b == 27
            then
                match escapeAt(data)(len)(index) with
                    | None -> (events, index)
                    | Some((maybeEvent, next)) ->
                        events
                        |> consMaybe(maybeEvent)
                        |> scan(data)(len)(next)
            else
                match lookupKey(keyBindings)(b) with
                    | Some(event) -> scan(data)(len)(index + 1)(event :: events)
                    | None -> scan(data)(len)(index + 1)(events)

let decode pending =
    (let data = bytes.fromText(pending)
    in
        let len = bytes.length(data)
        in
            match scan(data)(len)(0)([]) with
                | (events, leftover) -> (list.reverse(events), bytes.subText(data)(leftover)(len - leftover)))
