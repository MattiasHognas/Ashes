<script setup lang="ts">
import { onMounted, ref } from "vue";
import { useRouter } from "vue-router";
import { browsePackages } from "../api";
import LoadingState from "../components/LoadingState.vue";
import PackageCard from "../components/PackageCard.vue";
import type { PackageSummary } from "../types";

const router = useRouter();
const query = ref("");
const packages = ref<PackageSummary[]>([]);
const loading = ref(true);

function search(): void {
  const q = query.value.trim();
  void router.push({ name: "packages", query: q.length > 0 ? { q } : {} });
}

onMounted(async () => {
  try {
    const page = await browsePackages("recent", 6);
    packages.value = page.packages;
  } finally {
    loading.value = false;
  }
});
</script>

<template>
  <div class="home-view">
    <section class="hero">
      <div class="hero-glow" aria-hidden="true"></div>
      <div class="hero-inner page-width">
        <div class="hero-copy">
          <div class="eyebrow"><span></span> The Ashes package registry</div>
          <h1>Code meant to be shared<br /><em>and usable to anyone.</em></h1>
          <p>
            Discover source packages for Ashes, with immutable releases and
            compiler-inferred capability metadata visible before you add them.
          </p>

          <form class="hero-search" role="search" @submit.prevent="search">
            <svg viewBox="0 0 24 24" aria-hidden="true">
              <path d="m21 21-4.3-4.3m2.3-5.2a7.5 7.5 0 1 1-15 0 7.5 7.5 0 0 1 15 0Z" />
            </svg>
            <input v-model="query" type="search" aria-label="Search the registry"
              placeholder="Search by package, purpose, or keyword" autofocus />
            <button type="submit">Search registry</button>
          </form>

          <div class="hero-note">
            <span>Try it from your terminal</span>
            <code>ashes search json</code>
          </div>
        </div>

        <aside class="workflow-card" aria-label="How packages are added">
          <div class="workflow-card-head">
            <span class="status-dot"></span>
            <span>Package workflow</span>
            <small>source first</small>
          </div>

          <div class="terminal-block">
            <div class="terminal-dots" aria-hidden="true">
              <i></i><i></i><i></i>
            </div>
            <code><span>$</span> ashes add Json</code>
            <p><i>✓</i> Added Json to dependencies.</p>
          </div>

          <div class="manifest-preview">
            <div class="manifest-label">
              <span>ashes.json</span>
              <small>updated locally</small>
            </div>
            <pre><span>{</span>
  <b>"dependencies"</b>: <span>{</span>
    <b>"Json"</b>: <em>"*"</em>
  <span>}</span>
<span>}</span></pre>
          </div>

          <div class="workflow-foot">
            <span><i></i> Immutable source</span>
            <span><i></i> Release metadata</span>
          </div>
        </aside>
      </div>
    </section>

    <section class="principles page-width" aria-label="Registry principles">
      <article>
        <span class="principle-number">01</span>
        <div>
          <h2>Source first</h2>
          <p>Every release is a compact, inspectable source archive.</p>
        </div>
      </article>
      <article>
        <span class="principle-number">02</span>
        <div>
          <h2>Immutable releases</h2>
          <p>Content hashes make published versions reproducible.</p>
        </div>
      </article>
      <article>
        <span class="principle-number">03</span>
        <div>
          <h2>Visible effects</h2>
          <p>Capability requirements are surfaced as first-class metadata.</p>
        </div>
      </article>
    </section>

    <section class="recent-section page-width">
      <div class="section-heading">
        <div>
          <span class="eyebrow"><span></span> From the registry</span>
          <h2>Recently published</h2>
        </div>
        <RouterLink class="text-link" to="/packages">
          Browse all packages
          <svg viewBox="0 0 20 20" aria-hidden="true">
            <path d="M4 10h11m-4-4 4 4-4 4" />
          </svg>
        </RouterLink>
      </div>

      <LoadingState v-if="loading" />
      <div v-else-if="packages.length > 0" class="package-grid">
        <PackageCard v-for="(item, index) in packages" :key="item.namespace" :package="item" :index="index" />
      </div>
      <div v-else class="empty-showcase">
        <div>
          <h3>The registry is ready for its first package.</h3>
          <p>
            Publish from an Ashes project, then it will appear here automatically.
          </p>
        </div>
        <code>ashes publish</code>
      </div>
    </section>
  </div>
</template>
