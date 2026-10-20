---
name: tailwind-ui
description: Create and refine ProxyCraft's simple, accessible dashboard UI with Tailwind CSS v4 and its Node-free standalone compiler. Use for Razor styling, responsive layouts, forms, tables, and operating states; excludes SEO and marketing pages.
---

# Tailwind UI for ProxyCraft

Read the root `AGENTS.md`, then inspect `Build/Tailwind.targets`, `Styles/app.css`, `Views/Shared/_Layout.cshtml`, and existing affected components.

## Tailwind setup

- Use the official standalone Tailwind compiler pinned to **4.3.3** in `Build/Tailwind.targets`. Do not introduce Node/npm, package manifests, a CSS watcher, a frontend framework, or a component library.
- Compile once with `dotnet build -t:BuildCss --no-restore`. The first explicit call downloads a checksum-verified compiler into ignored `.tools/`; normal app build/run uses the included CSS and needs no compiler download.
- Edit `Styles/app.css`; `wwwroot/app.css` is generated and included in version control for .NET-only startup.
- Preserve the CSS-first `@import "tailwindcss" source(none)` and `@source "../Views"` and `@source "../wwwroot/js"` setup. Source paths are relative to the input stylesheet. Add another source only if actual UI classes live there.
- Use complete utility names in Razor conditionals. Tailwind scans source text and cannot discover classes assembled from fragments.
- Do not introduce v3 `@tailwind` directives, a configuration file, PostCSS, or CDN scripts by habit.

## UI decisions

- Use the semantic theme colors in `Styles/app.css`. Light uses slate/emerald; Dark preserves its simple layout; Rainbow explicitly allows neon gradients, aurora, and mouse-connected particles. Keep controls and status information easy to scan in every mode.
- Theme selection lives in `wwwroot/js/theme.js` and browser local storage. Preserve before-paint selection, automatic Rainbow effects, reduced-motion behavior, and pausing hidden-tab animation. The user removed the manual effects toggle and its preference.
- Use the shared shell. Create a shared component only for a repeated responsibility that actually exists.
- Build for narrow screens as well as desktop; allow controls to wrap. Configured proxy rows become mobile cards with wrapping/labeled destinations and 44px action targets. Other wide tables may scroll within their container.
- Use semantic HTML, associated labels, descriptive buttons, keyboard access, visible focus, sufficient contrast, and text alongside status colors.
- Show real loading, empty, offline, and error states. Never invent operational proxy data or present planned actions as working.
- Preserve API form errors, native dialog behavior, CSRF tokens, and authentication controls. Keep browser API calls in the existing JavaScript module.
- Keep page titles useful. Do not add SEO metadata, promotional sections, analytics, external fonts, or decorative packages.
- In Razor C# blocks, use explicit named types as required by `AGENTS.md`.

## Verification

Run `dotnet build -t:BuildCss --no-restore`, then build the MVC project and inspect changed pages at desktop and mobile widths when a browser is available. Include regenerated `wwwroot/app.css` with the change. Keep `.tools/` out of source control and published output.

Verify version-sensitive syntax against Tailwind's official [CLI documentation](https://tailwindcss.com/docs/installation/tailwind-cli) and [class detection documentation](https://tailwindcss.com/docs/detecting-classes-in-source-files). Recheck the installed major version before using current online examples.
