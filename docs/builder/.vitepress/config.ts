import { defineConfig } from "vitepress";
import ashesGrammar from "../../../vscode-extension/syntaxes/ashes.tmLanguage.json";

// https://vitepress.dev/reference/site-config
export default defineConfig({
  srcDir: "../md",
  title: "Ashes",
  description: "Ashes programming language documentation",
  markdown: {
    languages: [
      {
        ...ashesGrammar,
        aliases: ["ashes", "ash"],
      },
    ],
  },
  themeConfig: {
    // https://vitepress.dev/reference/default-theme-config
    nav: [
      { text: "Home", link: "/" },
      { text: "Examples", link: "/markdown-examples" },
    ],

    sidebar: [
      {
        text: "Examples",
        items: [
          { text: "Markdown Examples", link: "/markdown-examples" },
          { text: "Runtime API Examples", link: "/api-examples" },
        ],
      },
    ],

    socialLinks: [
      { icon: "github", link: "https://github.com/vuejs/vitepress" },
    ],
  },
});
