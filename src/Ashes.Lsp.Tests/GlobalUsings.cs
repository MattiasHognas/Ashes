// TUnit injects these global usings from a buildTransitive *.targets file, which
// the .NET 10.0.3xx SDK evaluates after GenerateGlobalUsings — so they silently go
// missing on CI (which runs a newer SDK than typical dev boxes) and [Test] etc. fail
// to resolve. Declaring them here makes the test project self-sufficient regardless
// of SDK; on SDKs that do apply the package usings these are harmless duplicates.
global using TUnit.Assertions;
global using TUnit.Assertions.Extensions;
global using TUnit.Core;
global using static TUnit.Core.HookType;
