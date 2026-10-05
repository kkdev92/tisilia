import vue from "@vitejs/plugin-vue";
import { defineConfig } from "vite";

// Static SPA served by ASP.NET Core under /__tisilia/: relative asset URLs, no remote modules.
export default defineConfig({
  base: "./",
  plugins: [vue()],
  build: {
    outDir: "dist",
    emptyOutDir: true,
    sourcemap: false,
    target: "es2022",
    modulePreload: { polyfill: false },
    // The page bundles third-party code (Vue, and the codec modules an application builds in); this lists every package of
    // it with its license, served beside the page.
    license: { fileName: "third-party-licenses.json" },
  },
});
