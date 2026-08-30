import http from "node:http";
import { randomUUID } from "node:crypto";
import type { McpManager } from "./manager.js";

const PROTOCOL_VERSION = "2024-11-05";

interface Session {
  res: http.ServerResponse;
  alive: boolean;
}

/**
 * In-process MCP gateway. Wire-compatible with `mcp-proxy`'s HTTP+SSE transport so
 * existing clients (llama-ui, Claude Desktop streamable configs, etc.) can
 * point at a single `http://localhost:<port>/sse` endpoint regardless of how
 * many upstream stdio servers are toggled on.
 */
export class ProxyGateway {
  private server: http.Server;
  private sessions = new Map<string, Session>();
  private keepAlive: NodeJS.Timeout;

  constructor(private manager: McpManager) {
    this.server = http.createServer((req, res) => void this.route(req, res));
    this.keepAlive = setInterval(() => this.ssePing(), 15_000);
  }

  listen(port: number): Promise<void> {
    return new Promise((resolve, reject) => {
      this.server.once("error", reject);
      this.server.listen(port, "127.0.0.1", () => {
        this.server.removeAllListeners("error");
        this.server.on("error", (e) =>
          this.manager.emit("log", "ERROR", `proxy: ${e.message}`)
        );
        resolve();
      });
    });
  }

  private ssePing(): void {
    for (const s of this.sessions.values()) {
      if (!s.alive) continue;
      try {
        s.res.write("event: ping\ndata: {}\n\n");
      } catch {
        s.alive = false;
      }
    }
  }

  async stop(): Promise<void> {
    clearInterval(this.keepAlive);
    for (const s of this.sessions.values()) {
      try {
        s.res.end();
      } catch {
        /* ignore */
      }
    }
    this.sessions.clear();
    this.server.closeAllConnections?.();
    await new Promise<void>((r) => this.server.close(() => r()));
  }

  // ---------------- routing ----------------

  private cors(res: http.ServerResponse): void {
    res.setHeader("Access-Control-Allow-Origin", "*");
    res.setHeader("Access-Control-Allow-Methods", "GET, POST, DELETE, OPTIONS");
    res.setHeader(
      "Access-Control-Allow-Headers",
      "Content-Type, Accept, Authorization, mcp-session-id, mcp-protocol-version"
    );
    res.setHeader("Access-Control-Max-Age", "86400");
    res.setHeader("Access-Control-Expose-Headers", "mcp-session-id, mcp-protocol-version");
  }

  private async route(req: http.IncomingMessage, res: http.ServerResponse): Promise<void> {
    this.cors(res);
    const url = new URL(req.url ?? "/", "http://localhost");

    if (req.method === "OPTIONS") {
      res.writeHead(204).end();
      return;
    }

    // Legacy HTTP+SSE transport (mcp-proxy style): event stream.
    if (req.method === "GET" && url.pathname === "/sse") {
      this.openSse(res);
      return;
    }
    if (req.method === "POST" && url.pathname === "/message") {
      const sessionId = url.searchParams.get("sessionId");
      const body = await readBody(req);
      if (!sessionId || !this.sessions.has(sessionId)) {
        this.json(res, 404, { error: "unknown sessionId — GET /sse first" });
        return;
      }
      void this.handleJsonRpc(body, (payload) => this.sseSend(sessionId, payload));
      res.writeHead(202, { "Content-Type": "application/json" }).end("{}");
      return;
    }

    // Streamable-HTTP-style single-shot endpoint (POST /mcp, JSON response).
    if (req.method === "POST" && (url.pathname === "/mcp" || url.pathname === "/")) {
      const body = await readBody(req);
      await this.handleJsonRpc(body, (payload) => {
        if (!res.headersSent) this.json(res, 200, payload);
      });
      if (!res.headersSent) this.json(res, 202, {}); // notification-only batch
      return;
    }

    if (req.method === "GET" && url.pathname === "/health") {
      const runtimes = [...this.manager.runtimes.values()];
      this.json(res, 200, {
        ok: true,
        servers: runtimes.map((r) => ({
          id: r.cfg.id,
          status: r.status,
          pid: r.pid,
          tools: r.tools.length,
        })),
        proxiedTools: this.manager.proxiedTools().map((t) => t.proxiedName),
      });
      return;
    }

    this.json(res, 404, { error: `no route: ${req.method} ${url.pathname}` });
  }

