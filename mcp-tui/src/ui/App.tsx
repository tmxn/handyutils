import React, { useEffect, useMemo, useReducer, useRef, useState } from "react";
import { Box, Text, useApp, useInput, useStdout } from "ink";
import type { McpManager, ServerRuntime } from "../manager.js";
import type { LogEntry } from "../types.js";

type Focus = "servers" | "logs";

const STATUS_COLOR: Record<string, string> = {
  RUNNING: "green",
  STARTING: "yellow",
  ERRORED: "red",
  STOPPED: "gray",
};

const LEVEL_COLOR: Record<string, string> = {
  INFO: "cyan",
  RPC: "gray",
  STDIO: "white",
  STDERR: "yellow",
  ERROR: "red",
};

function fmtTime(t: number): string {
  const d = new Date(t);
  const p = (n: number) => String(n).padStart(2, "0");
  return `${p(d.getHours())}:${p(d.getMinutes())}:${p(d.getSeconds())}`;
}

function statusSuffix(rt: ServerRuntime): string {
  switch (rt.status) {
    case "RUNNING":
      return `Running: PID ${rt.pid}`;
    case "STARTING":
      return "Starting...";
    case "ERRORED":
      return rt.lastError ? `ERRORED (${rt.lastError})` : "ERRORED";
    default:
      return "Stopped";
  }
}

// ---------------------------------------------------------------------------

