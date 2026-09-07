import Ashes.IO
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
            42
            |> LoadConstInt(1)
            |> makeInst,
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
            10
            |> LoadConstInt(1)
            |> makeInst,
            20
            |> LoadConstInt(2)
            |> makeInst,
            1
            |> AwaitTask(3)
            |> makeInst,
            3
            |> AddInt(4)(2)
            |> makeInst,
            4
            |> AwaitTask(5)
            |> makeInst,
            5
            |> AddInt(6)(2)
            |> makeInst,
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
            0
            |> LoadConstInt(1)
            |> makeInst,
            1
            |> StoreLocal(2)
            |> makeInst,
            makeInst(Label("loop_start")),
            2
            |> LoadLocal(3)
            |> makeInst,
            3
            |> AwaitTask(4)
            |> makeInst,
            1
            |> AddInt(5)(4)
            |> makeInst,
            5
            |> StoreLocal(2)
            |> makeInst,
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
            10
            |> LoadConstInt(1)
            |> makeInst,
            20
            |> LoadConstInt(2)
            |> makeInst,
            1
            |> AwaitTask(3)
            |> makeInst,
            3
            |> AddInt(4)(2)
            |> makeInst,
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