  private json(res: http.ServerResponse, code: number, body: unknown): void {
    if (res.headersSent) return;
    res.writeHead(code, { "Content-Type": "application/json" });
    res.end(JSON.stringify(body));
  }

  private openSse(res: http.ServerResponse): void {
    const sessionId = randomUUID();
    res.writeHead(200, {
      "Content-Type": "text/event-stream",
      "Cache-Control": "no-cache, no-transform",
      Connection: "keep-alive",
    });
    res.flushHeaders?.();
    this.sessions.set(sessionId, { res, alive: true });
    // mcp-proxy / spec: first event tells the client where to POST.
    res.write(
      `event: endpoint\ndata: /message?sessionId=${sessionId}\n\n`
    );
    const cleanup = () => {
      this.sessions.delete(sessionId);
    };
    res.on("close", cleanup);
    res.on("error", cleanup);
  }

  private sseSend(sessionId: string, payload: unknown): void {
    const s = this.sessions.get(sessionId);
    if (!s?.alive) return;
    try {
      s.res.write(`event: message\ndata: ${JSON.stringify(payload)}\n\n`);
    } catch {
      s.alive = false;
    }
  }

  // ---------------- JSON-RPC server (the proxy IS an MCP server) ----------------

  private async handleJsonRpc(
    rawBody: string,
    reply: (payload: unknown) => void
  ): Promise<void> {
    let msg: any;
    try {
      msg = JSON.parse(rawBody);
    } catch {
      reply(rpcError(null, -32700, "Parse error"));
      return;
    }
    if (Array.isArray(msg)) {
      const answers: unknown[] = [];
      for (const m of msg) {
        const a = await this.dispatch(m);
        if (a) answers.push(a);
      }
      if (answers.length) reply(answers.length === 1 ? answers[0] : answers);
      return;
    }
    const answer = await this.dispatch(msg);
    if (answer) reply(answer);
  }

  /** Returns an RPC response payload, or null for notifications. */
  private async dispatch(msg: any): Promise<unknown | null> {
    const isRequest = msg?.id !== undefined && typeof msg?.method === "string";
    const method = msg?.method as string;

    if (!isRequest) {
      // Notification — nothing to do beyond logging.
      return null;
    }

    const id = msg.id;
    try {
      switch (method) {
        case "initialize":
          return {
            jsonrpc: "2.0",
            id,
            result: {
              protocolVersion: PROTOCOL_VERSION,
              capabilities: { tools: { listChanged: false } },
              serverInfo: { name: "mcp-tui", version: "0.1.0" },
            },
          };
        case "ping":
          return { jsonrpc: "2.0", id, result: {} };
        case "tools/list":
          return {
            jsonrpc: "2.0",
            id,
            result: {
              tools: this.manager.proxiedTools().map((t) => ({
                name: t.proxiedName,
                description: t.description,
                inputSchema: t.inputSchema ?? { type: "object", properties: {} },
              })),
            },
          };
        case "tools/call": {
          const name: string = msg?.params?.name;
          const args: Record<string, unknown> = msg?.params?.arguments ?? {};
          if (!name) return rpcError(id, -32602, "tools/call: missing name");
          let route;
          try {
            route = await this.manager.callTool(name, args);
          } catch (err) {
            return rpcError(id, -32602, (err as Error).message);
          }
          try {
            const result = await route.owner.child!.request("tools/call", {
              name: route.originalName,
              arguments: args,
            });
            return { jsonrpc: "2.0", id, result };
          } catch (err) {
            // Tool execution errors travel inside the JSON-RPC result per spec.
            return {
              jsonrpc: "2.0",
              id,
              result: {
                isError: true,
                content: [{ type: "text", text: String((err as Error).message) }],
              },
            };
          }
        }
        default:
          return rpcError(id, -32601, `Method not found: ${method}`);
      }
    } catch (err) {
      return rpcError(id, -32603, (err as Error).message);
    }
  }
}

function rpcError(id: unknown, code: number, message: string): unknown {
  return { jsonrpc: "2.0", id, error: { code, message } };
}

async function readBody(req: http.IncomingMessage): Promise<string> {
  const chunks: Buffer[] = [];
  let size = 0;
  for await (const c of req) {
    size += (c as Buffer).length;
    if (size > 10 * 1024 * 1024) throw new Error("body too large");
    chunks.push(c as Buffer);
  }
  return Buffer.concat(chunks).toString("utf8");
}
