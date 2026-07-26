import { readonly, ref, type Ref } from "vue";

export type ColorTheme = "dark" | "light";

const storageKey = "ashes-registry-theme";
const activeTheme = ref<ColorTheme>("light");

function isColorTheme(value: string | null): value is ColorTheme {
  return value === "dark" || value === "light";
}

function applyTheme(theme: ColorTheme, persist: boolean): void {
  activeTheme.value = theme;
  document.documentElement.dataset.theme = theme;
  document.documentElement.style.colorScheme = theme;

  if (persist) {
    localStorage.setItem(storageKey, theme);
  }
}

export function initializeTheme(): void {
  const storedTheme = localStorage.getItem(storageKey);
  if (isColorTheme(storedTheme)) {
    applyTheme(storedTheme, false);
    return;
  }

  const prefersDark =
    typeof window.matchMedia === "function" &&
    window.matchMedia("(prefers-color-scheme: dark)").matches;
  const preferredTheme = prefersDark ? "dark" : "light";
  applyTheme(preferredTheme, false);
}

export function useColorTheme(): {
  theme: Readonly<Ref<ColorTheme>>;
  toggleTheme: () => void;
} {
  return {
    theme: readonly(activeTheme),
    toggleTheme: (): void => {
      applyTheme(activeTheme.value === "dark" ? "light" : "dark", true);
    },
  };
}
