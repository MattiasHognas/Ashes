<script setup lang="ts">
import { computed, ref, watch } from "vue";
import { useRoute, useRouter } from "vue-router";
import { browsePackages, searchPackages } from "../api";
import LoadingState from "../components/LoadingState.vue";
import PackageCard from "../components/PackageCard.vue";
import type { PackageSummary } from "../types";

const route = useRoute();
const router = useRouter();
const input = ref("");
const packages = ref<PackageSummary[]>([]);
const nextCursor = ref<string | null>(null);
const loading = ref(true);
const loadingMore = ref(false);
const error = ref("");

const query = computed(() =>
  typeof route.query.q === "string" ? route.query.q.trim() : "",
);
const sort = computed(() =>
  typeof route.query.sort === "string" ? route.query.sort : "recent",
);

async function load(reset: boolean): Promise<void> {
  if (reset) {
    loading.value = true;
    packages.value = [];
    nextCursor.value = null;
  } else {
    loadingMore.value = true;
  }
  error.value = "";

  try {
    const page =
      query.value.length > 0
        ? await searchPackages(query.value, 18, reset ? undefined : nextCursor.value ?? undefined)
        : await browsePackages(sort.value, 18, reset ? undefined : nextCursor.value ?? undefined);
    packages.value = reset ? page.packages : [...packages.value, ...page.packages];
    nextCursor.value = page.nextCursor;
  } catch (reason) {
    error.value = reason instanceof Error ? reason.message : "The registry could not be reached.";
  } finally {
    loading.value = false;
    loadingMore.value = false;
  }
}

function submit(): void {
  const q = input.value.trim();
  void router.push({
    name: "packages",
    query: {
      ...(q.length > 0 ? { q } : {}),
      ...(sort.value !== "recent" ? { sort: sort.value } : {}),
    },
  });
}

function setSort(value: string): void {
  void router.push({
    name: "packages",
    query: {
      ...(query.value.length > 0 ? { q: query.value } : {}),
      ...(value !== "recent" ? { sort: value } : {}),
    },
  });
}

watch(
  () => route.fullPath,
  () => {
    input.value = query.value;
    void load(true);
  },
  { immediate: true },
);
</script>

<template>
  <div class="browse-view page-width">
    <header class="browse-header">
      <span class="eyebrow"><span></span> Package index</span>
      <h1>{{ query ? `Results for “${query}”` : "Explore the registry" }}</h1>
      <p>
        Find reusable Ashes packages and inspect what they require before adding
        them to your project.
      </p>
    </header>

    <div class="browse-toolbar">
      <form class="browse-search" role="search" @submit.prevent="submit">
        <svg viewBox="0 0 24 24" aria-hidden="true">
          <path d="m21 21-4.3-4.3m2.3-5.2a7.5 7.5 0 1 1-15 0 7.5 7.5 0 0 1 15 0Z" />
        </svg>
        <input v-model="input" type="search" aria-label="Search packages" placeholder="Search packages" />
        <button type="submit">Search</button>
      </form>

      <div v-if="!query" class="sort-control" aria-label="Sort packages">
        <button :class="{ active: sort === 'recent' }" type="button" @click="setSort('recent')">
          Recent
        </button>
        <button :class="{ active: sort === 'name' }" type="button" @click="setSort('name')">
          A–Z
        </button>
      </div>
    </div>

    <LoadingState v-if="loading" />
    <div v-else-if="error" class="message-panel error-panel">
      <span>Registry unavailable</span>
      <h2>We couldn’t load the package index.</h2>
      <p>{{ error }}</p>
      <button type="button" @click="load(true)">Try again</button>
    </div>
    <div v-else-if="packages.length === 0" class="message-panel">
      <span>No matches</span>
      <h2>{{ query ? `Nothing matched “${query}”.` : "No packages have been published yet." }}</h2>
      <p>Try a broader term, package namespace, or keyword.</p>
      <RouterLink v-if="query" to="/packages">Clear the search</RouterLink>
    </div>
    <template v-else>
      <div class="results-meta">
        <span>{{ query ? "Ranked by relevance" : sort === "name" ? "Sorted by name" : "Newest first" }}</span>
        <span>{{ packages.length }} package{{ packages.length === 1 ? "" : "s" }}</span>
      </div>
      <div class="package-grid">
        <PackageCard
          v-for="(item, index) in packages"
          :key="item.namespace"
          :package="item"
          :index="index"
        />
      </div>
      <div v-if="nextCursor" class="load-more">
        <button type="button" :disabled="loadingMore" @click="load(false)">
          {{ loadingMore ? "Loading…" : "Load more packages" }}
        </button>
      </div>
    </template>
  </div>
</template>
