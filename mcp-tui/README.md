# mcp-tui

Single-process Node.js TUI + multiplexing HTTP/SSE proxy for managing local
MCP (Model Context Protocol) stdio servers. Replaces the legacy pattern of:

```
npx mcp-proxy --port 9090 --shell -- npx -y @modelcontextprotocol/server-filesystem "S:\FastGitRoot\appmain"
npx mcp-proxy --port 9091 -- npx -y bash-mcp
...one proxy instance + port per server...
```

with **one** in-process gateway on `:9090/sse` that aggregates the tools of
every enabled server, direct `child_process.spawn` stdio IPC (no `cmd.exe`
wrapping except where Windows `.cmd` shims like `npx`/`uvx` force it), and an
Ink terminal UI to toggle / restart / watch servers live.

## Usage

```bash
npm install
npm run build
npm start          # loads ./mcp-config.json (created with defaults on first run)
```

Keys (two panes — **Servers** and **Logs** — one is focused at a time):

- `Tab` (or `←`/`→`) — switch which pane is focused (highlighted border + `▶` marker)
- **Servers focused:** `↑`/`↓` select server · `Space` toggle · `R` restart
- **Logs focused:** `↑`/`↓` scroll the log viewer · `PageUp`/`PageDown` page · `g` jump to oldest · `G` jump back to tail
- `Space`/`R` act on the selected server from either pane · `Q`/`Ctrl+C` quit

Endpoints:

- `GET /sse` + `POST /message?sessionId=…` — mcp-proxy-compatible HTTP+SSE transport
- `POST /mcp` — JSON single-shot (streamable-style)
- `GET /health` — status of all servers + aggregated tool list

## Configuration (`mcp-config.json`)

```json
{
  "port": 9090,
  "prefixTools": "auto",
  "servers": [
    {
      "id": "file-system",
      "name": "File Access",
      "command": "npx",
      "args": ["-y", "@modelcontextprotocol/server-filesystem", "S:\\FastGitRoot\\appmain"],
      "autostart": true
    },
    {
      "id": "windows-desktop",
      "name": "Desktop Control",
      "command": "uvx",
      "args": ["windows-mcp", "serve"],
      "autostart": false
    }
  ]
}
```

`prefixTools`: `auto` (default) prefixes only colliding tool names with
`<id>_`, `always` prefixes everything, `never` passes names through raw.

Use `--config path/to/file.json` or env `MCP_TUI_CONFIG` for other configs.
