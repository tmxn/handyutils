import fs from "node:fs";
import path from "node:path";
import type { AppConfig, ServerConfig } from "./types.js";

export const DEFAULT_CONFIG_PATH =
  process.env.MCP_TUI_CONFIG ?? path.resolve(process.cwd(), "mcp-config.json");

export function defaultConfig(): AppConfig {
  return {
    port: 9090,
    prefixTools: "auto",
    servers: [
      {
        id: "file-system",
        name: "File Access",
        command: "npx",
        args: ["-y", "@modelcontextprotocol/server-filesystem", "S:\\FastGitRoot\\appmain"],
        autostart: true,
      },
      {
        id: "windows-desktop",
        name: "Desktop Control",
        command: "uvx",
        args: ["windows-mcp", "serve"],
        autostart: false,
      },
      {
        id: "bash-shell",
        name: "Bash Execution",
        command: "npx",
        args: ["-y", "bash-mcp"],
        autostart: false,
      },
    ],
  };
}

export function loadConfig(configPath: string): {
  config: AppConfig;
  created: boolean;
} {
  if (!fs.existsSync(configPath)) {
    const cfg = defaultConfig();
    saveConfig(configPath, cfg);
    return { config: cfg, created: true };
  }
  const raw = fs.readFileSync(configPath, "utf8");
  const config = JSON.parse(raw) as AppConfig;
  validateConfig(config);
  return { config, created: false };
}

export function saveConfig(configPath: string, config: AppConfig): void {
  fs.mkdirSync(path.dirname(configPath), { recursive: true });
  fs.writeFileSync(configPath, JSON.stringify(config, null, 2) + "\n");
}

export function validateConfig(config: unknown): asserts config is AppConfig {
  const c = config as AppConfig;
  if (!c || typeof c !== "object") throw new Error("config: object expected");
  if (typeof c.port !== "number" || c.port < 1 || c.port > 65535)
    throw new Error("config.port must be a number 1-65535");
  if (!Array.isArray(c.servers)) throw new Error("config.servers must be an array");
  const ids = new Set<string>();
  for (const s of c.servers) validateServer(s, ids);
}

function validateServer(s: ServerConfig, ids: Set<string>): void {
  if (!s || typeof s !== "object") throw new Error("server entry must be an object");
  if (!s.id || typeof s.id !== "string") throw new Error("server.id missing");
  if (ids.has(s.id)) throw new Error(`duplicate server id: ${s.id}`);
  ids.add(s.id);
  if (typeof s.command !== "string" || !s.command)
    throw new Error(`server ${s.id}: command missing`);
  if (!Array.isArray(s.args)) s.args = [];
  if (typeof s.name !== "string" || !s.name) s.name = s.id;
  if (typeof s.autostart !== "boolean") s.autostart = false;
}
