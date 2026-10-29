// A loop splits its input between an unannotated accumulator and an annotated one while passing a
// second unannotated parameter through unchanged; the exit tuple returns all three. The exit tuple
// holds its own copy of the accumulator.
// expect: 3 ash-2 ash-7 ash-5 | 2 | 41
type Entry =
    | label: Str
    | index: Int

let recursive splitLoop (items: List(Int)) known (conflicting: List(Int)) extra =
    match items with
        | [] -> (known, conflicting, extra)
        | x :: tail ->
            if x % 3 == 0
            then splitLoop(tail)(known)(x :: conflicting)(extra)
            else splitLoop(tail)(Entry(label = "ash-" + Ashes.Text.fromInt(x), index = x) :: known)(conflicting)(extra)

let labels (entries: List(Entry)) =
    Ashes.Collection.List.foldLeft(given (text) ->
        given (entry) -> text + " " + entry.label)("")(entries)

match splitLoop([5, 6, 7, 9, 2])([])([])(41) with
    | (known, conflicting, extra) ->
        Ashes.IO.print(Ashes.Text.fromInt(Ashes.Collection.List.length(known)) + labels(known) + " | " + Ashes.Text.fromInt(Ashes.Collection.List.length(conflicting)) + " | " + Ashes.Text.fromInt(extra))
