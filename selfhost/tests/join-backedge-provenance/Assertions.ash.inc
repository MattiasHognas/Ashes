let arm temp (_fresh: Bool) state =
    matchArmResultWith(0)(temp)(0)(false)(false)(state)

let check (_unused: Int) =
    let state = CoreLoweringState(backEdgeDummyTemps = [7, 8])
    in
        // t33 is the parked recursive CallClosure result; t7/t8 are actual backedge dummies.
        let reachingCall = arm(33)(false)(state)
        in
            let freshPair = arm(172)(true)(state)
            in
                let mixed = [reachingCall, freshPair]
                in
                    let neutral = [arm(7)(true)(state), arm(8)(true)(state)]
                    in
                        let nested = recordNeutralJoin(90)(neutral)(state)
                        in
                            let mixedNested = recordNeutralJoin(91)([arm(90)(true)(nested), reachingCall])(nested)
                            in
                                let allNested = recordNeutralJoin(92)([arm(90)(true)(nested), arm(7)(true)(nested)])(nested)
                                in
                                    !reachingCall.armRetainedOwner && !armNeverReachesJoin(33)(state) && reachingCall.armStoreTemp == 33 && !joinIsNewlyProduced(mixed) && !reachingArmsAllNewlyProduced(mixed) && armNeverReachesJoin(7)(state) && armNeverReachesJoin(90)(nested) && !armNeverReachesJoin(91)(mixedNested) && armNeverReachesJoin(92)(allNested) && !armNeverReachesJoin(93)(recordNeutralJoin(93)([])(state)) && reachingArmsAllNewlyProduced([arm(90)(true)(nested), freshPair])

let recursive repeat count =
    if count <= 0
    then true
    else check(0) && repeat(count - 1)

let rounds =
    match Ashes.IO.args with
        | _ :: "10000" :: _ -> 10000
        | _ -> 1000

if repeat(rounds)
then Ashes.IO.print("ok")
else Ashes.IO.panic("join provenance failed")
