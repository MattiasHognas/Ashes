<script setup lang="ts">
import { ref } from "vue";
import { useRouter } from "vue-router";
import { useColorTheme } from "../theme";

const router = useRouter();
const query = ref("");
const { theme, toggleTheme } = useColorTheme();

function submitSearch(): void {
  const q = query.value.trim();
  void router.push({ name: "packages", query: q.length > 0 ? { q } : {} });
  query.value = "";
}
</script>

<template>
  <header class="site-header">
    <div class="header-inner">
      <RouterLink class="brand" to="/" aria-label="Ashes Registry home">
        <span class="brand-mark">
          <img src="/logo.png" alt="" />
        </span>
        <span>
          <strong>Ashes</strong>
          <small>registry</small>
        </span>
      </RouterLink>

      <form class="header-search" role="search" @submit.prevent="submitSearch">
        <svg viewBox="0 0 24 24" aria-hidden="true">
          <path d="m21 21-4.3-4.3m2.3-5.2a7.5 7.5 0 1 1-15 0 7.5 7.5 0 0 1 15 0Z" />
        </svg>
        <input
          v-model="query"
          type="search"
          aria-label="Search packages"
          placeholder="Search packages"
        />
        <kbd>/</kbd>
      </form>

      <nav aria-label="Primary navigation">
        <RouterLink to="/packages">Browse</RouterLink>
        <a
          href="https://github.com/MattiasHognas/Ashes"
          target="_blank"
          rel="noreferrer"
        >
          GitHub
        </a>
        <button
          class="theme-toggle"
          type="button"
          :aria-label="`Switch to ${theme === 'dark' ? 'light' : 'dark'} mode`"
          :title="`Switch to ${theme === 'dark' ? 'light' : 'dark'} mode`"
          @click="toggleTheme"
        >
          <svg v-if="theme === 'dark'" viewBox="0 0 24 24" aria-hidden="true">
            <circle cx="12" cy="12" r="4" />
            <path d="M12 2v2m0 16v2M4.9 4.9l1.4 1.4m11.4 11.4 1.4 1.4M2 12h2m16 0h2M4.9 19.1l1.4-1.4M17.7 6.3l1.4-1.4" />
          </svg>
          <svg v-else viewBox="0 0 24 24" aria-hidden="true">
            <path d="M20.4 15.3A8.5 8.5 0 0 1 8.7 3.6 8.5 8.5 0 1 0 20.4 15.3Z" />
          </svg>
        </button>
      </nav>
    </div>
  </header>
</template>
