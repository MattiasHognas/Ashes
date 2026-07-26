import { defineConfig } from "vite";
import vue from "@vitejs/plugin-vue";

export default defineConfig({
  plugins: [vue()],
  publicDir: "../../../docs/md/public",
  server: {
    port: 5173,
    proxy: {
      "/api": {
        target: process.env.ASHES_REGISTRY_PROXY ?? "http://localhost:5000",
        changeOrigin: true,
      },
    },
  },
  build: {
    outDir: "dist",
    emptyOutDir: true,
  },
});
