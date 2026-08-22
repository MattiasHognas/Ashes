import Ashes.Collection.List.append
import Ashes.Test as test
import AshesCompiler.Semantics.CoroutineFrame
import AshesCompiler.Semantics.IrInstructions
import AshesCompiler.Semantics.StateMachineTransform
export (
    value runStateMachineTransformTests,
)

let makeInst kind = IrInstruction(instruction = kind, location = None)

let expectSingleStateTransform unit =
    (let body =
        [
            makeInst(LoadConstInt(1)(42)),
            makeInst(Return(1))
        ]
    in
        let result = transformStateMachine(body)(0)
        in
            let _ = test.assertEqual(1)(result.stateCount)
            in
                let _ = test.assertEqual(160)(result.stateStructSize)
                in Unit)

let expectMultiStateTransform unit =
    (let body =
        [
            makeInst(LoadConstInt(1)(10)),
            makeInst(LoadConstInt(2)(20)),
            makeInst(AwaitTask(3)(1)),
            makeInst(AddInt(4)(2)(3)),
            makeInst(AwaitTask(5)(4)),
            makeInst(AddInt(6)(2)(5)),
            makeInst(Return(6))
        ]
    in
        let awaitPos = findAwaitPositions(body)
        in
            let _ = test.assertEqual([2, 4])(awaitPos)
            in
                let result = transformStateMachine(body)(0)
                in
                    let _ = test.assertEqual(3)(result.stateCount)
                    in
                        let _ = test.assertEqual(true)(result.stateStructSize >= 168)
                        in Unit)

let expectBackEdgeLoopLiveLocals unit =
    (let body =
        [
            makeInst(LoadConstInt(1)(0)),
            makeInst(StoreLocal(2)(1)),
            makeInst(Label("loop_start")),
            makeInst(LoadLocal(3)(2)),
            makeInst(AwaitTask(4)(3)),
            makeInst(AddInt(5)(4)(1)),
            makeInst(StoreLocal(2)(5)),
            makeInst(Jump("loop_start")),
            makeInst(Return(5))
        ]
    in
        let awaitPos = findAwaitPositions(body)
        in
            let liveLocals = computeLiveLocalsAcrossAwaits(body)(awaitPos)(true)
            in
                let _ = test.assertEqual([[2]])(liveLocals)
                in
                    let result = transformStateMachine(body)(0)
                    in
                        let _ = test.assertEqual(2)(result.stateCount)
                        in Unit)

let expectCoroutineFrameRepresentation unit =
    (let body =
        [
            makeInst(LoadConstInt(1)(10)),
            makeInst(LoadConstInt(2)(20)),
            makeInst(AwaitTask(3)(1)),
            makeInst(AddInt(4)(2)(3)),
            makeInst(Return(4))
        ]
    in
        let smResult = transformStateMachine(body)(1)
        in
            let rep = buildCoroutineRepresentationRecord("coro_test")(smResult)([10])(body)
            in
                let _ = test.assertEqual("coro_test")(rep.coroutineLabel)
                in
                    let _ = test.assertEqual(2)(rep.stateCount)
                    in
                        let _ = test.assertEqual(1)(rep.captureCount)
                        in Unit)

let runStateMachineTransformTests unit =
    (let _ = expectSingleStateTransform(unit)
    in
        let _ = expectMultiStateTransform(unit)
        in
            let _ = expectBackEdgeLoopLiveLocals(unit)
            in
                let _ = expectCoroutineFrameRepresentation(unit)
                in Ashes.IO.print("all self-hosted state machine transform tests passed"))
