// The 2026-07 namespace restructure is a hard clean break: retired module names fail with the
// ordinary unknown-module diagnostic (which lists the current module set).
// expect-compile-error: reserved for the standard library
import Ashes.File as file
Ashes.IO.print("unreachable")
