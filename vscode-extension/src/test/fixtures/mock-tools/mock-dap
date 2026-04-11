#!/usr/bin/env node
// Mock DAP server for integration tests.
// Responds to --version for tool acquisition verification.
const command = process.argv[2];

switch (command) {
  case "--version":
    console.log("0.0.1");
    process.exit(0);
    break;
  default:
    process.exit(0);
    break;
}
