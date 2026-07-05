<script setup lang="ts">
// Client-side, theme-aware Mermaid rendering for ```mermaid fences (the fence
// rule in config.ts emits this component). Rendering happens on mount and
// re-runs when the color scheme flips, so diagrams match the active theme.
import { useData } from "vitepress";
import { onMounted, ref, watch } from "vue";

const props = defineProps<{ code: string }>();
const { isDark } = useData();
const container = ref<HTMLElement>();
let renderSeq = 0;

async function render() {
  const mermaid = (await import("mermaid")).default;
  mermaid.initialize({
    startOnLoad: false,
    theme: isDark.value ? "dark" : "default",
  });
  const id = `mermaid-${Date.now()}-${renderSeq++}`;
  const { svg } = await mermaid.render(id, decodeURIComponent(props.code));
  if (container.value) {
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