export function App({ manager, proxyUrl }: { manager: McpManager; proxyUrl: string }) {
  const { exit } = useApp();
  const { stdout } = useStdout();
  const [, invalidate] = useReducer((x: number) => x + 1, 0);
  const [selected, setSelected] = useState(0);
  const [focus, setFocus] = useState<Focus>("servers");
  const [logScroll, setLogScroll] = useState(0); // lines scrolled up from tail (0 = follow)
  const shuttingDown = useRef(false);

  const runtimes = useMemo(() => [...manager.runtimes.values()], [manager]);
  const n = runtimes.length;
  const sel = n ? runtimes[Math.min(selected, n - 1)] : undefined;

  // Re-render whenever a runtime changes status or emits a log line.
  useEffect(() => {
    const onChange = () => invalidate();
    manager.on("change", onChange);
    manager.on("log", onChange);
    return () => {
      manager.off("change", onChange);
      manager.off("log", onChange);
    };
  }, [manager]);

  // Ink only re-lays-out on resize; it does NOT re-run React renderers.
  // Force a re-render so our geometry math tracks the new terminal size.
  useEffect(() => {
    const onResize = () => invalidate();
    stdout.on?.("resize", onResize);
    return () => {
      stdout.off?.("resize", onResize);
    };
  }, [stdout]);

  const quit = () => {
    if (shuttingDown.current) return;
    shuttingDown.current = true;
    void manager.stopAll().finally(() => exit());
  };

  useInput((ch, key) => {
    try {
      if ((key.ctrl && ch === "c") || ch === "q" || ch === "Q") {
        quit();
        return;
      }
      if (key.tab) {
        setFocus((f) => (f === "servers" ? "logs" : "servers"));
        return;
      }
      if (key.leftArrow) {
        setFocus("servers");
        return;
      }
      if (key.rightArrow) {
        setFocus("logs");
        return;
      }

      // Space / R act on the selected server from either pane.
      if (ch === " " || key.return) {
        void sel?.toggle();
        return;
      }
      if (ch === "r" || ch === "R") {
        void sel?.restart();
        return;
      }

      if (focus === "logs") {
        if (key.upArrow || key.pageUp) {
          setLogScroll((s) => s + (key.pageUp ? 10 : 1));
          return;
        }
        if (key.downArrow || key.pageDown) {
          setLogScroll((s) => Math.max(0, s - (key.pageDown ? 10 : 1)));
          return;
        }
        if (ch === "g") {
          setLogScroll(Number.MAX_SAFE_INTEGER); // oldest
          return;
        }
        if (ch === "G") {
          setLogScroll(0); // back to tail
          return;
        }
        if (ch === "k") {
          setLogScroll((s) => s + 1);
          return;
        }
        if (ch === "j") {
          setLogScroll((s) => Math.max(0, s - 1));
          return;
        }
      } else {
        if (key.upArrow || ch === "k") {
          setSelected((s) => Math.max(0, s - 1));
          return;
        }
        if (key.downArrow || ch === "j") {
          setSelected((s) => Math.min(n - 1, s + 1));
          return;
        }
      }
    } catch (err) {
      process.stderr.write(`\ninput error: ${(err as Error).message}\n`);
    }
  });

  // ---------------- geometry (recomputed on every re-render incl. resize) ------
  const cols = stdout.columns ?? 80;
  const rows = stdout.rows ?? 24;
  const stack = cols < 56; // too narrow -> stack panes vertically

  // Whole UI fills the terminal; Ink only ever sets the root width itself,
  // so we pin the height here to make vertical flexGrow work.
  const bodyH = Math.max(4, rows - 4); // footer (3) + proxy line (1)
  const paneH = stack ? Math.floor(bodyH / 2) : bodyH;

  // ---------------- server list (clip to fit the pane) ------------------------
  const listCap = Math.max(1, paneH - 4); // header + footer rows inside border
  const visibleServers = runtimes.slice(0, listCap);
  const hiddenServers = n - visibleServers.length;

  // ---------------- log viewport ----------------------------------------------
  const total = sel ? sel.logs.length : 0;
  const viewport = Math.max(1, paneH - 3); // title row + borders
  const scroll = Math.min(logScroll, Math.max(0, total - viewport));
  const start = Math.max(0, total - scroll - viewport);
  const end = Math.max(0, total - scroll);
  const logLines: LogEntry[] = sel ? sel.logs.slice(start, end) : [];
  const atTail = scroll === 0;
  const toolCount = manager.proxiedTools().length;

  const serversFocused = focus === "servers";
  const logsFocused = focus === "logs";

  return (
    <Box flexDirection="column" height={rows}>
      <Box
        flexDirection={stack ? "column" : "row"}
        flexGrow={1}
      >
        {/* ============ Servers pane ============ */}
        <Box
          flexDirection="column"
          flexGrow={stack ? 1 : 0}
          width={stack ? undefined : "38%"}
          minWidth={stack ? undefined : 20}
          borderStyle="round"
          borderColor={serversFocused ? "cyan" : "gray"}
        >
          <Text bold color={serversFocused ? "cyan" : undefined} wrap="truncate-end">
            {serversFocused ? "◄" : " "} MCP Server Manager{" "}
            {serversFocused ? "►" : " "}
          </Text>
          {visibleServers.map((rt) => {
            const isSel = rt === sel;
            const enabled = rt.status === "RUNNING" || rt.status === "STARTING";
            return (
              <Box key={rt.cfg.id}>
                <Text color={isSel ? "cyan" : undefined}>
                  {isSel ? "▸ " : "  "}
                  {enabled ? "[o] " : "[x] "}
                </Text>
                <Text bold={isSel} color={STATUS_COLOR[rt.status]} wrap="truncate-end">
                  {rt.cfg.name} ({statusSuffix(rt)})
                </Text>
              </Box>
            );
          })}
          {hiddenServers > 0 && (
            <Text dimColor wrap="truncate-end">… {hiddenServers} more server(s) below</Text>
          )}
          <Box flexGrow={1} />
          <Text dimColor wrap="truncate-end"> proxied tools: {toolCount}</Text>
        </Box>

        {/* ============ Logs pane ============ */}
        <Box
          flexDirection="column"
          flexGrow={1}
          minWidth={stack ? undefined : 12}
          borderStyle="round"
          borderColor={logsFocused ? "cyan" : "gray"}
        >
          <Text bold color={logsFocused ? "cyan" : undefined} wrap="truncate-end">
            {logsFocused ? "◄" : " "} Live Logs:{" "}
            {sel ? sel.cfg.name : "— select a server —"}
            {atTail ? "" : ` (^${scroll} older)`}
          </Text>
          {logLines.map((l: LogEntry, i: number) => (
            <Text key={i} wrap="truncate-end">
              <Text dimColor>[{fmtTime(l.t)}] </Text>
              <Text color={LEVEL_COLOR[l.level] ?? "white"}>[{l.level}] </Text>
              {l.msg}
            </Text>
          ))}
          <Box flexGrow={1} />
        </Box>
      </Box>

      <Box height={3} flexShrink={0} borderStyle="round" borderColor="gray">
        <Text dimColor wrap="truncate-end">
          {" "}
          [Tab/←→] Switch Pane (focused:{" "}
          <Text color={serversFocused ? "cyan" : "gray"}>
            {serversFocused ? "▶ Servers" : "▷ Servers"}
          </Text>
          {" / "}
          <Text color={logsFocused ? "cyan" : "gray"}>
            {logsFocused ? "▶ Logs" : "▷ Logs"}
          </Text>
          {") "}
          │ [↑/↓] {serversFocused ? "Select Server" : "Scroll Logs"}{" "}
          │ [Space] Toggle │ [R] Restart │ [Q] Quit{" "}
        </Text>
      </Box>
      <Box flexShrink={0}>
        <Text dimColor wrap="truncate-end">
          {"  Proxy: "}
          <Text color="green">{proxyUrl}</Text>
          {"  (point MCP clients here — replaces per-server mcp-proxy ports)"}
        </Text>
      </Box>
    </Box>
  );
}
