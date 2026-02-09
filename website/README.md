# Hawk Website (Astro)

Marketing/docs-style site for the Hawk project, visually inspired by andobuild.com, but with a strong yellow brand palette that contrasts with black and white.

## Dev

From the repo root:

```bash
npm --prefix website install
npm --prefix website run dev
```

Then open `http://localhost:4321`.

## Build

```bash
npm --prefix website run build
npm --prefix website run preview
```

## Notes

- Theme lives in `website/src/styles/global.css` (brand yellow is `--c-brand`).
- Pages live in `website/src/pages/`.
