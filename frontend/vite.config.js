import { defineConfig } from "vite";
import vue from "@vitejs/plugin-vue";

// Dev proxy sends /api to a running backend. For local testing, start the reference
// Node server (server/index.js) on 8096 — it implements the same contract:
//   cd ../server && PORT=8096 node index.js
const API_TARGET = process.env.API_TARGET || "http://localhost:8096";

export default defineConfig({
  plugins: [vue()],
  server: {
    port: 5174,
    proxy: { "/api": API_TARGET }
  },
  // `vite preview` uses its own proxy block (server.proxy is dev-only).
  preview: {
    port: 4174,
    proxy: { "/api": API_TARGET }
  },
  build: { outDir: "dist" }
});
