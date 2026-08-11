import * as assert from "assert";
import * as fs from "fs";
import * as path from "path";

interface ScopeCapture {
  name?: string;
}

interface GrammarPattern {
  name?: string;
  match?: string;
  begin?: string;
  beginCaptures?: Record<string, ScopeCapture>;
  captures?: Record<string, ScopeCapture>;
  patterns?: GrammarPattern[];
}

interface Grammar {
  repository: Record<string, { patterns?: GrammarPattern[] }>;
}

interface Snippet {
  prefix: string;
  body: string | string[];
}

const extensionRoot = path.resolve(__dirname, "../../..");

function readJson<T>(relativePath: string): T {
  return JSON.parse(
    fs.readFileSync(path.join(extensionRoot, relativePath), "utf8"),
  ) as T;
}

function traitPatterns(): GrammarPattern[] {
  const grammar = readJson<Grammar>("syntaxes/ashes.tmLanguage.json");
  return grammar.repository.traits.patterns ?? [];
}

function findPattern(
  predicate: (pattern: GrammarPattern) => boolean,
): GrammarPattern {
  const match = traitPatterns().find(predicate);
  assert.ok(match, "Expected trait syntax pattern to be registered");
  return match;
}

suite("Trait syntax highlighting", () => {
  test("trait and implement declarations assign dedicated scopes", () => {
    const declaration = findPattern(
      (pattern) => pattern.captures?.["1"]?.name?.includes("trait") ?? false,
    );
    const implementation = findPattern(
      (pattern) =>
        pattern.captures?.["1"]?.name?.includes("implement") ?? false,
    );

    assert.match("trait Eq(a)", new RegExp(declaration.match ?? ""));
    assert.strictEqual(
      declaration.captures?.["2"]?.name,
      "entity.name.type.trait.ashes",
    );
    assert.match("implement Eq(Int)", new RegExp(implementation.match ?? ""));
    assert.strictEqual(
      implementation.captures?.["2"]?.name,
      "entity.other.inherited-class.trait.ashes",
    );
  });

  test("requires and deriving clauses highlight trait names inside braces", () => {
    const requires = findPattern(
      (pattern) =>
        pattern.beginCaptures?.["1"]?.name?.includes("requires") ?? false,
    );
    const deriving = findPattern(
      (pattern) =>
        pattern.beginCaptures?.["1"]?.name?.includes("deriving") ?? false,
    );

    assert.match("requires {", new RegExp(requires.begin ?? ""));
    assert.match("deriving {", new RegExp(deriving.begin ?? ""));
    assert.strictEqual(
      requires.patterns?.[0]?.name,
      "entity.other.inherited-class.trait.ashes",
    );
    assert.strictEqual(
      deriving.patterns?.[0]?.name,
      "entity.other.inherited-class.trait.ashes",
    );
  });

  test("trait methods and all standard method families receive method scopes", () => {
    const methodDeclaration = findPattern(
      (pattern) =>
        pattern.captures?.["2"]?.name ===
        "entity.name.function.trait-method.ashes",
    );
    const standardMethod = findPattern(
      (pattern) =>
        pattern.captures?.["3"]?.name ===
        "entity.name.function.trait-method.ashes",
    );

    assert.match(
      "    | equal : a -> a -> Bool",
      new RegExp(methodDeclaration.match ?? ""),
    );
    for (const reference of [
      "Eq.equal",
      "Ord.compare",
      "Show.show",
      "Hash.hash",
      "Add.add",
      "Not.not",
      "BitwiseNot.bitwiseNot",
      "Ashes.Trait.Subtract.subtract",
    ]) {
      assert.match(reference, new RegExp(standardMethod.match ?? ""));
    }
  });

  test("trait snippets and braces are contributed", () => {
    const snippets = readJson<Record<string, Snippet>>("snippets/ashes.json");
    const configuration = readJson<{
      brackets: string[][];
      autoClosingPairs: Array<{ open: string; close: string }>;
    }>("language-configuration.json");

    assert.deepStrictEqual(
      Object.values(snippets).map((snippet) => snippet.prefix),
      ["trait", "implement", "requires", "deriving"],
    );
    assert.ok(
      configuration.brackets.some((pair) => pair[0] === "{" && pair[1] === "}"),
    );
    assert.ok(
      configuration.autoClosingPairs.some(
        (pair) => pair.open === "{" && pair.close === "}",
      ),
    );
  });
});
