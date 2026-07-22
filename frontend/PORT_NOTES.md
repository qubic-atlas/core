# Qubic Atlas — Vue 3 frontend (port notes)

A 1:1 port of the React reference (`../web/src`) to **Vue 3 + Vite**, with all styling moved
into a **central CSS class system** (no inline design values). Same dark aesthetic, same layout,
same canvas visualizations — just Vue components and semantic classes.

## Naming (Atlas / Proofs)
- Product is **Qubic Atlas** (brand, `<title>`, docs, hero copy).
- A mining "solution" is a **Proof** in the UI. Routes are `/proofs` and `/proofs/:hash`; the nav
  label is **Proofs**. `/solutions` and `/solutions/:hash` redirect to the new paths (query preserved).
- **API paths keep `/api/solutions...`** (backend contract) — only UI copy renames to "Proof".
- **"genome"** is kept as the technical term (`annGenomeId`, "genome hash"); newcomer copy says
  "the trained network".

## Structure
```
frontend/
  index.html                 # mounts #app; <title>Qubic Atlas</title>
  vite.config.js             # dev + preview /api proxy → $API_TARGET (default :8096)
  Dockerfile                 # node build → nginx (multi-stage)
  nginx.conf                 # envsubst TEMPLATE (SPA + /api proxy)
  .dockerignore
  src/
    main.js                  # createApp + router; imports tokens.css then app.css
    App.vue                  # <Header/> + <router-view/>
    router.js                # vue-router; /proofs routes + legacy redirects
    api.js                   # fetch wrapper, short(), fmt(), FEATURED hash
    assets/styles/
      tokens.css             # design tokens (CSS variables) — the ONLY place colors are defined
      app.css                # central component stylesheet (all classes live here)
    pages/
      Overview.vue           # hero + live stats + "how mining works" + two cards
      Proofs.vue             # (was Solutions) list + filters + pager
      ProofDetail.vue        # (was SolutionDetail) verify banner, metrics, AnnViews, MutationReplay
      Epochs.vue
      Computors.vue
      Docs.vue
    components/
      Header.vue             # sticky nav + live epoch/tick pills (polls /api/live/tick-info)
      HeroNet.vue            # decorative animated hero canvas
      Explainer.vue          # collapsible "what is Qubic computing here"
      AnnViews.vue           # Flow/Radial/Matrix switch (reads ?view=) + Inspector
      NeuralGraph.vue        # animated layered signal-flow canvas (Flow)
      RadialView.vue         # chord canvas (Radial)
      MatrixView.vue         # 56×56 weight heatmap canvas (Matrix)
      Inspector.vue          # per-neuron wiring panel
      MutationReplay.vue     # score chart + scrubber + play + narration
```

## The CSS system (central, no inline styles)
Two stylesheets, imported once in `main.js`:

1. **`tokens.css`** — a single `:root` block of CSS variables ported from the reference
   `styles.css` `:root` (`--bg #0b1016`, `--panel`, `--panel2`, `--line #22344a`, `--txt #dce7f2`,
   `--dim #8397ac`, `--accent #00e0c6`, `--accent2 #3a9bff`, `--good`, `--bad`, `--warn`, `--evo`,
   …). The reference expressed several colors as inline literals (neuron colors, pulse colors, chip
   tints); those are promoted to named tokens here (`--neuron-input/output/evolution`,
   `--syn-pos/neg`, `--pulse-pos/neg`, `--ink`, etc.). **Nothing else in the app hard-codes a color.**

2. **`app.css`** — every visual rule. First half is the reference `styles.css` ported ~verbatim
   (`.panel`, `.stat`, `.chip`, `.banner`, tables, header, responsive block). Second half replaces
   the reference's `style={{…}}` with **semantic BEM-ish classes**: `.hero__title`, `.step-card--teal`,
   `.view-switch__tab--active`, `.banner--info`, `.mixbar__pos`, `.inspector__foot`, `.narrate--ok`,
   `.tooltip`, `.canvas-el`, `.sw--input`, etc. A handful of tiny spacing utilities
   (`.mt-16`, `.mb-26`, …) map to the reference's exact literal margins.

