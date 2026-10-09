<script setup lang="ts">
import type { ContractFormField } from "@kkdev92/tisilia-runtime";
import { t } from "../i18n.js";
import { selectFormFiles, type Draft } from "../state.js";
import { mapEntryKey } from "../explorer.js";

const props = withDefaults(defineProps<{ fields: readonly ContractFormField[]; draft: Draft; domKey: string; prefix?: string }>(), { prefix: "" });
const keyOf = (field: ContractFormField): string => props.prefix + field.name;
const countOf = (field: ContractFormField): number => field.repeated || field.kind === "map" ? Number(props.draft.formValues[keyOf(field)] ?? 0) : 1;
function resize(field: ContractFormField, count: number): void {
  const key = keyOf(field);
  // Removed rows must not retain uploads or hidden values when they are added again.
  const removed = key + `[${count}]`;
  for (const values of [props.draft.formValues, props.draft.formUploads]) {
    for (const name of Object.keys(values)) { if (name.startsWith(removed + ".")) { delete values[name]; } }
  }
  for (const part of ["key", "value"] as const) { delete props.draft.formValues[mapEntryKey(key, count, part)]; }
  if (count === 0 && field.presence === "optional") { delete props.draft.formValues[key]; }
  else { props.draft.formValues[key] = String(count); }
  props.draft.edited = true;
}
</script>

<template>
  <div v-for="field in fields" :key="field.name" class="field" :class="{ 'form-group': field.kind === 'object' }">
    <label :for="'form-' + domKey + '-' + keyOf(field)">{{ keyOf(field) }}<span v-if="field.presence === 'required'" class="req">*</span></label>
    <div v-if="field.kind === 'object'" class="field-control">
      <template v-if="field.repeated">
        <div v-for="index in countOf(field)" :key="index" class="form-item">
          <FormFieldsEditor :fields="field.fields ?? []" :draft="draft" :dom-key="domKey" :prefix="keyOf(field) + `[${index - 1}].`" />
        </div>
        <div class="form-actions"><button type="button" class="btn small" :aria-label="t().formAddItem + ': ' + keyOf(field)" :disabled="countOf(field) >= 1024" @click="resize(field, countOf(field) + 1)">{{ t().formAddItem }}</button>
        <button v-if="countOf(field) > 0" type="button" class="btn small" :aria-label="t().formRemoveItem + ': ' + keyOf(field)" @click="resize(field, countOf(field) - 1)">{{ t().formRemoveItem }}</button></div>
      </template>
      <template v-else>
        <label v-if="field.presence === 'optional'" class="field-hint"><input type="checkbox" :checked="draft.formValues[keyOf(field)] !== undefined" @change="($event.target as HTMLInputElement).checked ? draft.formValues[keyOf(field)] = '1' : delete draft.formValues[keyOf(field)]; draft.edited = true" />{{ t().sendField }}</label>
        <FormFieldsEditor v-if="field.presence === 'required' || draft.formValues[keyOf(field)] !== undefined" :fields="field.fields ?? []" :draft="draft" :dom-key="domKey" :prefix="keyOf(field) + '.'" />
      </template>
    </div>
    <div v-else-if="field.kind === 'map'" class="field-control">
      <div v-for="index in countOf(field)" :key="index" class="form-item map-entry">
        <input class="input" :aria-label="t().formEntryKey + ': ' + keyOf(field)" :placeholder="t().formEntryKey" :value="draft.formValues[mapEntryKey(keyOf(field), index - 1, 'key')] ?? ''" @input="draft.formValues[mapEntryKey(keyOf(field), index - 1, 'key')] = ($event.target as HTMLInputElement).value; draft.edited = true; draft.touched['/body'] = true" />
        <input class="input" :aria-label="t().formEntryValue + ': ' + keyOf(field)" :placeholder="t().formEntryValue" :value="draft.formValues[mapEntryKey(keyOf(field), index - 1, 'value')] ?? ''" @input="draft.formValues[mapEntryKey(keyOf(field), index - 1, 'value')] = ($event.target as HTMLInputElement).value; draft.edited = true; draft.touched['/body'] = true" />
      </div>
      <div class="form-actions"><button type="button" class="btn small" :aria-label="t().formAddItem + ': ' + keyOf(field)" :disabled="countOf(field) >= 1024" @click="resize(field, countOf(field) + 1)">{{ t().formAddItem }}</button>
      <button v-if="countOf(field) > 0" type="button" class="btn small" :aria-label="t().formRemoveItem + ': ' + keyOf(field)" @click="resize(field, countOf(field) - 1)">{{ t().formRemoveItem }}</button></div>
    </div>
    <input v-else-if="field.kind === 'file'" :id="'form-' + domKey + '-' + keyOf(field)" data-form-file class="input" type="file" :multiple="field.repeated" @change="selectFormFiles(draft, keyOf(field), Array.from(($event.target as HTMLInputElement).files ?? []))" />
    <div v-else class="field-control">
      <label v-if="field.presence === 'optional'" class="field-hint"><input type="checkbox" :checked="draft.formValues[keyOf(field)] !== undefined" @change="($event.target as HTMLInputElement).checked ? draft.formValues[keyOf(field)] = '' : delete draft.formValues[keyOf(field)]; draft.edited = true" />{{ t().sendField }}</label>
      <textarea :id="'form-' + domKey + '-' + keyOf(field)" class="input" :rows="field.repeated ? 3 : 1" :value="draft.formValues[keyOf(field)] ?? ''" @input="draft.formValues[keyOf(field)] = ($event.target as HTMLTextAreaElement).value; draft.edited = true; draft.touched['/body'] = true" />
      <span v-if="field.repeated" class="field-hint">{{ t().formRepeatedHint }}</span>
    </div>
  </div>
</template>

<style scoped>
.form-group { display: block; }
.form-group > label { display: block; font-weight: 600; margin-bottom: 6px; }
.field > label { min-width: 0; overflow-wrap: anywhere; }
.form-actions { display: flex; flex-wrap: wrap; gap: 6px; }
.form-item { border-left: 2px solid var(--border); padding-left: 12px; margin-block: 10px; }
.map-entry { display: grid; grid-template-columns: minmax(0, 1fr) minmax(0, 2fr); gap: 6px; }
</style>
