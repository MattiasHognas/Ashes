import { createRouter, createWebHistory } from "vue-router";
import HomeView from "./views/HomeView.vue";
import PackageView from "./views/PackageView.vue";
import PackagesView from "./views/PackagesView.vue";

export default createRouter({
  history: createWebHistory(),
  routes: [
    { path: "/", name: "home", component: HomeView },
    { path: "/packages", name: "packages", component: PackagesView },
    {
      path: "/packages/:namespace/:version?",
      name: "package",
      component: PackageView,
      props: true,
    },
    { path: "/:pathMatch(.*)*", redirect: "/" },
  ],
  scrollBehavior(to, from, savedPosition) {
    if (savedPosition) {
      return savedPosition;
    }

    if (to.path !== from.path) {
      return { top: 0 };
    }

    return {};
  },
});