**Inline `:style` is used in exactly four places, and only for genuinely dynamic geometry that
cannot be a class:**
- tooltip `left/top` in `NeuralGraph`, `RadialView`, `MatrixView` (follows the cursor);
- the connection-mix bar `width: N%` in `Inspector` (computed from link counts).

No literal colors, fonts, or fixed spacings appear in any template. Colored swatches, tab-active
states, step-card accents, banner variants, and score-match text colors are all classes.

## Canvas visualizations
The drawing / animation / hit-testing logic is framework-agnostic JS and was **ported verbatim**
from the React components. Each is wrapped in an SFC using a `<canvas ref>`, with `onMounted` for
setup + RAF start and `onBeforeUnmount` for teardown; `watch` re-inits when `graph`/`selected`
change (mirroring the React effect deps). Behaviour preserved:
- **NeuralGraph (Flow)** — layered layout handling BOTH a 1024-neuron HyperIdentity net (no grown
  neurons → wide input/output bands) and a 256-neuron Addition net (14 in / 8 out / 234 grown center
  cloud); animated signal pulses; pause/play; hover tooltip; forgiving click hit-test → select.
- **RadialView** — neurons on a ring by type, synapses as chords through the centre; selecting a
  neuron brightens its chords.
- **MatrixView** — GxG weight heatmap from `graph.matrix` (teal +, red −), axis labels, hover.
- **Inspector** — id/type/value, in/out degree, strengthening vs inhibiting counts and neighbor
  chips from `recon.graph.links`, with the "Based on N of M synapses" note.
- **MutationReplay** — best-score line + candidate dots + dashed threshold, scrubber, play,
  plain-language narration, and the threshold-crossing callout.

## Responsive
Verified 360 → 1440px (measured via CDP: `document.scrollWidth === innerWidth` at 360/375, no body
overflow). Header nav wraps and the live pills hide < 640px; canvases are `max-width:100%` with
hit-testing scaled by the canvas's rendered width; stat grids reflow via `auto-fit` minmax; tables
live in `.table-wrap` (`overflow-x:auto`, `min-width` on the table) so they scroll internally
instead of stretching the body; the hero font uses `clamp()`; `.kv` metadata grids collapse to a
single column on mobile.

## Dev / test
```bash
# 1) backend (reference Node server, same contract)
cd ../server && PORT=8096 node index.js

# 2a) dev
npm install && npm run dev            # http://localhost:5174, proxies /api → :8096

# 2b) or preview a production build
npm run build && npm run preview      # http://localhost:4174, proxies /api → :8096
```
Override the proxy target with `API_TARGET=http://host:port`.

## Docker
Multi-stage build (node → nginx):
```bash
docker build -t qubic-atlas .
# ATLAS_UPSTREAM = the backend host:port the /api proxy forwards to (default :8099)
docker run -e ATLAS_UPSTREAM=host.docker.internal:8099 -p 8080:80 qubic-atlas
```
`nginx.conf` is installed as an **envsubst template** (`/etc/nginx/templates/default.conf.template`);
the official nginx image expands `${ATLAS_UPSTREAM}` at container start (nginx's own `$host`/`$uri`
are preserved because they aren't env vars). nginx serves `dist/`, long-caches `/assets/`, proxies
`/api/` (120s read timeout for slow HyperIdentity verifies), and falls back to `index.html` for SPA
routes. Verified end-to-end: static serve, SPA fallback (`/proofs` → 200), and `/api` proxy return
live data from the backend.

## Deviations from the reference (all intentional)
- **Naming**: Explorer → **Atlas**, Solution(s) → **Proof(s)** in all UI copy, routes, nav, table
  headers, and page titles. API paths, `annGenomeId`, and the on-chain transaction description are
  unchanged.
- **No inline styles**: every `style={{…}}` became a class in `app.css` (except the 4 dynamic-geometry
  `:style` bindings noted above). This is the core requirement of the port.
- **Router active state**: uses `$route.path` matching instead of React Router's `NavLink isActive`.
- **Legacy `/solutions` redirects** added so old links keep working.
- **Vite `preview.proxy`** added (dev `server.proxy` is dev-only) so a production build can be
  smoke-tested against the backend.
- Framework libs swapped (React/react-router-dom → Vue 3/vue-router). No behavioural change.
