import { fileURLToPath } from 'node:url';
import solid from 'vite-plugin-solid';
import { defineConfig } from 'vite';

const here = (path: string) => fileURLToPath(new URL(path, import.meta.url));

export default defineConfig({
  plugins: [solid()],
  resolve: {
    alias: {
      // The client of Routeables.SignalR is used from its sources: there is nothing to build or publish first.
      '@routeables/client': here('../../../clients/typescript/src/index.ts'),
      // Its sources are outside this folder: SignalR is the one of this app.
      '@microsoft/signalr': here('node_modules/@microsoft/signalr'),
    },
  },
  server: {
    port: 5176,
    strictPort: true,
    // The client sources are in clients/typescript.
    fs: { allow: [here('../../..')] },
    // /routeables is the hub of Routeables.SignalR: the server of the Angular sample (XAnimals.AngularServer) answers it.
    proxy: { '/routeables': { target: 'http://localhost:5090', ws: true, changeOrigin: false } },
  },
  preview: {
    port: 5176,
    proxy: { '/routeables': { target: 'http://localhost:5090', ws: true, changeOrigin: false } },
  },
});
