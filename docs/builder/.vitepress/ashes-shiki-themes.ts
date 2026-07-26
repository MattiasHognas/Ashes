const sharedTokenColors = {
  dark: {
    foreground: "#e1dad2",
    muted: "#8c837b",
    keyword: "#f0a064",
    operator: "#aaa097",
    string: "#b9c49b",
    constant: "#e2b875",
    type: "#efd2b4",
    function: "#d9bd9f",
    punctuation: "#978e86",
    invalid: "#e78e7e",
  },
  light: {
    foreground: "#352f2a",
    muted: "#81766c",
    keyword: "#a94d1d",
    operator: "#70665d",
    string: "#596b37",
    constant: "#92621f",
    type: "#754821",
    function: "#725438",
    punctuation: "#746a61",
    invalid: "#a53e35",
  },
} as const;

function createAshesTheme(
  name: string,
  type: "dark" | "light",
  background: string,
) {
  const colors = sharedTokenColors[type];
  return {
    name,
    type,
    colors: {
      "editor.background": background,
      "editor.foreground": colors.foreground,
    },
    tokenColors: [
      {
        scope: ["comment", "punctuation.definition.comment"],
        settings: { foreground: colors.muted, fontStyle: "italic" },
      },
      {
        scope: ["keyword", "storage", "storage.type", "storage.modifier"],
        settings: { foreground: colors.keyword },
      },
      {
        scope: ["keyword.operator"],
        settings: { foreground: colors.operator },
      },
      {
        scope: [
          "string",
          "punctuation.definition.string",
          "constant.character.escape",
        ],
        settings: { foreground: colors.string },
      },
      {
        scope: ["constant", "constant.numeric", "constant.language"],
        settings: { foreground: colors.constant },
      },
      {
        scope: [
          "entity.name.type",
          "entity.name.class",
          "entity.name.namespace",
          "support.type",
        ],
        settings: { foreground: colors.type },
      },
      {
        scope: ["entity.name.function", "support.function"],
        settings: { foreground: colors.function },
      },
      {
        scope: ["punctuation"],
        settings: { foreground: colors.punctuation },
      },
      {
        scope: ["variable", "variable.other", "variable.parameter"],
        settings: { foreground: colors.foreground },
      },
      {
        scope: ["invalid", "message.error"],
        settings: { foreground: colors.invalid },
      },
    ],
  };
}

export const ashesDarkTheme = createAshesTheme(
  "ashes-ember-dark",
  "dark",
  "#11100f",
);

export const ashesLightTheme = createAshesTheme(
  "ashes-ember-light",
  "light",
  "#fbf8f3",
);
