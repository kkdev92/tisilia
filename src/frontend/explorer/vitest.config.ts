import { defineConfig, mergeConfig } from "vitest/config";
import viteConfig from "./vite.config.js";

// the package's vite.config.ts builds the bundle (it ships with the package); the tests add their setup here
export default mergeConfig(viteConfig, defineConfig({ test: { setupFiles: ["./test/setup.ts"] } }));
