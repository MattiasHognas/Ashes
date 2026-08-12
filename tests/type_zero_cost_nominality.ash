// expect-compile-error: ASH002

type UserId = UserId(Int)

let needsUserId (value: UserId) = value

needsUserId(42)
