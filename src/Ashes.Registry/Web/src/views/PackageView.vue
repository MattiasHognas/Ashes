<script setup lang="ts">
import DOMPurify from "dompurify";
import { marked } from "marked";
import { computed, ref, watch } from "vue";
import { useRoute, useRouter } from "vue-router";
import { getPackage, getReadme } from "../api";
import CopyCommand from "../components/CopyCommand.vue";
import LoadingState from "../components/LoadingState.vue";
import type { Package, PackageVersion } from "../types";

const props = defineProps<{ namespace: string; version?: string }>();
const route = useRoute();
const router = useRouter();
const packageInfo = ref<Package | null>(null);
const readmeHtml = ref("");
const readmeLoading = ref(false);
const loading = ref(true);
const error = ref("");

const orderedVersions = computed(() =>
  [...(packageInfo.value?.versions ?? [])].sort((a, b) =>
    b.publishedAt.localeCompare(a.publishedAt),
  ),
);
const latestVersion = computed(
  () =>
    orderedVersions.value.find(
      (item) => item.version === packageInfo.value?.latest,
    ) ?? orderedVersions.value[0],
);
const selectedVersion = computed<PackageVersion | undefined>(() => {
  const requested = typeof route.params.version === "string" ? route.params.version : props.version;
  return (
    orderedVersions.value.find((item) => item.version === requested) ??
    latestVersion.value
  );
});
const sourceUrl = computed(() =>
  selectedVersion.value
    ? `/api/v1/packages/${encodeURIComponent(props.namespace)}/${encodeURIComponent(selectedVersion.value.version)}/source`
    : "#",
);

function formatDate(value: string): string {
  return new Intl.DateTimeFormat("en", {
    day: "numeric",
    month: "short",
    year: "numeric",
  }).format(new Date(value));
}

function formatSize(bytes: number): string {
  if (bytes < 1024) {
    return `${bytes} B`;
  }
  if (bytes < 1024 * 1024) {
    return `${(bytes / 1024).toFixed(1)} KB`;
  }
  return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
}

function selectVersion(event: Event): void {
  const version = (event.target as HTMLSelectElement).value;
  void router.push({
    name: "package",
    params: { namespace: props.namespace, version },
  });
}

async function loadReadme(version: PackageVersion | undefined): Promise<void> {
  readmeHtml.value = "";
  if (!version) {
    return;
  }

  readmeLoading.value = true;
  try {
    const markdown = await getReadme(props.namespace, version.version);
    if (markdown) {
      const rendered = await marked.parse(markdown, {
        gfm: true,
        breaks: false,
      });
      readmeHtml.value = DOMPurify.sanitize(rendered, {
        USE_PROFILES: { html: true },
        FORBID_TAGS: ["iframe", "style", "form", "input", "button", "img"],
      });
    }
  } catch {
    readmeHtml.value = "";
  } finally {
    readmeLoading.value = false;
  }
}

async function loadPackage(): Promise<void> {
  loading.value = true;
  error.value = "";
  packageInfo.value = null;
  try {
    packageInfo.value = await getPackage(props.namespace);
  } catch (reason) {
    error.value = reason instanceof Error ? reason.message : "This package could not be loaded.";
  } finally {
    loading.value = false;
  }
}

watch(
  () => props.namespace,
  () => void loadPackage(),
  { immediate: true },
);
watch(
  () => selectedVersion.value?.version,
  () => void loadReadme(selectedVersion.value),
);
</script>

