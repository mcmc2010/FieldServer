import { defineConfig } from "vite";

// 开发服务器把 HTTP 调试端点和 WebSocket 都代理到 FieldServer，
// 前端代码里用相对路径即可，避免 fetch 跨域问题。
export default defineConfig({
  server: {
    port: 5173,
    proxy: {
      "/ws": { target: "ws://127.0.0.1:5000", ws: true },
      "/rooms": "http://127.0.0.1:5000",
      "/battles": "http://127.0.0.1:5000",
      "/movement": "http://127.0.0.1:5000",
    },
  },
});
