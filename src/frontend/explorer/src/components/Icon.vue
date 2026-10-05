<script setup lang="ts">
// Portions of this inline icon set are adapted from Feather Icons.
// Feather Icons: Copyright (c) 2013-2023 Cole Bemis, MIT License.
// Modifications and Tisilia-specific icons: Copyright (c) 2026 kkdev92.
// See NOTICE for the full license text.
//
// Icons remain inline so the Explorer does not depend on an icon font,
// remote asset, or third-party runtime package (the CSP allows 'self' only).
const props = withDefaults(defineProps<{ name: string; size?: number }>(), { size: 16 });

const paths: Record<string, string[]> = {
  search: ["M11 19a8 8 0 1 1 0-16 8 8 0 0 1 0 16Z", "m21 21-4.3-4.3"],
  lock: ["M7 11V7a5 5 0 0 1 10 0v4", "M5 11h14v10H5z"],
  unlock: ["M7 11V7a5 5 0 0 1 9.9-1", "M5 11h14v10H5z"],
  key: ["M15.5 7.5a3.5 3.5 0 1 1-7 0 3.5 3.5 0 0 1 7 0Z", "M10.5 10.5 3 18v3h3l1-1v-2h2v-2h2l1.5-1.5"],
  sun: ["M12 17a5 5 0 1 0 0-10 5 5 0 0 0 0 10Z", "M12 1v2M12 21v2M4.2 4.2l1.4 1.4M18.4 18.4l1.4 1.4M1 12h2M21 12h2M4.2 19.8l1.4-1.4M18.4 5.6l1.4-1.4"],
  moon: ["M21 12.8A9 9 0 1 1 11.2 3a7 7 0 0 0 9.8 9.8Z"],
  monitor: ["M3 4h18v12H3z", "M8 20h8M12 16v4"],
  eye: ["M1 12s4-8 11-8 11 8 11 8-4 8-11 8S1 12 1 12Z", "M12 15a3 3 0 1 0 0-6 3 3 0 0 0 0 6Z"],
  "eye-off": ["M17.9 17.9A10.7 10.7 0 0 1 12 20c-7 0-11-8-11-8a19.8 19.8 0 0 1 5.1-5.9", "M9.9 4.2A10 10 0 0 1 12 4c7 0 11 8 11 8a19.9 19.9 0 0 1-2.2 3.2", "M14.1 14.1a3 3 0 1 1-4.2-4.2", "m1 1 22 22"],
  copy: ["M9 9h11v11H9z", "M5 15H4V4h11v1"],
  check: ["m20 6-11 11-5-5"],
  play: ["M6 4v16l14-8z"],
  stop: ["M6 6h12v12H6z"],
  x: ["M18 6 6 18M6 6l12 12"],
  plus: ["M12 5v14M5 12h14"],
  minus: ["M5 12h14"],
  trash: ["M3 6h18", "M8 6V4h8v2", "M19 6l-1 14H6L5 6"],
  chevron: ["m9 18 6-6-6-6"],
  "chevron-down": ["m6 9 6 6 6-6"],
  clock: ["M12 22a10 10 0 1 0 0-20 10 10 0 0 0 0 20Z", "M12 6v6l4 2"],
  code: ["m16 18 6-6-6-6M8 6l-6 6 6 6"],
  braces: ["M8 3H7a2 2 0 0 0-2 2v5a2 2 0 0 1-2 2 2 2 0 0 1 2 2v5a2 2 0 0 0 2 2h1", "M16 21h1a2 2 0 0 0 2-2v-5a2 2 0 0 1 2-2 2 2 0 0 1-2-2V5a2 2 0 0 0-2-2h-1"],
  refresh: ["M21 12a9 9 0 1 1-2.6-6.4L21 8", "M21 3v5h-5"],
  alert: ["M12 9v4M12 17h.01", "M10.3 3.9 1.8 18a2 2 0 0 0 1.7 3h17a2 2 0 0 0 1.7-3L13.7 3.9a2 2 0 0 0-3.4 0Z"],
  info: ["M12 22a10 10 0 1 0 0-20 10 10 0 0 0 0 20Z", "M12 16v-4M12 8h.01"],
  menu: ["M3 6h18M3 12h18M3 18h18"],
  sparkles: ["M12 3l1.6 4.4L18 9l-4.4 1.6L12 15l-1.6-4.4L6 9l4.4-1.6Z", "M19 15l.8 2.2L22 18l-2.2.8L19 21l-.8-2.2L16 18l2.2-.8Z"],
  layers: ["m12 2 10 5-10 5L2 7l10-5Z", "m2 17 10 5 10-5", "m2 12 10 5 10-5"],
  history: ["M3 3v5h5", "M3.1 13a9 9 0 1 0 2.2-7.3L3 8", "M12 7v5l4 2"],
  send: ["M22 2 11 13", "M22 2 15 22l-4-9-9-4Z"],
  file: ["M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8Z", "M14 2v6h6"],
  shield: ["M12 22s8-4 8-10V5l-8-3-8 3v7c0 6 8 10 8 10Z"],
  link: ["M10 13a5 5 0 0 0 7.5.5l3-3a5 5 0 0 0-7-7l-1.7 1.7", "M14 11a5 5 0 0 0-7.5-.5l-3 3a5 5 0 0 0 7 7l1.7-1.7"],
  wand: ["M15 4V2M15 16v-2M8 9h2M20 9h2M17.8 11.8 19 13M17.8 6.2 19 5M3 21l9-9M12.2 6.2 11 5"],
  download: ["M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4", "m7 10 5 5 5-5", "M12 15V3"],
  filter: ["M22 3H2l8 9.5V19l4 2v-8.5Z"],
  server: ["M3 4h18v6H3zM3 14h18v6H3z", "M7 7h.01M7 17h.01"],
  globe: ["M12 22a10 10 0 1 0 0-20 10 10 0 0 0 0 20Z", "M2 12h20", "M12 2a15 15 0 0 1 0 20 15 15 0 0 1 0-20Z"],
};
</script>

<template>
  <svg :width="props.size" :height="props.size" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true" focusable="false">
    <path v-for="(d, i) in paths[props.name] ?? []" :key="i" :d="d" />
  </svg>
</template>
