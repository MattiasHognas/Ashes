<script setup lang="ts">
import { computed, ref } from "vue";

const props = defineProps<{ namespace: string }>();
const copied = ref(false);
const command = computed(() => `ashes add ${props.namespace}`);

async function copy(): Promise<void> {
  await navigator.clipboard.writeText(command.value);
  copied.value = true;
  window.setTimeout(() => {
    copied.value = false;
  }, 1800);
}
</script>

<template>
  <button class="copy-command" type="button" @click="copy">
    <code><span>$</span> {{ command }}</code>
    <span class="copy-label">{{ copied ? "Copied" : "Copy" }}</span>
  </button>
</template>
