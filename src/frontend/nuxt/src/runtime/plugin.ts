import { defineNuxtPlugin } from "nuxt/app";
import { useTisiliaScope } from "./composables/scope.js";

/**
 * Establishes the per-request scope nonce before any page runs: generated on the server for each
 * request, carried to the browser in the Nuxt state payload as an opaque public value. No client, token or
 * credential is captured in a global singleton here.
 */
export default defineNuxtPlugin({
  name: "tisilia",
  enforce: "pre",
  setup() {
    useTisiliaScope();
  },
});
