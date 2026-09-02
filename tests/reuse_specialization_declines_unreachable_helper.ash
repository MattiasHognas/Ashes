// expect: 2
import Ashes.Collection.List
type Live =
    | lb: Int
    | li: Bool
    | lo: Bool
    deriving {Eq}

let recursive anyLive (xs: List(Int)) (region: List(Int)) (liveness: List(Live)) =
    match xs with
        | [] -> false
        | x :: rest ->
            if x > 5
            then true
            else anyLive(rest)(region)(liveness)

let recursive livenessStep (blocks: List(Int)) (region: List(Int)) (facts: List(Int)) (liveness: List(Live)) (remaining: List(Int)) =
    match remaining with
        | [] -> []
        | blockIndex :: rest ->
            match blocks with
                | b :: _ ->
                    liveness
                    |> anyLive([b])(region)
                    |> (given (liveOut) -> Live(lb = blockIndex, li = liveOut || blockIndex > 0, lo = liveOut))
                    |> (given (entry) -> entry :: livenessStep(blocks)(region)(facts)(liveness)(rest))
                | [] -> livenessStep(blocks)(region)(facts)(liveness)(rest)

let recursive livenessFixpoint (blocks: List(Int)) (region: List(Int)) (facts: List(Int)) (liveness: List(Live)) =
    (let next = livenessStep(blocks)(region)(facts)(liveness)(region)
    in
        if next == liveness
        then next
        else livenessFixpoint(blocks)(region)(facts)(next))

let recursive initialLive (region: List(Int)) =
    match region with
        | [] -> []
        | b :: rest -> Live(lb = b, li = false, lo = false) :: initialLive(rest)

let place (region: List(Int)) =
    region
    |> initialLive
    |> livenessFixpoint([1])(region)([])

[0, 1]
|> place
|> length
|> Ashes.IO.print
