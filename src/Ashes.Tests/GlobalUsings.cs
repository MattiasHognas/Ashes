// TUnit and Verify inject these global usings from buildTransitive *.targets files,
// which the .NET 10.0.3xx SDK evaluates after GenerateGlobalUsings — so they silently
// go missing on CI (which runs a newer SDK than typical dev boxes) and [Test] /
// VerifySettings etc. fail to resolve. Declaring them here makes the test project
// self-sufficient regardless of SDK; on SDKs that do apply the package usings these
// are harmless duplicates.
global using TUnit;
global using TUnit.Assertions;
global using TUnit.Assertions.Extensions;
global using TUnit.Core;
global using VerifyTests;
global using VerifyTUnit;
global using static TUnit.Core.HookType;
global using static VerifyTUnit.Verifier;
