import { defineConfig } from "vite";
import { resolve } from "path";

// base: "./" makes built asset paths relative, so the site works when served
// from a GitHub Pages sub-path like https://user.github.io/FsLemming/.
// Two pages: the game (index.html) and the level editor (editor.html).
export default defineConfig({
  base: "./",
  build: {
    outDir: "dist",
    rollupOptions: {
      input: {
        main: resolve(__dirname, "index.html"),
        editor: resolve(__dirname, "editor.html"),
      },
    },
  },
});
