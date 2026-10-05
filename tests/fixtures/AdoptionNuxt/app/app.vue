<script setup lang="ts">
import * as api from "../../src/api/index.js";
import { guid } from "@kkdev92/tisilia-runtime";
const file = await useTisiliaOperation<api.FileGetArgs, api.FileGetResult>(api, api.fileGetOperation, {});
const json = await useTisiliaOperation<api.PingGetArgs, api.PingGetResult>(api, api.pingGetOperation, { id: guid("0f8fad5b-d9cb-469f-a165-70867728950e") });
if (import.meta.server) {
  const result = file.serverResult.value;
  if (result?.kind !== "response" || !("data" in result) || JSON.stringify([...result.data.bytes]) !== "[0,255,1,195,40]") throw new Error("SSR binary serverResult missing");
  useResponseHeader("x-ssr-file-size").value = "5";
}
const mounted = ref(false);
const imperative = ref(0);
const client = useTisiliaClient(api.createInstallCheckClient);
async function download() {
  const result = await client.fileGet();
  if (result.kind !== "response") throw new Error(result.kind);
  if (JSON.stringify([...result.data.bytes]) !== "[0,255,1,195,40]") throw new Error("imperative binary bytes changed");
  imperative.value = result.data.bytes.length;
}
onMounted(() => { mounted.value = true; });
</script>
<template>
  <main :data-mounted="mounted">
    <div id="binary">{{ file.result.value?.kind }}:{{ file.result.value && 'code' in file.result.value ? file.result.value.code : '' }}</div>
    <div id="json">{{ json.result.value?.kind === 'response' && 'data' in json.result.value ? json.result.value.data.revision.toString() : '' }}</div>
    <button id="refresh" @click="file.refresh()">Refresh</button>
    <button id="download" @click="download">Imperative download</button>
    <div id="bytes">{{ imperative }}</div>
  </main>
</template>
