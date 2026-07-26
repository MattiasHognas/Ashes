import { createApp } from "vue";
import App from "./App.vue";
import router from "./router";
import "./styles.css";
import { initializeTheme } from "./theme";

initializeTheme();
createApp(App).use(router).mount("#app");
