// Nuxt's build-time environment flags on import.meta (mirrors @nuxt/schema's ImportMeta augmentation; identical
// member types merge without conflict when both are present).
interface ImportMeta {
  readonly server: boolean;
  readonly client: boolean;
  readonly dev: boolean;
}
