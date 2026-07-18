import Ashes.Bytes as bytes
import Ashes.List as list
import Ashes.UInt as uint
type Event =
    | Up
    | Down
    | MouseRow(Int)
    | Quit

let byteAt data index = uint.toInt(bytes.get(data)(index))

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

let mouseRowAt data len index =
    match parseNumber(data)(len)(index)(0) with
        | (_button, afterButton) ->
            if afterButton >= len
            then None
            else
                if byteAt(data)(afterButton) == 59
                then
                    match parseNumber(data)(len)(afterButton + 1)(0) with
                        | (_column, afterColumn) ->
                            if afterColumn >= len
                            then None
                            else
                                if byteAt(data)(afterColumn) == 59
                                then
                                    match parseNumber(data)(len)(afterColumn + 1)(0) with
                                        | (row, afterRow) ->
                                            if afterRow >= len
                                            then None
                                            else
                                                let final = byteAt(data)(afterRow)
                                                in
                                                    if final == 77
                                                    then Some((row, afterRow + 1))
                                                    else
                                                        if final == 109
                                                        then Some((row, afterRow + 1))
                                                        else Some((-1, afterRow + 1))
                                else None
                else None

let recursive skipSequence data len index =
    if index >= len
    then None
    else
        let b = byteAt(data)(index)
        in
            if b >= 64
            then
                if b <= 126
                then Some(index + 1)
                else skipSequence(data)(len)(index + 1)
            else skipSequence(data)(len)(index + 1)

let recursive scan data len index events =
    if index >= len
    then (events, index)
    else
        let b = byteAt(data)(index)
        in
            if b == 27
            then
                if index + 1 >= len
                then (events, index)
                else
                    if byteAt(data)(index + 1) == 91
                    then
                        if index + 2 >= len
                        then (events, index)
                        else
                            let kind = byteAt(data)(index + 2)
                            in
                                if kind == 65
                                then scan(data)(len)(index + 3)(Up :: events)
                                else
                                    if kind == 66
                                    then scan(data)(len)(index + 3)(Down :: events)
                                    else
                                        if kind == 60
                                        then
                                            match mouseRowAt(data)(len)(index + 3) with
                                                | None -> (events, index)
                                                | Some((row, next)) ->
                                                    if row >= 0
                                                    then scan(data)(len)(next)(MouseRow(row) :: events)
                                                    else scan(data)(len)(next)(events)
                                        else
                                            match skipSequence(data)(len)(index + 2) with
                                                | None -> (events, index)
                                                | Some(next) -> scan(data)(len)(next)(events)
                    else scan(data)(len)(index + 1)(events)
            else
                if b == 113
                then scan(data)(len)(index + 1)(Quit :: events)
                else
                    if b == 3
                    then scan(data)(len)(index + 1)(Quit :: events)
                    else
                        if b == 4
                        then scan(data)(len)(index + 1)(Quit :: events)
                        else
                            if b == 119
                            then scan(data)(len)(index + 1)(Up :: events)
                            else
                                if b == 87
                                then scan(data)(len)(index + 1)(Up :: events)
                                else
                                    if b == 115
                                    then scan(data)(len)(index + 1)(Down :: events)
                                    else
                                        if b == 83
                                        then scan(data)(len)(index + 1)(Down :: events)
                                        else scan(data)(len)(index + 1)(events)

let decode pending =
    (let data = bytes.fromText(pending)
    in
        let len = bytes.length(data)
        in
            match scan(data)(len)(0)([]) with
                | (events, leftover) -> (list.reverse(events), bytes.subText(data)(leftover)(len - leftover)))
