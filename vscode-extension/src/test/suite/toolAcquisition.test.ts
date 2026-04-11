import * as assert from "assert";
import * as path from "path";
import {
  getRid,
  getExecutableName,
  verifyToolVersion,
} from "../../toolAcquisition";

const mockToolsPath = path.resolve(
  __dirname,
  "../../../src/test/fixtures/mock-tools",
);

/** Platforms where getRid() is expected to return a value. */
const SUPPORTED_RIDS = ["linux-x64", "linux-arm64", "win-x64"];

suite("Tool Acquisition", () => {
  test("getRid returns a supported RID or throws on unsupported platform", () => {
    try {
      const rid = getRid();
      assert.ok(
        SUPPORTED_RIDS.includes(rid),
        `getRid() returned '${rid}' which is not in: ${SUPPORTED_RIDS.join(", ")}`,
      );
    } catch (err) {
      assert.ok(
        (err as Error).message.includes("Unsupported platform"),
        "Should throw an Unsupported platform error on unknown OS/arch",
      );
    }
  });

  test("getExecutableName appends .exe for Windows RIDs", () => {
    assert.strictEqual(getExecutableName("ashes", "win-x64"), "ashes.exe");
  });

  test("getExecutableName returns bare name for Linux RIDs", () => {
    assert.strictEqual(getExecutableName("ashes", "linux-x64"), "ashes");
    assert.strictEqual(getExecutableName("ashes", "linux-arm64"), "ashes");
  });

  test("verifyToolVersion succeeds when version matches", () => {
    const mockCompiler = path.join(mockToolsPath, "mock-compiler");
    assert.doesNotThrow(() => {
      verifyToolVersion(mockCompiler, "0.0.1", "Mock compiler");
    });
  });

  test("verifyToolVersion throws on version mismatch", () => {
    const mockCompiler = path.join(mockToolsPath, "mock-compiler");
    assert.throws(
      () => verifyToolVersion(mockCompiler, "9.9.9", "Mock compiler"),
      /version mismatch/,
    );
  });

  test("verifyToolVersion throws for non-existent executable", () => {
    assert.throws(
      () => verifyToolVersion("/nonexistent/binary", "0.0.1", "Missing tool"),
      /Failed to run/,
    );
  });

  test("verifyToolVersion reports correct display name in error", () => {
    const mockCompiler = path.join(mockToolsPath, "mock-compiler");
    try {
      verifyToolVersion(mockCompiler, "9.9.9", "My Custom Tool");
      assert.fail("Should have thrown");
    } catch (err) {
      assert.ok(
        (err as Error).message.includes("My Custom Tool"),
        "Error message should include the display name",
      );
    }
  });
});
