// Minimal stdio MCP server used for testing mcp-tui.
// Usage: node toy-mcp-server.mjs [tag]
import readline from "node:readline";

const tag = process.argv[2] ?? "toy";

const tools = [
  {
    name: "echo",
    description: `Echo back text (${tag})`,
    inputSchema: { type: "object", properties: { text: { type: "string" } } },
  },
  {
    name: "slow",
    description: "Sleep then report",
    inputSchema: { type: "object", properties: { ms: { type: "number" } } },
  },
];

const lines = readline.createInterface({ input: process.stdin });
lines.on("line", async (line) => {
  let msg;
  try {
    msg = JSON.parse(line);
  } catch {
    return;
  }
  const { id, method, params } = msg;
  const send = (payload) =>
    process.stdout.write(JSON.stringify({ jsonrpc: "2.0", ...payload }) + "\n");

  process.stderr.write(`[${tag}] got ${method ?? "notification"}\n`);

  if (method === "initialize")
    return send({
      id,
      result: {
        protocolVersion: "2024-11-05",
        capabilities: { tools: {} },
        serverInfo: { name: `toy-${tag}`, version: "1.0" },
      },
    });
  if (method === "tools/list") return send({ id, result: { tools } });
  if (method === "tools/call") {
    const name = params?.name;
    if (name === "echo")
      return send({
        id,
        result: { content: [{ type: "text", text: `${tag}:${params.arguments.text}` }] },
      });
    if (name === "slow") {
      await new Promise((r) => setTimeout(r, params.arguments.ms ?? 100));
      return send({ id, result: { content: [{ type: "text", text: "done" }] } });
    }
    return send({
      id,
      result: { isError: true, content: [{ type: "text", text: "unknown tool" }] },
    });
  }
  if (method === "ping") return send({ id, result: {} });
  if (id !== undefined)
    send({ id, error: { code: -32601, message: `no method ${method}` } });
});
