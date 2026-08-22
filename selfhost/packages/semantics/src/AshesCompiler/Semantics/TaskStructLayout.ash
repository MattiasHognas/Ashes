// Defines the fixed scheduler/task frame layout shared by coroutine lowering and code generation.
//
// Invariants:
// - Every header word is eight bytes and captures begin at headerSize.
// - Negative state codes are stable runtime ABI values, not allocation-order identities.
// - Wait codes describe scheduler readiness independently from the task's state code.

export (
    type TaskStructLayout(..),
    type TaskStateKind(..),
    type TaskWaitKind(..),
    value taskStructLayout,
    value taskStateCode,
    value taskWaitCode,
)

type TaskStructLayout =
    | stateIndex: Int
    | coroutineFunction: Int
    | resultSlot: Int
    | awaitedTask: Int
    | nextTask: Int
    | sleepDurationMilliseconds: Int
    | ioArgument0: Int
    | ioArgument1: Int
    | waitKind: Int
    | waitHandle: Int
    | waitData0: Int
    | waitData1: Int
    | frameSizeBytes: Int
    | arenaCursor: Int
    | arenaEnd: Int
    | readyNext: Int
    | waiter: Int
    | arenaOwner: Int
    | loopResetOk: Int
    | frameDropper: Int
    | headerSize: Int
    deriving {Eq, Show}

let taskStructLayout =
    TaskStructLayout(
        stateIndex = 0,
        coroutineFunction = 8,
        resultSlot = 16,
        awaitedTask = 24,
        nextTask = 32,
        sleepDurationMilliseconds = 40,
        ioArgument0 = 48,
        ioArgument1 = 56,
        waitKind = 64,
        waitHandle = 72,
        waitData0 = 80,
        waitData1 = 88,
        frameSizeBytes = 96,
        arenaCursor = 104,
        arenaEnd = 112,
        readyNext = 120,
        waiter = 128,
        arenaOwner = 136,
        loopResetOk = 144,
        frameDropper = 152,
        headerSize = 160
    )

type TaskStateKind =
    | CompletedTaskState
    | SleepingTaskState
    | TcpConnectTaskState
    | TcpSendTaskState
    | TcpReceiveTaskState
    | TcpCloseTaskState
    | HttpGetTaskState
    | HttpPostTaskState
    | TcpListenTaskState
    | TcpAcceptTaskState
    | ForkWorkersTaskState
    | TlsConnectTaskState
    | TlsHandshakeTaskState
    | TlsSendTaskState
    | TlsReceiveTaskState
    | TlsCloseTaskState
    | TlsServerHandshakeTaskState
    | AllCompositeTaskState
    | RaceCompositeTaskState
    | ScopeCompositeTaskState
    deriving {Eq, Show}

let taskStateCode state =
    match state with
        | CompletedTaskState -> -1
        | SleepingTaskState -> -2
        | TcpConnectTaskState -> -10
        | TcpSendTaskState -> -11
        | TcpReceiveTaskState -> -12
        | TcpCloseTaskState -> -13
        | HttpGetTaskState -> -14
        | HttpPostTaskState -> -15
        | TcpListenTaskState -> -16
        | TcpAcceptTaskState -> -17
        | ForkWorkersTaskState -> -18
        | TlsConnectTaskState -> -19
        | TlsHandshakeTaskState -> -20
        | TlsSendTaskState -> -21
        | TlsReceiveTaskState -> -22
        | TlsCloseTaskState -> -23
        | TlsServerHandshakeTaskState -> -24
        | AllCompositeTaskState -> -40
        | RaceCompositeTaskState -> -41
        | ScopeCompositeTaskState -> -42

type TaskWaitKind =
    | NoTaskWait
    | SocketReadTaskWait
    | SocketWriteTaskWait
    | TlsReadTaskWait
    | TlsWriteTaskWait
    | TimerTaskWait
    deriving {Eq, Show}

let taskWaitCode wait =
    match wait with
        | NoTaskWait -> 0
        | SocketReadTaskWait -> 1
        | SocketWriteTaskWait -> 2
        | TlsReadTaskWait -> 3
        | TlsWriteTaskWait -> 4
        | TimerTaskWait -> 5
