import { createRouter, createWebHistory } from "vue-router";
import Overview from "./pages/Overview.vue";
import Proofs from "./pages/Proofs.vue";
import ProofDetail from "./pages/ProofDetail.vue";
import Epochs from "./pages/Epochs.vue";
import Computors from "./pages/Computors.vue";
import ComputorDetail from "./pages/ComputorDetail.vue";
import Network from "./pages/Network.vue";
import RunVerifier from "./pages/RunVerifier.vue";
import Docs from "./pages/Docs.vue";

// A "solution" is a Proof in the UI → routes are /proofs and /proofs/:hash.
export const router = createRouter({
  history: createWebHistory(),
  routes: [
    { path: "/", component: Overview },
    { path: "/proofs", component: Proofs },
    { path: "/proofs/:hash", component: ProofDetail },
    { path: "/epochs", component: Epochs },
    { path: "/computors", component: Computors },
    { path: "/computors/:id", component: ComputorDetail },
    { path: "/network", component: Network },
    { path: "/run-verifier", component: RunVerifier },
    { path: "/docs", component: Docs },
    // legacy redirects so old /solutions links keep working
    { path: "/solutions", redirect: (to) => ({ path: "/proofs", query: to.query }) },
    { path: "/solutions/:hash", redirect: (to) => ({ path: `/proofs/${to.params.hash}`, query: to.query }) },
  ],
  scrollBehavior() { return { top: 0 }; },
});
