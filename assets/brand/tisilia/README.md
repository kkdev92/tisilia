# Tisilia-chan brand assets

Ready-sized JPEG artwork for Tisilia's package icons, repository README, documentation, Explorer UI and social sharing.

**Asset inventory:** [manifest.json](./manifest.json)

## Start here

Keep this directory at `assets/brand/tisilia/` in the repository. Do not rename individual files during initial integration. The manifest records all 21 runtime/publication images, including dimensions, SHA-256 digests, byte counts, English text and background colors.

Run the read-only check of the NuGet icon and Explorer distribution copies from the repository root:

```sh
npm run brand:check
```

The NuGet icon, repository banner, and Explorer empty-state illustrations are integrated into the repository. Run `npm run brand:sync` after updating the selected Explorer artwork or its policy; published Explorer builds use their bundled copies.

## Formats and identity

Every image is an actual 8-bit RGB JPEG in sRGB, with a `.jpg` extension. These are not renamed PNGs. JPEGs are opaque: illustrations use ivory (`#FFF9E9`) or dark charcoal (`#1B1C27`) mattes. They are not transparent cutouts. Place them on matching surfaces, or inside an intentional card.

The NuGet icon and all avatar sizes use the same portrait crop. Keep the golden hair, amber eyes, blue/purple link hair accessory, white-and-yellow clothing and cheerful expression. Do not mirror the images: the asymmetric hair accessory would move sides. Do not stretch an image to change its aspect ratio.

All lettering inside the supplied images is English. Image captions are typeset separately; previous posters with Japanese lettering have not been carried into this pack. Do not use an image as the only place to communicate installation instructions or product capabilities.

## Files

| File, relative to this folder | Pixels | Bytes | Intended use |
| --- | --- | ---: | --- |
| `icons/nuget-icon.jpg` | 128 x 128 | 20,226 | Embedded icon shared by all Tisilia NuGet packages |
| `icons/avatar-256.jpg` | 256 x 256 | 65,195 | Compact avatar or documentation portrait |
| `icons/avatar-512.jpg` | 512 x 512 | 175,022 | GitHub or social profile avatar |
| `icons/avatar-1024.jpg` | 1024 x 1024 | 476,338 | High-resolution avatar for future non-upscaled exports |
| `icons/link-mark-light.jpg` | 256 x 256 | 11,567 | Supporting connection mark; not a replacement for the mascot icon |
| `icons/link-mark-dark.jpg` | 256 x 256 | 11,878 | Supporting connection mark; not a replacement for the mascot icon |
| `banners/readme-banner-light.jpg` | 1600 x 480 | 192,096 | Root GitHub README hero; display at up to 100% width |
| `banners/readme-banner-dark.jpg` | 1600 x 480 | 195,721 | Root GitHub README hero; display at up to 100% width |
| `banners/package-banner.jpg` | 960 x 240 | 64,674 | Compact NuGet and npm README header, after public URL verification |
| `social/github-social-preview.jpg` | 1280 x 640 | 214,781 | GitHub Settings > Social preview upload |
| `social/social-card.jpg` | 1200 x 630 | 205,967 | Website Open Graph image or general link card |
| `social/release-card.jpg` | 1600 x 900 | 251,874 | Release announcement artwork with a full-body mascot |
| `illustrations/mascot-full-light.jpg` | 1024 x 1536 | 344,354 | Full-body illustration for the product site, docs or release compositions |
| `illustrations/mascot-docs-light.jpg` | 640 x 800 | 233,756 | Documentation guide illustration; use at 220-320 CSS px width |
| `illustrations/mascot-chibi-light.jpg` | 512 x 512 | 100,866 | Explorer welcome or empty-state illustration |
| `illustrations/mascot-chibi-small-light.jpg` | 160 x 160 | 15,468 | Small UI decoration, no text or status meaning |
| `illustrations/mascot-full-dark.jpg` | 1024 x 1536 | 346,818 | Full-body illustration for the product site, docs or release compositions |
| `illustrations/mascot-docs-dark.jpg` | 640 x 800 | 239,717 | Documentation guide illustration; use at 220-320 CSS px width |
| `illustrations/mascot-chibi-dark.jpg` | 512 x 512 | 103,904 | Explorer welcome or empty-state illustration |
| `illustrations/mascot-chibi-small-dark.jpg` | 160 x 160 | 16,881 | Small UI decoration, no text or status meaning |
| `illustrations/mascot-docs-small.jpg` | 176 x 220 | 28,508 | Optional small standalone illustration for a package README instead of its banner |

`manifest.json` is the file inventory, not a third-party license inventory. `ASSET_PREVIEW.jpg` lives with the integration docs and is a browsing aid, not a runtime asset.

## Placement defaults

Use the 128 px face icon for every NuGet package. Use the light 1600 x 480 banner at the top of the GitHub README; the dark version is available for theme-aware presentation. Use either the short package banner or the 176 x 220 illustration in a package README, not both. Use the chibi in Explorer's real welcome/empty state, not in place of actionable error text.

The supporting ring mark is secondary to the face icon. It expresses the adopted blue/purple/yellow connection motif and does not imply endorsement by Microsoft or the TypeScript team.

## Production record and policy

The mascot illustrations are derived from user-approved AI-generated images supplied earlier in this project conversation. This pack crops, resizes, color-manages and composites those images; it does not claim that a human illustrator drew them by hand. The plain connection mark was drawn programmatically from geometric shapes for this pack, not imported from an icon library. Text is rasterized; no font files are redistributed here.

Original source image hashes are recorded in `manifest.json`; source PNGs are not bundled. Preserve source artwork separately for future high-resolution or transparent exports. Do not repeatedly recompress these JPEGs or describe them as lossless masters.

Existing Vue, Feather Icons and ASP.NET Core notices remain separate in [NOTICE](../../../NOTICE) and must not be removed or conflated with the mascot's production record.

## Brand asset policy

The Tisilia source code is MIT-licensed. Tisilia names, logos, Tisilia-chan artwork and the other files in this directory are subject to [BRAND-ASSET-POLICY.md](./BRAND-ASSET-POLICY.md) for branding and identity uses.

The policy allows normal factual, editorial, documentation and integration references, but does not grant permission to imply endorsement or to adopt Tisilia's identity as the branding of an unrelated product.

Some artwork was created with generative-AI tools and subsequently curated and prepared for the project. The policy does not claim that every individual asset is independently copyrightable in every jurisdiction.
