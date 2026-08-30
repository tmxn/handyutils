import { EventEmitter } from "node:events";
import { McpChild } from "./mcp-child.js";
import type {
  AppConfig,
  LogEntry,
  ServerConfig,
  ServerStatus,
  ToolDef,
} from "./types.js";

const MAX_LOG_ENTRIES = 500;

/** Runtime state for a single configured MCP server. */
export class ServerRuntime extends EventEmitter {
  cfg: ServerConfig;
  status: ServerStatus = "STOPPED";
  pid: number | null = null;
  tools: ToolDef[] = [];
  logs: LogEntry[] = [];
  lastError: string | null = null;
  child: McpChild | null = null;

  constructor(cfg: ServerConfig) {
    super();
    this.cfg = cfg;
  }

  addLog(level: LogEntry["level"], msg: string): void {
    this.logs.push({ t: Date.now(), level, msg });
    if (this.logs.length > MAX_LOG_ENTRIES) this.logs.shift();
    this.emit("log");
  }

  setStatus(status: ServerStatus, error?: string): void {
    this.status = status;
    this.lastError = error ?? (status === "ERRORED" ? this.lastError : null);
    this.emit("change");
  }

  async start(): Promise<void> {
    if (this.status === "STARTING" || this.status === "RUNNING") return;
    this.setStatus("STARTING");
    this.addLog("INFO", `Starting '${this.cfg.name}'`);
    const child = new McpChild(this.cfg);
    this.child = child;
    child.on("log", (level, msg) => this.addLog(level, msg));
    child.on("exit", (code) => {
      this.tools = [];
      this.pid = null;
      this.child = null;
      if (code !== 0 && this.status !== "STOPPED") {
        this.setStatus("ERRORED", `exited with code ${code}`);
      } else if (this.status !== "STOPPED") {
        this.setStatus("STOPPED");
      } else {
        this.setStatus("STOPPED");
      }
    });
    try {
      const { tools } = await child.start();
      this.tools = tools;
      this.pid = child.child?.pid ?? null;
      this.setStatus("RUNNING");
    } catch (err) {
      this.addLog("ERROR", (err as Error).message);
      this.setStatus("ERRORED", (err as Error).message);
      await child.stop();
      this.child = null;
    }
  }

  async stop(): Promise<void> {
    if (!this.child) {
      this.setStatus("STOPPED");
      return;
    }
    this.setStatus("STOPPED");
    await this.child.stop();
    this.child = null;
    this.tools = [];
    this.pid = null;
  }

  async restart(): Promise<void> {
    await this.stop();
    await this.start();
  }

  toggle(): Promise<void> {
    return this.status === "RUNNING" || this.status === "STARTING"
      ? this.stop()
      : this.start();
  }
}

/** Holds all runtimes and aggregates proxied tool definitions with collision handling. */
export class McpManager extends EventEmitter {
  config: AppConfig;
  runtimes = new Map<string, ServerRuntime>();

  constructor(config: AppConfig) {
    super();
    this.config = config;
    for (const s of config.servers) this.runtimes.set(s.id, new ServerRuntime(s));
    for (const rt of this.runtimes.values()) {
      rt.on("change", () => this.emit("change"));
      rt.on("log", () => this.emit("log"));
    }
  }

  get(id: string): ServerRuntime | undefined {
    return this.runtimes.get(id);
  }

  /**
   * Aggregate tool list across RUNNING servers.
   * Collision policy: "always" prefixes everything, "never" lets duplicates win-first,
   * "auto" (default) only prefixes tools whose bare name collides.
   */
  proxiedTools(): Array<ToolDef & { proxiedName: string }> {
    const policy = this.config.prefixTools ?? "auto";
    const running = [...this.runtimes.values()].filter((r) => r.status === "RUNNING");
    const bareCounts = new Map<string, number>();
    for (const rt of running)
      for (const t of rt.tools)
        bareCounts.set(t.originalName, (bareCounts.get(t.originalName) ?? 0) + 1);

    const out: Array<ToolDef & { proxiedName: string }> = [];
    const used = new Set<string>();
    for (const rt of running) {
      for (const t of rt.tools) {
        let proxied: string;
        if (policy === "always") proxied = `${rt.cfg.id}_${t.originalName}`;
        else if (policy === "never") proxied = t.originalName;
        else
          proxied =
            (bareCounts.get(t.originalName) ?? 0) > 1
              ? `${rt.cfg.id}_${t.originalName}`
              : t.originalName;
        while (used.has(proxied)) proxied = `${rt.cfg.id}_${proxied}`;
        used.add(proxied);
        out.push({ ...t, proxiedName: proxied });
      }
    }
    return out;
  }

  /** Route a proxied tool call to its owning server. */
  async callTool(
    proxiedName: string,
    args: Record<string, unknown>
  ): Promise<{ owner: ServerRuntime; originalName: string }> {
    for (const rt of this.runtimes.values()) {
      if (rt.status !== "RUNNING") continue;
      const proxied = this.proxiedTools().find(
        (t) => t.proxiedName === proxiedName && t.serverId === rt.cfg.id
      );
      if (proxied) return { owner: rt, originalName: proxied.originalName };
    }
    throw new Error(`Unknown tool: ${proxiedName}`);
  }

  async autostart(): Promise<void> {
    const starts = [...this.runtimes.values()]
      .filter((r) => r.cfg.autostart)
      .map((r) => r.start()); // parallel autostart
    await Promise.allSettled(starts);
  }

  async stopAll(): Promise<void> {
    await Promise.allSettled([...this.runtimes.values()].map((r) => r.stop()));
  }
}
