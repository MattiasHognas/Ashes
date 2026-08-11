import * as assert from "assert";
import * as fs from "fs";
import * as path from "path";
import * as oniguruma from "vscode-oniguruma";
import { INITIAL, Registry, parseRawGrammar } from "vscode-textmate";
import type { IGrammar, IToken } from "vscode-textmate";

// Real tokenization of the traits.ash fixture through the actual TextMate engine
// (vscode-textmate + its oniguruma regex backend), not just regex-matching individual
// grammar patterns against hand-picked strings the way syntaxHighlighting.test.ts does.
// This exercises pattern ordering/precedence and the fixture's full text — the kind of
// bug (e.g. one pattern shadowing another, or the trait repository never actually being
// included from the grammar's top level) a shape-only test cannot catch.

const extensionRoot = path.resolve(__dirname, "../../..");
const wasmPath = require.resolve("vscode-oniguruma/release/onig.wasm");

let grammarPromise: Promise<IGrammar> | undefined;

async function loadGrammar(): Promise<IGrammar> {
  if (!grammarPromise) {
    grammarPromise = (async () => {
      const wasmBin = fs.readFileSync(wasmPath).buffer;
      await oniguruma.loadWASM(wasmBin);
      const onigLib = Promise.resolve({
        createOnigScanner: (patterns: string[]) =>
          new oniguruma.OnigScanner(patterns),
        createOnigString: (s: string) => new oniguruma.OnigString(s),
      });

      const grammarPath = path.join(
        extensionRoot,
        "syntaxes/ashes.tmLanguage.json",
      );
      const rawGrammar = parseRawGrammar(
        fs.readFileSync(grammarPath, "utf8"),
        grammarPath,
      );

      const registry = new Registry({
        onigLib,
        loadGrammar: (scopeName: string) =>
          Promise.resolve(scopeName === "source.ashes" ? rawGrammar : null),
      });
      const grammar = await registry.loadGrammar("source.ashes");
      assert.ok(grammar, "Expected the ashes grammar to load");
      return grammar;
    })();
  }
  return grammarPromise;
}

/** Tokenizes every line of `traits.ash`, threading TextMate state across lines. */
async function tokenizeTraitsFixture(): Promise<
  { line: string; tokens: IToken[] }[]
> {
  const grammar = await loadGrammar();
  const source = fs.readFileSync(
    path.join(extensionRoot, "src/test/fixtures/traits.ash"),
    "utf8",
  );
  const lines = source.split("\n");
  let ruleStack = INITIAL;
  const result: { line: string; tokens: IToken[] }[] = [];
  for (const line of lines) {
    const tokenized = grammar.tokenizeLine(line, ruleStack);
    result.push({ line, tokens: tokenized.tokens });
    ruleStack = tokenized.ruleStack;
  }
  return result;
}

/** The scopes of the token covering `text` on the (unique) line containing it. */
function scopesFor(
  lines: { line: string; tokens: IToken[] }[],
  lineText: string,
  text: string,
): string[] {
  const entry = lines.find((l) => l.line.includes(lineText));
  assert.ok(
    entry,
    `Expected a fixture line containing ${JSON.stringify(lineText)}`,
  );
  const column = entry.line.indexOf(text, entry.line.indexOf(lineText));
  assert.ok(
    column >= 0,
    `Expected ${JSON.stringify(text)} on line ${JSON.stringify(lineText)}`,
  );
  const token = entry.tokens.find(
    (t) => column >= t.startIndex && column < t.endIndex,
  );
  assert.ok(
    token,
    `Expected a token covering column ${column} on ${JSON.stringify(lineText)}`,
  );
  return token.scopes;
}

suite("Trait fixture tokenization (real TextMate engine)", function () {
  // WASM instantiation + first grammar compile is slow under the default 2s mocha timeout.
  this.timeout(20000);

  test("trait declaration names the trait", async () => {
    const lines = await tokenizeTraitsFixture();
    assert.ok(
      scopesFor(lines, "trait Render(a)", "Render").includes(
        "entity.name.type.trait.ashes",
      ),
    );
  });

  test("implementation head names the trait as an inherited class", async () => {
    const lines = await tokenizeTraitsFixture();
    assert.ok(
      scopesFor(lines, "implement Render(Int)", "Render").includes(
        "entity.other.inherited-class.trait.ashes",
      ),
    );
  });

  test("trait method declarations and overrides get method scopes", async () => {
    const lines = await tokenizeTraitsFixture();
    assert.ok(
      scopesFor(lines, "| render : a -> Str", "render").includes(
        "entity.name.function.trait-method.ashes",
      ),
      "declared method",
    );
    assert.ok(
      scopesFor(lines, "| render =", "render").includes(
        "entity.name.function.trait-method.ashes",
      ),
      "implementation override",
    );
  });

  test("deriving clause highlights every listed trait", async () => {
    const lines = await tokenizeTraitsFixture();
    for (const traitName of ["Eq", "Show", "Hash"]) {
      assert.ok(
        scopesFor(lines, "deriving {Eq, Show, Hash}", traitName).includes(
          "entity.other.inherited-class.trait.ashes",
        ),
        `deriving entry ${traitName}`,
      );
    }
  });

  test("requires clause highlights the trait name", async () => {
    const lines = await tokenizeTraitsFixture();
    assert.ok(
      scopesFor(lines, "requires {Render(a)}", "Render").includes(
        "entity.other.inherited-class.trait.ashes",
      ),
    );
  });

  test("a standard-trait qualified method call scopes the trait and the method", async () => {
    // The qualified-call pattern matches a closed list of standard trait/method names
    // (Eq, Ord, Show, ... / equal, compare, show, ...) by design, so it never
    // false-positives on an ordinary module-qualified call like Ashes.IO.print(...) or
    // List.map(...). Eq.equal is in that list; verify it actually lights up.
    const lines = await tokenizeTraitsFixture();
    const callLine = "Eq.equal(left)(right)";
    assert.ok(
      scopesFor(lines, callLine, "Eq").includes("entity.name.type.trait.ashes"),
      "call-site trait name",
    );
    assert.ok(
      scopesFor(lines, callLine, "equal").includes(
        "entity.name.function.trait-method.ashes",
      ),
      "call-site method name",
    );
  });

  test("a user-defined trait's qualified call is not mistaken for a standard one", async () => {
    // Render/render are not in the standard trait/method name list, so this call site
    // correctly gets no trait-specific scope at all (unlike the Render trait's own
    // declaration and implementation sites, which the tests above cover) -- documenting
    // the boundary of the qualified-call pattern's intentionally closed name list.
    const lines = await tokenizeTraitsFixture();
    const callLine = "Render.render(value)";
    assert.ok(
      !scopesFor(lines, callLine, "Render").includes(
        "entity.name.type.trait.ashes",
      ),
    );
    assert.ok(
      !scopesFor(lines, callLine, "render").includes(
        "entity.name.function.trait-method.ashes",
      ),
    );
  });
});
