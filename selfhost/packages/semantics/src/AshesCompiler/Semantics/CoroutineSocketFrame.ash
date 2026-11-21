// A directly owned socket may stay in a task frame until teardown when all of
// its uses are confined to tasks awaited inside that frame. In particular a
// returned socket, callback or unawaited task must keep its original lifetime.
import Ashes.Collection.List.append
import AshesCompiler.Frontend.Syntax.Expr
import AshesCompiler.Semantics.ExprMentions.exprReadsName
import AshesCompiler.Semantics.Ir.IrFunction
import AshesCompiler.Semantics.IrInstructions
import AshesCompiler.Semantics.FunctionOrigins.createCoroutineFrameDropperOrigin
export (
    value socketStaysInTask,
    value makeSocketFrameDropper,
)

type SocketUse =
    | CapturedSocket
    | LocalTask
    | LocalTaskFactory

let recursive inner expr =
    match expr with
        | ExprNode(_, value) -> inner(value)
        | ExprAt(_, value) -> inner(value)
        | other -> other

let recursive callParts expr args =
    match inner(expr) with
        | ExprCall(function, argument, _, _) -> callParts(function)(argument :: args)
        | root -> (root, args)

let named name value =
    match inner(value) with
        | ExprVar(actual) -> name == actual
        | _ -> false

let recursive noneRead name values =
    match values with
        | [] -> true
        | value :: rest -> !exprReadsName(name)(value) && noneRead(name)(rest)

let tcpOperation moduleName member = moduleName == "Ashes.Net.Tcp" && (member == "send" || member == "receive" || member == "close") || moduleName == "Ashes.Net.Tcp.Server" && member == "accept"

let recursive waited mode name expr =
    if !exprReadsName(name)(expr)
    then true
    else
        match (mode, callParts(expr)([])) with
            | (CapturedSocket, (ExprQualifiedVar(moduleName, member), first :: rest)) -> tcpOperation(moduleName)(member) && named(name)(first) && noneRead(name)(rest)
            | (LocalTask, (ExprVar(local), [])) -> local == name
            | (LocalTaskFactory, (ExprVar(local), first :: rest)) -> local == name && noneRead(name)(first :: rest)
            | (_, (ExprQualifiedVar("Ashes.Task", member), children :: [])) ->
                match inner(children) with
                    | ExprList(entries, _) -> (member == "race" || member == "all") && waitedList(mode)(name)(entries)
                    | _ -> false
            | _ -> false
and waitedList mode name entries =
    match entries with
        | [] -> true
        | head :: rest -> waited(mode)(name)(head) && waitedList(mode)(name)(rest)

let asyncRoot root =
    match root with
        | ExprVar("async") -> true
        | ExprQualifiedVar("Ashes.Task", "task") -> true
        | _ -> false

let recursive confined mode name expr =
    if !exprReadsName(name)(expr)
    then true
    else
        match inner(expr) with
            | ExprAwait(operand) -> waited(mode)(name)(operand)
            | ExprIf(cond, yes, no) -> confined(mode)(name)(cond) && confined(mode)(name)(yes) && confined(mode)(name)(no)
            | ExprMatch(value, arms, _) -> confined(mode)(name)(value) && confinedArms(mode)(name)(arms)
            | ExprLet(local, value, body, _, _, _) ->
                local != name && local != "async" && confined(mode)(name)(body) && (confined(mode)(name)(value) || taskDefinition(mode)(name)(value) && confined(taskUseOf(value))(local)(body))
            | ExprLetResult(local, value, body) -> local != name && confined(mode)(name)(value) && confined(mode)(name)(body)
            | _ -> false
and confinedArms mode name arms =
    match arms with
        | [] -> true
        | (_, body, guard) :: rest ->
            let safeGuard =
                match guard with
                    | Some(condition) -> confined(mode)(name)(condition)
                    | None -> true
            in safeGuard && confined(mode)(name)(body) && confinedArms(mode)(name)(rest)
and taskDefinition mode name value =
    match inner(value) with
        | ExprLambda(parameter, body, _) -> parameter != name && parameter != "async" && taskDefinition(mode)(name)(body)
        | other ->
            match callParts(other)([]) with
                | (root, body :: []) -> asyncRoot(root) && confined(mode)(name)(body)
                | _ -> false
and taskUseOf value =
    match inner(value) with
        | ExprLambda(_, _, _) -> LocalTaskFactory
        | _ -> LocalTask

// The coroutine lambda wraps its source body in Ok. Only the source body decides
// whether the socket or a task using it can escape.
let socketStaysInTask name wrapped =
    match callParts(wrapped)([]) with
        | (ExprVar("Ok"), body :: []) -> confined(CapturedSocket)(name)(body)
        | _ -> false

let inst kind = IrInstruction(instruction = kind, location = None)

let recursive releaseSlots offsets temp command =
    match offsets with
        | [] -> [inst(Return(2))]
        | offset :: rest ->
            let next = "socket_next_" + Ashes.Text.fromInt(offset)
            in
                let skip =
                    if command
                    then "disarm_" + next
                    else next
                in
                    let check =
                        if command
                        then
                            [5
                            |> CmpIntEq(temp + 1)(temp)
                            |> inst, skip
                            |> JumpIfFalse(temp + 1)
                            |> inst]
                        else
                            [3
                            |> CmpIntNe(temp + 1)(temp)
                            |> inst, skip
                            |> JumpIfFalse(temp + 1)
                            |> inst]
                    in
                        let drop =
                            if command
                            then []
                            else
                                [None
                                |> CleanupResource(temp)("Socket")
                                |> inst]
                        in
                            command
                            |> releaseSlots(rest)(temp + 2)
                            |> append([3
                            |> StoreMemOffset(0)(offset)
                            |> inst, inst(Label(skip))])
                            |> append(drop)
                            |> append(check)
                            |> append([offset
                            |> LoadMemOffset(temp)(0)
                            |> inst])

let makeSocketFrameDropper parent offsets =
    (let label = parent + "_frame_drop"
    in
        let instructions =
            [
                0
                |> LoadLocal(0)
                |> inst,
                1
                |> LoadLocal(1)
                |> inst,
                0
                |> LoadConstInt(2)
                |> inst,
                -1
                |> LoadConstInt(3)
                |> inst,
                2
                |> CmpIntNe(4)(1)
                |> inst,
                "frame_teardown"
                |> JumpIfFalse(4)
                |> inst,
                0
                |> LoadMemOffset(5)(1)
                |> inst
            ]
        in
            IrFunction(
                label = label,
                instructions = false
                |> releaseSlots(offsets)(6 + 2 * Ashes.Collection.List.length(offsets))
                |> append([inst(Label("frame_teardown"))])
                |> append(releaseSlots(offsets)(6)(true))
                |> append(instructions),
                localCount = 2,
                tempCount = 6 + 4 * Ashes.Collection.List.length(offsets),
                hasEnvAndArgParams = true,
                coroutine = None,
                localNames = [],
                localTypes = [],
                origin = parent
                |> createCoroutineFrameDropperOrigin(label)
                |> Some,
                lifetimesPlaced = true
            ))
