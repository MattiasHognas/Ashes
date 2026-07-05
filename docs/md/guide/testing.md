# Ashes Testing

This document defines the supported surface of `ashes test` and the expected behavior of Ashes tests.
It serves as a reference for test authors and a specification for test runner implementations.

Ashes tests are ordinary `.ash` programs annotated with leading `//` comment
directives. The test runner compiles each test to a native executable, runs it,
and compares the observed result against the declared expectations.

## Discovery

Default discovery:

- `ashes test` looks for tests under `tests/`.
- Discovery is recursive.
- Only `.ash` files are included.
- Hidden directories such as `.git` and `.vscode` are ignored.
- Files are executed in lexicographic path order.

Project-aware behavior:

- If `ashes.json` is discovered by searching upward from the current directory,
	`ashes test` runs in project mode.
- `ashes test --project path/to/ashes.json` forces project mode for that project.
- In project mode, each discovered test is compiled as the entry module while
	reusing the project `sourceRoots`, `include`, and shipped standard library
	resolution.

Explicit paths:

- `ashes test some/path` runs tests only from the provided file or directory.
- If a project is active and a relative path does not exist from the current
	directory, the runner also tries resolving it relative to the project root.

## Execution Model

For each discovered test, the runner:

1. Reads the leading directive block.
2. Materializes any file fixtures into a temporary working directory.
3. Compiles the test to a native executable.
4. Runs the executable.
5. Compares the observed exit code and stdout with the directive expectations.
6. Continues to the next test even if the current test fails.

Tests run sequentially.

## Matching Rules

Stdout matching:

- The runner compares stdout after trimming trailing newlines and trailing
	whitespace on the captured output.
- The expected text from `// expect:` is also trimmed at the end.
- Matching is otherwise exact.

Examples:

- `// expect: 42` matches program output `42\n`.
- `// expect: hello world` matches only `hello world`, not `hello  world`.

Stderr behavior:

- Stderr does not participate in pass/fail matching.
- If a test fails and stderr is non-empty, stderr is appended to the rendered
	failure output for diagnostics.

Timeouts:

- The current runner has no built-in timeout.

Compile-error tests:

- `// expect-compile-error:` matches by substring containment within the
	rendered compiler diagnostic output.

## Supported Directives

Directives must appear in the leading comment block before the first
non-comment, non-empty source line.

### `// expect: ...`

Syntax:

```ash
// expect: 42
```

Meaning:

- Declares the exact expected stdout for a successful test.
- Implies default expected exit code `0` unless overridden by `// exit:`.

Examples:

```ash
// expect: ok
Ashes.IO.print("ok")
```

```ash
// expect: empty
Ashes.IO.print("empty")
```

Common mistakes:

- `// expect: empty` does not mean empty stdout. It literally expects the text
	`empty`.
- Multi-line expected output is not supported as a single directive; only the
	remainder of the directive line is captured.

### `// expect-compile-error: ...`

Syntax:

```ash
// expect-compile-error: Could not resolve module 'Missing'
```

Meaning:

- The test is expected to fail during compilation.
- The runner expects exit code `1`.
- The provided text must appear in the rendered compiler diagnostics.

Example:

```ash
// expect-compile-error: Undefined variable
Ashes.IO.print(missing)
```

Common mistakes:

- This is for compile-time failures only, not runtime panics.

### `// exit: N`

Syntax:

```ash
// exit: 1
```

Meaning:

- Overrides the expected process exit code.
- If omitted, the default is `0`.

Example:

```ash
// exit: 1
// expect: boom
Ashes.IO.panic("boom")
```

Common mistakes:

- If you expect a runtime failure, set both `// exit:` and `// expect:`.

### `// stdin: ...`

Syntax:

```ash
// stdin: hello\nworld\n
```

Meaning:

- Sends the decoded string to the test process standard input.
- Supported escapes: `\n`, `\r`, `\t`, `\\`.

Example:

```ash
// stdin: hello\n
// expect: hello
match Ashes.IO.readLine() with
		| Some(text) -> Ashes.IO.print(text)
		| None -> Ashes.IO.print("none")
```

### `// file: path = content`

Syntax:

```ash
// file: input.txt = hello
```

Meaning:

- Creates a UTF-8 text fixture file in the test working directory before
	execution.
- Paths must be relative and cannot escape the temporary test directory.

Example:

```ash
// file: data/input.txt = hello
// expect: hello
match Ashes.File.readText("data/input.txt") with
		| Ok(text) -> Ashes.IO.print(text)
		| Error(msg) -> Ashes.IO.print(msg)
```

### `// file-bytes: path = HEX HEX ...`

Syntax:

```ash
// file-bytes: bad.bin = FF FE FD
```

Meaning:

- Creates a binary fixture file using hexadecimal byte values.

Example:

```ash
// file-bytes: bad.bin = FF FE FD
// expect: Ashes.File.readText() encountered invalid UTF-8
match Ashes.File.readText("bad.bin") with
		| Ok(text) -> Ashes.IO.print(text)
		| Error(msg) -> Ashes.IO.print(msg)
```

### `// tcp-server: accept`

Syntax:

```ash
// tcp-server: accept
```

Meaning:

- Starts a loopback TCP server fixture on an ephemeral port.
- The test source may reference the placeholder `__TCP_PORT__`, which is
	substituted before compilation.

### `// tcp-expect: ...`

Syntax:

```ash
// tcp-expect: hello
```

Meaning:

- The loopback TCP fixture expects the client to send exactly the provided
	UTF-8 text.

### `// tcp-send: ...`

Syntax:

```ash
// tcp-send: hello
```

Meaning:

- The loopback TCP fixture sends the provided UTF-8 text to the client after
	accept.

## Unknown Directives

Unknown directives are currently ignored by the TestRunner.

That means:

- they do not fail the test by themselves
- they are not interpreted as supported behavior
- they should not be relied on as part of the stable tooling surface

## Formatting Interaction

- Tests may include leading `//` directives; examples should not use test
	directives.
- `ashes fmt` formats `.ash` source but preserves the leading comment block.
- `// fmt-skip: ...` is not interpreted by the TestRunner. It is used by CI and
	formatting verification scripts to exempt intentionally malformed fixtures
	from formatting checks.

## Failure Reporting

When a test fails, the runner reports:

- the file name
- expected exit code
- actual exit code
- expected output
- actual output
- stderr, when present and relevant to the failure

This keeps failures deterministic and suitable for CI output.
