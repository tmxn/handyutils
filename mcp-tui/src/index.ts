#!/usr/bin/env node
import React from "react";
import { render } from "ink";
import { loadConfig, DEFAULT_CONFIG_PATH } from "./config.js";
import { McpManager } from "./manager.js";
import { ProxyGateway } from "./proxy.js";
import { App } from "./ui/App.js";

// Alternate-screen buffer: keeps the TUI self-contained so terminal
// scrollback (and mouse-wheel / arrow scrolling) never reveals old frames.
const ENTER_ALT = "\x1b[?1049h";
const LEAVE_ALT = "\x1b[?1049l";
let altRestored = false;
function restoreScreen(): void {
  if (altRestored) return;
  altRestored = true;
  try {
    process.stdout.write(LEAVE_ALT);
  } catch {
    /* ignore */
  }
}
process.on("exit", restoreScreen);
process.on("SIGINT", () => {
  restoreScreen();
  process.exit(0);
});

async function main(): Promise<void> {
  const args = process.argv.slice(2);
  const cfgIdx = args.findIndex((a) => a === "--config" || a === "-c");
  const configPath =
    cfgIdx !== -1 && args[cfgIdx + 1]
      ? args[cfgIdx + 1]
      : DEFAULT_CONFIG_PATH;

  let loaded;
  try {
    loaded = loadConfig(configPath);
  } catch (err) {
    console.error(`mcp-tui: failed to load ${configPath}: ${(err as Error).message}`);
    process.exit(1);
  }
  const { config, created } = loaded;

  const manager = new McpManager(config);
  const gateway = new ProxyGateway(manager);

  try {
    await gateway.listen(config.port);
  } catch (err) {
    console.error(
      `mcp-tui: cannot listen on port ${config.port}: ${(err as Error).message}` +
        `\n(likely a leftover mcp-proxy instance — stop it first; that is exactly what this tool replaces)`
    );
    process.exit(1);
  }

  // Kick off autostart servers without blocking the UI render.
  void manager.autostart();

  try {
    process.stdout.write(ENTER_ALT);
  } catch {
    /* ignore */
  }

  const app = render(
    React.createElement(App, {
      manager,
      proxyUrl: `http://localhost:${config.port}/sse`,
    })
  );

  await app.waitUntilExit();
  await manager.stopAll();
  await gateway.stop();
  restoreScreen();
  if (created) console.log(`(created default config at ${configPath})`);
}

process.on("uncaughtException", (err) => {
  process.stderr.write(`\nmcp-tui: uncaught exception: ${err.stack ?? err}\n`);
});

main().catch((err) => {
  console.error("mcp-tui:", err);
  process.exit(1);
});
