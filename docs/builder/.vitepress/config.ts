import { readFileSync } from "node:fs";
import { createRequire } from "node:module";
import { fileURLToPath } from "node:url";
import { slugify } from "@mdit-vue/shared";
import { defineConfig, type DefaultTheme } from "vitepress";
import ashesGrammar from "../../../vscode-extension/syntaxes/ashes.tmLanguage.json";

const require = createRequire(import.meta.url);

// Deployment base path. GitHub Pages project sites live under /<repo>/, so the
// deploy workflow sets DOCS_BASE=/Ashes/; local dev and custom-domain builds
// default to /. Head asset hrefs are not base-prefixed automatically, so the
// favicon link below must use it explicitly.
const base = process.env.DOCS_BASE ?? "/";

/**
 * A sidebar entry for a page plus a collapsed submenu of its section headings.
 * Sections are the shallowest heading level the page uses after its title
 * (H2 normally; H1 chapters for the language spec), and anchors are produced
 * with VitePress's own slugifier so they always match the rendered ids.
 */
function page(text: string, link: string): DefaultTheme.SidebarItem {
  const file = fileURLToPath(new URL(`../../md${link}.md`, import.meta.url));
  const lines = readFileSync(file, "utf8").split("\n");
  const headings: { level: number; text: string }[] = [];
  let inFence = false;
  let seenTitle = false;
  for (const line of lines) {
    if (line.trimStart().startsWith("```")) {
      inFence = !inFence;
      continue;
    }
    if (inFence) continue;
    const m = /^(#{1,2})\s+(.+?)\s*$/.exec(line);
    if (!m) continue;
    if (!seenTitle && m[1].length === 1) {
      seenTitle = true;
      continue;
    }
    headings.push({ level: m[1].length, text: m[2] });
  }
  const top = Math.min(...headings.map((x) => x.level), 2);
  const items = headings
    .filter((x) => x.level === top)
    .map((x) => {
      const clean = x.text
        .replace(/`([^`]*)`/g, "$1")
        .replace(/\[([^\]]*)\]\([^)]*\)/g, "$1")
        .replace(/\*\*?([^*]*)\*\*?/g, "$1");
      return { text: clean, link: `${link}#${slugify(clean)}` };
    });
  return items.length > 1
    ? { text, link, collapsed: true, items }
    : { text, link };
}

// https://vitepress.dev/reference/site-config
export default defineConfig({
  base,
  srcDir: "../md",
  outDir: "../site",
  cleanUrls: true,
  lastUpdated: true,
  title: "Ashes",
  description:
    "A pure functional ML-family language compiled to standalone native executables",
  head: [
    ["link", { rel: "icon", type: "image/png", href: `${base}logo.png` }],
  ],
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
          page("Getting Started", "/guide/getting-started"),
          page("Projects", "/guide/projects"),
          page("Testing", "/guide/testing"),
          page("Debugging", "/guide/debugging"),
          page("Development", "/guide/development"),
          page("Local CI/CD", "/guide/local-ci"),
        ],
      },
      {
        text: "Reference",
        items: [
          page("Language Specification", "/reference/language"),
          page("Standard Library", "/reference/standard-library"),
          page("Compiler CLI", "/reference/cli"),
          page("Formatter", "/reference/formatter"),
          page("Diagnostics", "/reference/diagnostics"),
        ],
      },
      {
        text: "Internals",
        items: [
          page("Compiler Architecture", "/internals/architecture"),
          page("IR Reference", "/internals/ir"),
        ],
      },
      {
        text: "Future Designs",
        collapsed: true,
        items: [
          page("Future Features", "/future/FUTURE_FEATURES"),
          page("Package Manager", "/future/PACKAGE_MANAGER"),
          page("Registry API", "/future/REGISTRY_API"),
          page("Server Support", "/future/SERVER_SUPPORT"),
          page("Self-Hosting", "/future/SELF_HOSTING"),
          page("Compiler Optimization", "/future/COMPILER_OPTIMIZATION"),
        ],
      },
    ],

    socialLinks: [
      { icon: "github", link: "https://github.com/MattiasHognas/Ashes" },
    ],
  },
});
