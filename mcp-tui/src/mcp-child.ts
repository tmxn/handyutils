import { spawn, type ChildProcess } from "node:child_process";
import { EventEmitter } from "node:events";
import treeKill from "tree-kill";
import type { LogLevel, ServerConfig, ToolDef } from "./types.js";

const PROTOCOL_VERSION = "2024-11-05";
const INITIALIZE_TIMEOUT_MS = 30_000;
const REQUEST_TIMEOUT_MS = 60_000;

type Pending = {
  resolve: (v: unknown) => void;
  reject: (e: Error) => void;
  timer: NodeJS.Timeout;
  method: string;
};

/**
 * Minimal MCP client speaking newline-delimited JSON-RPC over a child
 * process's stdio pipes. No SDK, no extra framing — keeps footprint small.
 */
export class McpChild extends EventEmitter {
  readonly cfg: ServerConfig;
  child: ChildProcess | null = null;
  private nextId = 1;
  private pending = new Map<number | string, Pending>();
  private stdoutBuf = "";
  private stderrBuf = "";
  private killed = false;

  constructor(cfg: ServerConfig) {
    super();
    this.cfg = cfg;
  }

  log(level: LogLevel, msg: string): void {
    this.emit("log", level, msg);
  }

  /** Spawn the child and perform the MCP initialize + tools/list handshake. */
  async start(): Promise<{ tools: ToolDef[] }> {
    const { command, useShell } = resolveCommand(this.cfg.command);
    const args = this.cfg.args ?? [];

    this.log("INFO", `Spawning: ${command} ${args.join(" ")}`);
    let child: ChildProcess;
    try {
      if (useShell) {
        // Windows .cmd/.bat shims (npx, uvx, ...) cannot be spawned directly;
      // build one fully-quoted command string for cmd.exe (still tree-killed).
        const full = [command, ...args].map(shellQuote).join(" ");
        child = spawn(full, {
          cwd: this.cfg.cwd,
          env: { ...process.env, ...this.cfg.env },
          shell: true,
          windowsHide: true,
        });
      } else {
        child = spawn(command, args, {
          cwd: this.cfg.cwd,
          env: { ...process.env, ...this.cfg.env },
          windowsHide: true,
          shell: false,
        });
      }
    } catch (err) {
      throw new Error(`spawn failed: ${(err as Error).message}`);
    }

    this.child = child;
    child.stdin!.setDefaultEncoding("utf8");

    child.stdout!.on("data", (chunk: Buffer) => this.onStdout(chunk));
    child.stderr!.on("data", (chunk: Buffer) => this.onStderr(chunk));

    child.on("error", (err) => {
      this.log("ERROR", `Process error: ${err.message}`);
      this.emit("exit", -1, err.message);
    });
    child.on("exit", (code, signal) => {
      this.flushBuffers();
      const why = signal ? `signal ${signal}` : `exit code ${code}`;
      this.log(code === 0 || this.killed ? "INFO" : "ERROR", `Process exited (${why})`);
      for (const p of this.pending.values()) {
        clearTimeout(p.timer);
        p.reject(new Error(`process exited (${why}) during ${p.method}`));
      }
      this.pending.clear();
      this.emit("exit", code ?? -1);
    });

    // --- MCP handshake ---
    await withTimeout(
      this.request("initialize", {
        protocolVersion: PROTOCOL_VERSION,
        capabilities: {},
        clientInfo: { name: "mcp-tui", version: "0.1.0" },
      }),
      INITIALIZE_TIMEOUT_MS,
      "initialize"
    );
    this.notify("notifications/initialized", {});
    const res = (await this.request("tools/list", {})) as {
      tools?: Array<Partial<ToolDef> & { name: string }>;
    };
    const tools = (res.tools ?? [])
      .filter((t) => typeof t.name === "string")
      .map((t) => ({
        name: t.name,
        originalName: t.name,
        serverId: this.cfg.id,
        description: t.description,
        inputSchema: t.inputSchema as Record<string, unknown> | undefined,
      }));
    this.log("INFO", `Handshake complete — ${tools.length} tool(s)`);
    return { tools };
  }

  /** Graceful-ish then forced tree kill. */
  stop(): Promise<void> {
    const child = this.child;
    if (!child || child.exitCode !== null || this.killed) return Promise.resolve();
    this.killed = true;
    const pid = child.pid;
    this.log("INFO", `Terminating process tree (PID ${pid})`);
    return new Promise((resolve) => {
      // Windows needs the taskkill-based tree-kill; POSIX gets SIGTERM first.
      if (process.platform === "win32") {
        treeKill(pid!, undefined, () => resolve());
      } else {
        try {
          child.kill("SIGTERM");
        } catch {
          /* ignore */
        }
        setTimeout(() => treeKill(pid!, "SIGKILL", () => resolve()), 3000);
      }
    });
  }

