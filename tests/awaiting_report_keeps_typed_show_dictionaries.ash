// expect: Some(3):custom:5|Some(3):custom:false
type Label =
    | Label

implement Show(Label) =
    | show =
        given (_value) -> "custom"

let job : a -> Task(Str, Str) requires {Show(a)} =
    given (value) ->
        async(
            let render unit = Ashes.Trait.Show.show(Some(3)) + ":" + Ashes.Trait.Show.show(Label) + ":" + Ashes.Trait.Show.show(value)
            in
                match await Ashes.Task.sleep(1) with
                    | Ok(_) -> render(Unit)
                    | Error(message) -> message
        )

let read pending =
    match Ashes.Task.run(pending) with
        | Ok(text) -> text
        | Error(message) -> message

Ashes.IO.print(read(job(5)) + "|" + read(job(false)))
