<script setup lang="ts">
// Client-side, theme-aware Mermaid rendering for ```mermaid fences (the fence
// rule in config.ts emits this component). Rendering happens on mount and
// re-runs when the color scheme flips, so diagrams match the active theme.
import { useData } from "vitepress";
import { onMounted, ref, useId, watch } from "vue";

const props = defineProps<{ code: string }>();
const { isDark } = useData();
const container = ref<HTMLElement>();
const componentId = useId().replace(/[^A-Za-z0-9_-]/g, "");
let renderSeq = 0;
let latestRender = 0;

async function render() {
  const requestedRender = ++latestRender;
  const mermaid = (await import("mermaid")).default;
  const themeVariables = isDark.value
    ? {
        background: "#171614",
        primaryColor: "#2a1f18",
        primaryTextColor: "#f4f0e9",
        primaryBorderColor: "#efa66a",
        secondaryColor: "#1d1b18",
        secondaryTextColor: "#aaa49c",
        secondaryBorderColor: "#77716a",
        tertiaryColor: "#131210",
        tertiaryTextColor: "#f4f0e9",
        tertiaryBorderColor: "#b96632",
        lineColor: "#99938b",
        textColor: "#f4f0e9",
        mainBkg: "#171614",
        nodeBorder: "#b96632",
        clusterBkg: "#131210",
        clusterBorder: "#4c4640",
        titleColor: "#ffc48f",
        edgeLabelBackground: "#0e0d0c",
      }
    : {
        background: "#fbf8f3",
        primaryColor: "#f4e0d1",
        primaryTextColor: "#28231f",
        primaryBorderColor: "#b65f2d",
        secondaryColor: "#f3ede5",
        secondaryTextColor: "#665f58",
        secondaryBorderColor: "#8b8278",
        tertiaryColor: "#ffffff",
        tertiaryTextColor: "#28231f",
        tertiaryBorderColor: "#d67c42",
        lineColor: "#665f58",
        textColor: "#28231f",
        mainBkg: "#ffffff",
        nodeBorder: "#b65f2d",
        clusterBkg: "#f3ede5",
        clusterBorder: "#c9bbae",
        titleColor: "#87431f",
        edgeLabelBackground: "#fbf8f3",
      };
  mermaid.initialize({
    startOnLoad: false,
    theme: "base",
    themeVariables: {
      ...themeVariables,
      darkMode: isDark.value,
      fontFamily:
        'Inter, ui-sans-serif, -apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif',
    },
  });
  const id = `mermaid-${componentId}-${renderSeq++}`;
  const { svg } = await mermaid.render(id, decodeURIComponent(props.code));
  if (container.value && requestedRender === latestRender) {
    container.value.innerHTML = svg;
  }
}

onMounted(render);
watch(isDark, render);
</script>

<template>
  <div ref="container" class="mermaid-diagram" />
</template>

<style scoped>
.mermaid-diagram {
  display: flex;
  justify-content: center;
  margin: 16px 0;
  overflow-x: auto;
}
</style>
