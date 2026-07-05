import { createRequire } from "node:module";
import { defineConfig } from "vitepress";
import ashesGrammar from "../../../vscode-extension/syntaxes/ashes.tmLanguage.json";

const require = createRequire(import.meta.url);

// https://vitepress.dev/reference/site-config
export default defineConfig({
  srcDir: "../md",
  outDir: "../site",
  cleanUrls: true,
  lastUpdated: true,
  title: "Ashes",
  description:
    "A pure functional ML-family language compiled to standalone native executables",
  head: [["link", { rel: "icon", type: "image/png", href: "/logo.png" }]],
  // Default to the dark (Catppuccin Mocha) look; the toggle stays available.
  appearance: "dark",

  // The content lives outside the builder root (../md), so Vue imports generated
  // for each page cannot be resolved by walking up from the .md files — pin them
  // to the builder's own node_modules.
  vite: {
    resolve: {
      alias: [
        { find: /^vue$/, replacement: require.resolve("vue") },
        {
          find: /^vue\/server-renderer$/,
          replacement: require.resolve("vue/server-renderer"),
        },
      ],
    },
  },

  markdown: {
    // The single source of truth for Ashes highlighting is the VS Code
    // extension's TextMate grammar — referenced, never copied, so editor and
    // site can never drift.
    languages: [
      {
        ...ashesGrammar,
        name: "ashes",
        aliases: ["ash"],
      },
    ],
    theme: {
      light: "catppuccin-latte",
      dark: "catppuccin-mocha",
    },
    // Render ```mermaid fences through the <Mermaid> theme component
    // (client-side, theme-aware) instead of highlighting them as code.
    config: (md) => {
      const defaultFence = md.renderer.rules.fence!;
      md.renderer.rules.fence = (tokens, idx, options, env, self) => {
        const token = tokens[idx];
        if (token.info.trim() === "mermaid") {
          return `<Mermaid code="${encodeURIComponent(token.content)}" />`;
        }
        return defaultFence(tokens, idx, options, env, self);
      };
    },
  },

  themeConfig: {
    // https://vitepress.dev/reference/default-theme-config
    logo: "/logo.png",
    outline: [2, 3],

    search: {
      provider: "local",
    },

    nav: [
      { text: "Guide", link: "/guide/getting-started" },
      { text: "Reference", link: "/reference/language" },
      { text: "Internals", link: "/internals/architecture" },
    ],

    sidebar: [
      {
        text: "Guide",
        items: [
          { text: "Getting Started", link: "/guide/getting-started" },
          { text: "Projects", link: "/guide/projects" },
          { text: "Testing", link: "/guide/testing" },
          { text: "Debugging", link: "/guide/debugging" },
          { text: "Development", link: "/guide/development" },
          { text: "Local CI/CD", link: "/guide/local-ci" },
        ],
      },
      {
        text: "Reference",
        items: [
          { text: "Language Specification", link: "/reference/language" },
          { text: "Standard Library", link: "/reference/standard-library" },
          { text: "Compiler CLI", link: "/reference/cli" },
          { text: "Formatter", link: "/reference/formatter" },
          { text: "Diagnostics", link: "/reference/diagnostics" },
        ],
      },
      {
        text: "Internals",
        items: [
          { text: "Compiler Architecture", link: "/internals/architecture" },
          { text: "IR Reference", link: "/internals/ir" },
        ],
      },
      {
        text: "Future Designs",
        collapsed: true,
        items: [
          { text: "Future Features", link: "/future/FUTURE_FEATURES" },
          { text: "Package Manager", link: "/future/PACKAGE_MANAGER" },
          { text: "Registry API", link: "/future/REGISTRY_API" },
          { text: "Server Support", link: "/future/SERVER_SUPPORT" },
          { text: "Self-Hosting", link: "/future/SELF_HOSTING" },
          {
            text: "Compiler Optimization",
            link: "/future/COMPILER_OPTIMIZATION",
          },
          { text: "Documentation Site", link: "/future/DOCS_SITE" },
        ],
      },
    ],

    socialLinks: [
      { icon: "github", link: "https://github.com/MattiasHognas/Ashes" },
    ],
  },
});
