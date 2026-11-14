let instruction kind = IrInstruction(instruction = kind, location = None)

let forward =
    [
        0
        |> LoadEnv(0)
        |> instruction,
        8
        |> LoadMemOffset(1)(0)
        |> instruction,
        1
        |> Borrow(6)
        |> instruction,
        6
        |> StoreLocal(8)
        |> instruction,
        6
        |> IsReferenceCounted(9)
        |> instruction,
        "done"
        |> JumpIfFalse(9)
        |> instruction,
        None
        |> CopyOutArena(10)(6)(-1)(false)(IndependentClone)
        |> instruction,
        10
        |> StoreLocal(8)
        |> instruction,
        instruction(Label("done")),
        8
        |> LoadLocal(11)
        |> instruction,
        false
        |> Alloc(12)(8)
        |> instruction,
        11
        |> StoreMemOffset(12)(0)
        |> instruction
    ]

let seed = CoreLoweringState(reversedInstructions = reverse(forward), nextTemp = 100, nextLabelId = 50, freshTemps = [])

let expect condition =
    if condition
    then Unit
    else Ashes.IO.panic("tuple byte parts mirror failed")

let found state =
    borrowedStringDemotion(11)(state.reversedInstructions)([])(reverse(state.reversedInstructions))(state)

let missing state =
    match found(state) with
        | None -> true
        | _ -> false

let interrupted kind (state: CoreLoweringState) =
    match state.reversedInstructions with
        | store :: rest -> state with reversedInstructions = store :: instruction(kind) :: rest
        | [] -> state

let keepsFallback state =
    match preserveRequestedBorrowedStringParts(12)([0])([SemBytes])(reverse(state.reversedInstructions))([])(state)([]) with
        | (unchanged, borrowed) -> borrowed == [] && unchanged.nextTemp == state.nextTemp

let oneRound n =
    (let state = seed with nextTemp = 100 + n
    in
        let original =
            match found(state) with
                | Some((9, 6, "done")) -> true
                | _ -> false
        in
            let transformed =
                match preserveRequestedBorrowedStringParts(12)([0])([SemBytes])(forward)([])(state)([]) with
                    | (guarded, borrowed) ->
                        match tuplePartStores(reverse(guarded.reversedInstructions))(0)([12])([0])([])([])(reverse(guarded.reversedInstructions))(guarded)(borrowed)([])([]) with
                            | ([], (_position, 11) :: []) -> borrowed == [(11, 6)] && guarded.nextTemp == 105 + n && guarded.nextLabelId == 51 && tuplePartStoreCount(12)(0)(guarded.reversedInstructions)(0) == 1
                            | _ -> false
            in
                let unchangedScalar =
                    match preserveRequestedBorrowedStringParts(12)([0])([SemInt])(forward)([])(state)([]) with
                        | (unchanged, borrowed) -> borrowed == [] && unchanged.nextTemp == 100 + n && Ashes.Collection.List.length(unchanged.reversedInstructions) == Ashes.Collection.List.length(state.reversedInstructions)
                in
                    let twoStores =
                        emit(StoreMemOffset(12)(0)(11))(state)
                    in
                        let duplicate =
                            match preserveRequestedBorrowedStringParts(12)([0])([SemBytes])(reverse(twoStores.reversedInstructions))([])(twoStores)([]) with
                                | (unchanged, borrowed) -> borrowed == [] && unchanged.nextTemp == 100 + n
                        in
                            original && transformed && unchangedScalar && duplicate && keepsFallback(interrupted(RcDrop(6)("Bytes")(-1)(true)(false)(None))(state)) && keepsFallback(interrupted(Jump("outside"))(state)) && missing((state with freshTemps = [6])) && missing(emit(Borrow(13)(11))(state)))

let recursive rounds count =
    if count == 0
    then Ashes.IO.print("tuple byte parts mirror passed")
    else
        if oneRound(count)
        then rounds(count - 1)
        else Ashes.IO.panic("tuple byte parts mirror failed")

match Ashes.IO.args with
    | "1" :: [] -> rounds(1)
    | "1000" :: [] -> rounds(1000)
    | "10000" :: [] -> rounds(10000)
    | _ -> Ashes.IO.panic("count")
