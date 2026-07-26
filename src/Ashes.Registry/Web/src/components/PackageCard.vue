<script setup lang="ts">
import type { PackageSummary } from "../types";

defineProps<{
  package: PackageSummary;
  index?: number;
}>();
</script>

<template>
  <RouterLink
    class="package-card"
    :style="{ '--delay': `${(index ?? 0) * 45}ms` }"
    :to="{ name: 'package', params: { namespace: package.namespace } }"
  >
    <div class="package-card-top">
      <span class="package-glyph" aria-hidden="true">{{ package.namespace.slice(0, 1) }}</span>
      <span v-if="package.latest" class="version-pill">v{{ package.latest }}</span>
      <span v-else class="version-pill muted">No active release</span>
    </div>
    <div>
      <h3>{{ package.namespace }}</h3>
      <p>{{ package.description || "No package description has been provided yet." }}</p>
    </div>
    <span class="card-action">
      Explore package
      <svg viewBox="0 0 20 20" aria-hidden="true">
        <path d="M4 10h11m-4-4 4 4-4 4" />
      </svg>
    </span>
  </RouterLink>
</template>
