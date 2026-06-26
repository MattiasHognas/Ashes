#!/usr/bin/env node
// Mock compiler for integration tests.
// Handles --version and common compiler subcommands.
const fs = require("fs");
const command = process.argv[2];

switch (command) {
  case "--version":
    console.log("0.0.1");
    process.exit(0);
    break;
  case "compile":
    if (process.env.ASHES_MOCK_SENTINEL) {
      fs.writeFileSync(process.env.ASHES_MOCK_SENTINEL, "compile");
    }
    console.log("Compiled successfully");
    process.exit(0);
    break;
  case "run":
    if (process.env.ASHES_MOCK_SENTINEL) {
      fs.writeFileSync(process.env.ASHES_MOCK_SENTINEL, "run");
    }
    console.log("Running...");
    process.exit(0);
    break;
  case "test":
    if (process.env.ASHES_MOCK_SENTINEL) {
      fs.writeFileSync(process.env.ASHES_MOCK_SENTINEL, "test");
    }
    console.log("All tests passed");
    process.exit(0);
    break;
  default:
    process.exit(0);
    break;
}