  request(method: string, params: unknown, timeoutMs = REQUEST_TIMEOUT_MS): Promise<unknown> {
    if (!this.child || !this.child.stdin || this.child.stdin.destroyed) {
      return Promise.reject(new Error("not connected"));
    }
    const id = this.nextId++;
    const payload = { jsonrpc: "2.0", id, method, params };
    this.log("RPC", `--> ${method} #${id}`);
    return new Promise((resolve, reject) => {
      const timer = setTimeout(() => {
        this.pending.delete(id);
        reject(new Error(`${method} timed out after ${timeoutMs}ms`));
      }, timeoutMs);
      this.pending.set(id, { resolve, reject, timer, method });
      this.WriteLine(JSON.stringify(payload));
    });
  }

  notify(method: string, params: unknown): void {
    if (!this.child?.stdin || this.child.stdin.destroyed) return;
    this.WriteLine(JSON.stringify({ jsonrpc: "2.0", method, params }));
  }

  private WriteLine(line: string): void {
    try {
      this.child!.stdin!.write(line + "\n");
    } catch (err) {
      this.log("ERROR", `write failed: ${(err as Error).message}`);
    }
  }

  // --- stdout parsing: newline-delimited JSON-RPC, non-JSON -> STDIO log ---
  private onStdout(chunk: Buffer): void {
    this.stdoutBuf += chunk.toString("utf8");
    let idx: number;
    while ((idx = this.stdoutBuf.search(/\r?\n/)) !== -1) {
      const line = this.stdoutBuf.slice(0, idx);
      this.stdoutBuf = this.stdoutBuf.slice(idx + 1);
      if (!line.trim()) continue;
      if (line.startsWith("Content-Length:")) continue; // tolerate LSP-ish framing
      let msg: AnyMsg;
      try {
        msg = JSON.parse(line) as AnyMsg;
      } catch {
        this.log("STDIO", line);
        continue;
      }
      this.handleMessage(msg);
    }
  }

  private onStderr(chunk: Buffer): void {
    this.stderrBuf += chunk.toString("utf8");
    let idx: number;
    while ((idx = this.stderrBuf.search(/\r?\n/)) !== -1) {
      const line = this.stderrBuf.slice(0, idx);
      this.stderrBuf = this.stderrBuf.slice(idx + 1);
      if (line.trim()) this.log("STDERR", line);
    }
  }

  private flushBuffers(): void {
    if (this.stdoutBuf.trim()) this.log("STDIO", this.stdoutBuf.trim());
    if (this.stderrBuf.trim()) this.log("STDERR", this.stderrBuf.trim());
    this.stdoutBuf = this.stderrBuf = "";
  }

  private handleMessage(msg: AnyMsg): void {
    if (msg.id !== undefined && msg.method === undefined) {
      // Response to one of our requests.
      const p = this.pending.get(msg.id as number | string);
      if (!p) return;
      this.pending.delete(msg.id as number | string);
      clearTimeout(p.timer);
      if (msg.error)
        p.reject(new Error(`${p.method}: ${msg.error.message ?? JSON.stringify(msg.error)}`));
      else p.resolve(msg.result ?? null);
      return;
    }

    if (msg.method !== undefined) {
      if (msg.method.startsWith("notifications/")) {
        if (msg.method !== "notifications/initialized")
          this.log("RPC", `<-- ${msg.method}`);
        return;
      }
      this.log("RPC", `<-- request ${msg.method}`);
      // Answer server-originated requests minimally.
      if (msg.id !== undefined) {
        if (msg.method === "ping") {
          this.WriteLine(JSON.stringify({ jsonrpc: "2.0", id: msg.id, result: {} }));
        } else {
          this.WriteLine(
            JSON.stringify({
              jsonrpc: "2.0",
              id: msg.id,
              error: { code: -32601, message: "Method not found" },
            })
          );
        }
      }
    }
  }
}

type AnyMsg = {
  jsonrpc?: string;
  id?: number | string;
  method?: string;
  params?: unknown;
  result?: unknown;
  error?: { code?: number; message?: string };
};

function withTimeout<T>(p: Promise<T>, ms: number, label: string): Promise<T> {
  let timer: NodeJS.Timeout;
  return Promise.race([
    p.finally(() => clearTimeout(timer)),
    new Promise<never>((_, rej) => {
      timer = setTimeout(() => rej(new Error(`${label} timed out after ${ms}ms`)), ms);
    }),
  ]);
}

function shellQuote(s: string): string {
  return /[^A-Za-z0-9._\-/\\:@^%]/.test(s) ? `"${s.replace(/["^&|<>]/g, (c) => `^${c}`)}"` : s;
}

/**
 * On Windows, resolve whether the command is actually a .cmd/.bat shim
 * (npx, uvx, pip, ...) which must go through cmd.exe; everything else
 * (node, python, .exe, absolute paths) is spawned directly with no wrapper.
 */
function resolveCommand(command: string): { command: string; useShell: boolean } {
  if (process.platform !== "win32") return { command, useShell: false };
  const lower = command.toLowerCase();
  if (lower.endsWith(".exe") || command.includes("\\")) return { command, useShell: false };
  const shimNames = new Set([
    "npx", "npm", "uvx", "uv", "pip", "pip3", "conda", "yarn", "pnpm", "deno", "bun", "poetry",
  ]);
  return { command, useShell: shimNames.has(lower) };
}
