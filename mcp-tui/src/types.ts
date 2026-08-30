/** Configuration schema for mcp-config.json */
export interface ServerConfig {
  id: string;
  name: string;
  command: string;
  args: string[];
  autostart: boolean;
  /** Optional extra environment variables */
  env?: Record<string, string>;
  /** Optional working directory */
  cwd?: string;
}

export interface AppConfig {
  port: number;
  /** Prefix tool names with "<id>_" to avoid collisions (default: only on collision) */
  prefixTools?: "always" | "never" | "auto";
  servers: ServerConfig[];
}

export type ServerStatus = "STOPPED" | "STARTING" | "RUNNING" | "ERRORED";

export type LogLevel = "INFO" | "STDERR" | "STDIO" | "RPC" | "ERROR";

export interface LogEntry {
  /** epoch ms */
  t: number;
  level: LogLevel;
  msg: string;
}

export interface ToolDef {
  name: string; // proxied (possibly prefixed) name
  originalName: string;
  serverId: string;
  description?: string;
  inputSchema?: Record<string, unknown>;
}
