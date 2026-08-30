import { loadConfig } from "../dist/config.js";
import { McpManager } from "../dist/manager.js";
import { ProxyGateway } from "../dist/proxy.js";

const BASE = "http://127.0.0.1:19090";
let failures = 0;
const check = (name, cond, extra = "") => {
  console.log(`${cond ? "PASS" : "FAIL"}  ${name} ${cond ? "" : extra}`);
  if (!cond) failures++;
};

const { config } = loadConfig("test/test-config.json");
const manager = new McpManager(config);
const gateway = new ProxyGateway(manager);
await gateway.listen(config.port);
await manager.autostart();

// ---- /health ----------------------------------------------------------------
let res = await fetch(`${BASE}/health`);
let health = await res.json();
check("health: 2 running", health.servers.filter((s) => s.status === "RUNNING").length === 2);
check(
  "collision auto-prefixed tools",
  JSON.stringify(health.proxiedTools.sort()) ===
    JSON.stringify(["toy-a_echo", "toy-a_slow", "toy-b_echo", "toy-b_slow"])
);

// ---- SSE session -------------------------------------------------------------
const sseRes = await fetch(`${BASE}/sse`);
const reader = sseRes.body.getReader();
const dec = new TextDecoder();
const first = dec.decode((await reader.read()).value);
const sessionId = first.match(/sessionId=([\w-]+)/)?.[1];
check("sse: endpoint event advertises sessionId", !!sessionId, first);

let reqId = 0;
const rpc = async (method, params) => {
  const id = ++reqId;
  await fetch(`${BASE}/message?sessionId=${sessionId}`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ jsonrpc: "2.0", id, method, params }),
  });
  // read frames until we see our id
  for (;;) {
    const { value, done } = await reader.read();
    if (done) throw new Error("sse closed");
    const text = dec.decode(value);
    for (const m of text.matchAll(/data: (.+)\n/g)) {
      const msg = JSON.parse(m[1]);
      if (msg.id === id) return msg;
    }
  }
};

let out = await rpc("initialize", { protocolVersion: "2024-11-05", capabilities: {}, clientInfo: { name: "test", version: "0" } });
check("sse: initialize", out.result?.serverInfo?.name === "mcp-tui", JSON.stringify(out));
await fetch(`${BASE}/message?sessionId=${sessionId}`, {
  method: "POST",
  body: JSON.stringify({ jsonrpc: "2.0", method: "notifications/initialized", params: {} }),
});

out = await rpc("tools/call", { name: "toy-b_echo", arguments: { text: "hi" } });
check("routing: call hits toy B", out.result?.content?.[0]?.text === "B:hi", JSON.stringify(out));

out = await rpc("tools/call", { name: "echo", arguments: { text: "x" } });
check("unknown bare name rejected", !!out.error, JSON.stringify(out));

out = await rpc("tools/call", { name: "toy-a_slow", arguments: { ms: 400 } });
check("async tool call result", out.result?.content?.[0]?.text === "done", JSON.stringify(out));

// ---- single-shot /mcp ---------------------------------------------------------
res = await fetch(`${BASE}/mcp`, {
  method: "POST",
  headers: { "Content-Type": "application/json" },
  body: JSON.stringify({ jsonrpc: "2.0", id: 99, method: "tools/list", params: {} }),
});
out = await res.json();
check("POST /mcp single-shot tools/list", out.result?.tools?.length === 4);

// ---- CORS preflight -----------------------------------------------------------
res = await fetch(`${BASE}/sse`, { method: "OPTIONS" });
check("CORS preflight 204", res.status === 204 && res.headers.get("access-control-allow-origin") === "*");

// ---- toggle off -> tree killed, tools shrink -----------------------------------
const rtA = manager.get("toy-a");
const pidA = rtA.pid;
await rtA.stop();
await new Promise((r) => setTimeout(r, 800));
check("toy-a STOPPED", rtA.status === "STOPPED");
const procAlive = (() => {
  try {
    process.kill(pidA, 0);
    return true;
  } catch {
    return false;
  }
})();
check("toy-a process tree dead", !procAlive, `pid ${pidA} still alive`);

res = await fetch(`${BASE}/health`);
health = await res.json();
check("tools/list shrinks after toggle", health.proxiedTools.length === 2);

// ---- restart toy-a via manager --------------------------------------------------
await rtA.restart();
check("toy-a back RUNNING", rtA.status === "RUNNING");
res = await fetch(`${BASE}/health`);
check("tools back after restart", (await res.json()).proxiedTools.length === 4);

// ---- log capture -----------------------------------------------------------------
check("logs captured (stderr relay)", rtA.logs.some((l) => l.level === "STDERR" && l.msg.includes("[A]")));

await gateway.stop();
await manager.stopAll();
console.log(failures ? `\n${failures} FAILURE(S)` : "\nALL PASS");
process.exit(failures ? 1 : 0);
