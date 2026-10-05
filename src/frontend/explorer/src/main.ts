import { createApp, watchEffect } from "vue";
import App from "./App.vue";
import { applyPreferences } from "./prefs.js";
import "./styles.css";

// the language and the theme reach the document before the first render, and follow every change
watchEffect(() => applyPreferences(document.documentElement));
createApp(App).mount("#app");
