import { createApp } from "vue";
import "./assets/styles/tokens.css";
import "./assets/styles/app.css";
import App from "./App.vue";
import { router } from "./router.js";

createApp(App).use(router).mount("#app");
