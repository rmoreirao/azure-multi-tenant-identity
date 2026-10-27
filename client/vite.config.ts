import react from "@vitejs/plugin-react";
import { defineConfig } from "vite";

export default defineConfig({
  plugins: [react()],
  build: {
    outDir: "../src/Kanban.Web/wwwroot",
    emptyOutDir: true,
    sourcemap: false,
  },
});
