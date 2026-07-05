import type { Theme } from "vitepress";
import DefaultTheme from "vitepress/theme";
import Mermaid from "./Mermaid.vue";
// Catppuccin for VitePress: Latte in light mode, Mocha in dark mode.
import "@catppuccin/vitepress/theme/mocha/mauve.css";

export default {
  extends: DefaultTheme,
  enhanceApp({ app }) {
    app.component("Mermaid", Mermaid);
  },
} satisfies Theme;