<template>
  <div class="package-view page-width">
    <LoadingState v-if="loading" />
    <div v-else-if="error || !packageInfo" class="message-panel error-panel">
      <span>Package not found</span>
      <h1>There’s nothing at this namespace.</h1>
      <p>{{ error }}</p>
      <RouterLink to="/packages">Browse the package index</RouterLink>
    </div>
    <template v-else>
      <nav class="breadcrumbs" aria-label="Breadcrumb">
        <RouterLink to="/packages">Packages</RouterLink>
        <svg viewBox="0 0 20 20" aria-hidden="true"><path d="m8 5 5 5-5 5" /></svg>
        <span>{{ packageInfo.namespace }}</span>
      </nav>

      <header class="package-hero">
        <div class="package-identity">
          <div class="large-package-glyph" aria-hidden="true">
            {{ packageInfo.namespace.slice(0, 1) }}
          </div>
          <div>
            <div class="package-title-line">
              <h1>{{ packageInfo.namespace }}</h1>
              <span v-if="selectedVersion?.yanked" class="yanked-pill">Yanked</span>
            </div>
            <p>{{ packageInfo.description || "No package description has been provided." }}</p>
            <div v-if="packageInfo.keywords.length > 0" class="keyword-list">
              <span v-for="keyword in packageInfo.keywords" :key="keyword">{{ keyword }}</span>
            </div>
          </div>
        </div>
        <div v-if="selectedVersion" class="version-picker">
          <label for="version">Release</label>
          <select id="version" :value="selectedVersion.version" @change="selectVersion">
            <option v-for="item in orderedVersions" :key="item.version" :value="item.version">
              {{ item.version }}{{ item.yanked ? " · yanked" : "" }}
            </option>
          </select>
        </div>
      </header>

      <div v-if="selectedVersion" class="package-layout">
        <main class="package-main">
          <div class="readme-heading">
            <div>
              <span class="eyebrow"><span></span> Package documentation</span>
              <h2>README</h2>
            </div>
            <a :href="sourceUrl">Download source</a>
          </div>
          <LoadingState v-if="readmeLoading" />
          <article v-else-if="readmeHtml" class="readme-content" v-html="readmeHtml"></article>
          <div v-else class="readme-empty">
            <span>README</span>
            <h3>This release doesn’t include a README.</h3>
            <p>The immutable source archive is still available for inspection.</p>
            <a :href="sourceUrl">Download {{ packageInfo.namespace }} {{ selectedVersion.version }}</a>
          </div>
        </main>

        <aside class="package-sidebar">
          <section class="sidebar-section install-section">
            <span class="sidebar-label">Add to a project</span>
            <CopyCommand :namespace="packageInfo.namespace" />
          </section>

          <section class="sidebar-section">
            <div class="sidebar-heading">
              <span class="sidebar-label">Public API capabilities</span>
              <span class="compiler-mark">Compiler inferred</span>
            </div>
            <div v-if="selectedVersion.capabilities.length > 0" class="capability-list">
              <span v-for="capability in selectedVersion.capabilities" :key="capability">
                <i></i>{{ capability }}
              </span>
            </div>
            <p v-else class="sidebar-empty">
              No capability requirements were recorded for this release.
            </p>
          </section>

          <section class="sidebar-section">
            <span class="sidebar-label">
              Dependencies
              <small>{{ selectedVersion.dependencies.length }}</small>
            </span>
            <div v-if="selectedVersion.dependencies.length > 0" class="dependency-list">
              <RouterLink
                v-for="dependency in selectedVersion.dependencies"
                :key="dependency.namespace"
                :to="{ name: 'package', params: { namespace: dependency.namespace } }"
              >
                <span>{{ dependency.namespace }}</span>
                <code>{{ dependency.req }}</code>
              </RouterLink>
            </div>
            <p v-else class="sidebar-empty">This release has no registry dependencies.</p>
          </section>

          <section class="sidebar-section metadata-list">
            <div>
              <span>Published</span>
              <strong>{{ formatDate(selectedVersion.publishedAt) }}</strong>
            </div>
            <div>
              <span>Source size</span>
              <strong>{{ formatSize(selectedVersion.size) }}</strong>
            </div>
            <div>
              <span>Owners</span>
              <strong>{{ packageInfo.owners.join(", ") || "Unclaimed" }}</strong>
            </div>
            <div class="hash-row">
              <span>Content hash</span>
              <code :title="selectedVersion.hash">{{ selectedVersion.hash }}</code>
            </div>
          </section>

          <section class="sidebar-section release-list">
            <span class="sidebar-label">Release history</span>
            <RouterLink
              v-for="item in orderedVersions.slice(0, 5)"
              :key="item.version"
              :class="{ current: item.version === selectedVersion.version }"
              :to="{ name: 'package', params: { namespace: packageInfo.namespace, version: item.version } }"
            >
              <span>v{{ item.version }}</span>
              <small>{{ item.yanked ? "Yanked" : formatDate(item.publishedAt) }}</small>
            </RouterLink>
          </section>
        </aside>
      </div>
      <div v-else class="message-panel">
        <span>No active releases</span>
        <h2>This namespace has no published versions.</h2>
      </div>
    </template>
  </div>
</template>
