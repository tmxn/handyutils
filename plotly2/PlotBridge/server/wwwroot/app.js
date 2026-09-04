'use strict';

/* --------------------------------------------------------------------- colour
   Categorical hues in a FIXED order — a series keeps its slot for life, so
   deleting series 2 never repaints series 3. Both mode arrays are selected sets
   from the validated reference palette (same eight hues, stepped per surface).

   Only the first three slots clear the all-pairs CVD floors that a scatter plot
   demands, so slot also drives a marker SYMBOL. Beyond three series, shape — not
   hue — is what actually separates them.

   Lines stay SOLID. Slot used to pick a dash pattern too, as a second redundant
   channel, but on point sequences a dashed polyline reads as gaps in the geometry
   — it misdescribes the data to say something the marker shape already says. */

const PALETTE = {
  light: ['#2a78d6', '#eb6834', '#1baf7a', '#eda100', '#e87ba4', '#008300', '#4a3aa7', '#e34948'],
  dark:  ['#3987e5', '#d95926', '#199e70', '#c98500', '#d55181', '#008300', '#9085e9', '#e66767'],
};
const SYMBOLS_2D = ['circle', 'square', 'diamond', 'triangle-up', 'cross', 'x', 'star', 'hexagon'];
const SYMBOLS_3D = ['circle', 'square', 'diamond', 'cross', 'x', 'circle-open', 'square-open', 'diamond-open'];

const GL_THRESHOLD = 2000;   // above this, 2D switches to WebGL rendering

// Fallbacks for series that arrive without a style. Keep in step with Style's
// defaults in Models.cs — the server stamps those on anything it stores, so these
// only apply to a series the page renders before the server has spoken.
const DEFAULT_DRAW_MODE = 'lines+markers';
const DEFAULT_SIZE = 3;

/** Colour repeats every 8 slots, symbol shifts one step per octave, so no
 *  (colour, symbol) pair repeats until 64 series are on one chart. */
function symbolIndex(slot) {
  return ((slot % 8) + Math.floor(slot / 8)) % 8;
}

function colorFor(series) {
  if (series.style && series.style.color) return series.style.color;
  const slot = (series.style && series.style.slot) || 0;
  return PALETTE[themeName()][slot % 8];
}

/* ---------------------------------------------------------------------- theme */

let themePref = localStorage.getItem('plotbridge.theme') || 'auto';

function themeName() {
  if (themePref === 'auto') {
    return window.matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light';
  }
  return themePref;
}

function applyTheme() {
  if (themePref === 'auto') document.documentElement.removeAttribute('data-theme');
  else document.documentElement.setAttribute('data-theme', themePref);
  $('themeBtn').textContent = 'theme: ' + themePref;
  scheduleRender();
}

function cssVar(name) {
  return getComputedStyle(document.documentElement).getPropertyValue(name).trim();
}

function chrome() {
  return {
    surface: cssVar('--surface-1'),
    text2: cssVar('--text-2'),
    muted: cssVar('--muted'),
    grid: cssVar('--grid'),
    baseline: cssVar('--baseline'),
  };
}

/* ----------------------------------------------------------------- app state */

const $ = (id) => document.getElementById(id);
const params = new URLSearchParams(location.search);
const BOARD = params.get('board') || 'default';

let state = { name: BOARD, charts: [] };
let activeChart = null;      // chart name
let clientId = null;
let socket = null;
let retry = 0;
const fresh = new Set();     // charts with unseen pushes
const viewRev = new Map();   // chart name -> uirevision counter (bumped on Reset view)

/* Last user-chosen 2D axis ranges, per chart.
   Plotly's uirevision alone is not enough here: with equal aspect on, the
   scaleanchor constraint machinery recomputes both ranges whenever the data
   changes and discards the user's zoom. (Verified — with equal aspect off,
   uirevision holds the range fine; with it on, the view jumps to the new
   extent.) So we capture the range on user interaction and re-apply it
   explicitly. An entry exists only once the user has actually zoomed or
   panned, which keeps the useful default: an untouched chart still autoscales
   to newly pushed data. */
const zoom2d = new Map();
let suppressRelayout = false;

function chartByName(name) {
  return state.charts.find((c) => c.name === name) || null;
}

function active() {
  return chartByName(activeChart);
}

/* -------------------------------------------------------------------- socket */

function connect() {
  const proto = location.protocol === 'https:' ? 'wss:' : 'ws:';
  socket = new WebSocket(`${proto}//${location.host}/ws?board=${encodeURIComponent(BOARD)}`);

  socket.onopen = () => {
    retry = 0;
    setStatus('live', 'live');
  };

  socket.onclose = () => {
    setStatus('down', 'disconnected — retrying');
    const delay = Math.min(500 * Math.pow(1.6, retry++), 8000);
    setTimeout(connect, delay);
  };

  socket.onerror = () => setStatus('down', 'connection error');

  socket.onmessage = (ev) => {
    let msg;
    try { msg = JSON.parse(ev.data); } catch { return; }
    handle(msg);
  };
}

function send(msg) {
  if (socket && socket.readyState === WebSocket.OPEN) socket.send(JSON.stringify(msg));
}

function setStatus(cls, text) {
  const el = $('status');
  el.className = 'status ' + cls;
  $('statusText').textContent = text;
}

function handle(msg) {
  switch (msg.type) {
    // Fire-and-forget: the reply goes back over HTTP, not the socket, because the
    // payload is image bytes and the waiting request is an HTTP request.
    case 'render':
      renderToImage(msg);
      return;

    case 'snapshot': {
      clientId = msg.clientId;
      state = msg.board || { name: BOARD, charts: [] };
      if (!chartByName(activeChart)) activeChart = state.charts.length ? state.charts[0].name : null;
      renderAll();
      return;
    }
    case 'series': {
      let chart = chartByName(msg.chart);
      if (!chart) {
        chart = { name: msg.chart, mode: msg.mode || 'auto', uniform: msg.uniform !== false, series: [] };
        state.charts.push(chart);
      }
      chart.mode = msg.mode || chart.mode;
      if (typeof msg.uniform === 'boolean') chart.uniform = msg.uniform;

      const i = chart.series.findIndex((s) => s.name === msg.series.name);
      if (i >= 0) chart.series[i] = msg.series;
      else chart.series.push(msg.series);

      if (!activeChart || ($('follow').checked && chart.name !== activeChart)) activeChart = chart.name;
      if (chart.name !== activeChart) fresh.add(chart.name);
      renderAll();
      return;
    }
    case 'seriesRemoved': {
      const c = chartByName(msg.chart);
      if (c) c.series = c.series.filter((s) => s.name !== msg.series);
      renderAll();
      return;
    }
    case 'seriesRenamed': {
      const c = chartByName(msg.chart);
      const s = c && c.series.find((s) => s.name === msg.series);
      if (s) s.name = msg.to;
      renderAll();
      return;
    }
    case 'seriesStyle': {
      const c = chartByName(msg.chart);
      const s = c && c.series.find((s) => s.name === msg.series);
      if (s) s.style = msg.style;
      renderAll();
      return;
    }
    case 'seriesVisible': {
      const c = chartByName(msg.chart);
      const s = c && c.series.find((s) => s.name === msg.series);
      if (s) s.visible = msg.visible;
      renderAll();
      return;
    }
    case 'chartOpts': {
      const c = chartByName(msg.chart);
      if (c) { c.mode = msg.mode; c.uniform = msg.uniform; }
      renderAll();
      return;
    }
    case 'chartAdded': {
      if (!chartByName(msg.chart)) {
        state.charts.push({ name: msg.chart, mode: msg.mode || 'auto', uniform: msg.uniform !== false, series: [] });
      }
      renderAll();
      return;
    }
    case 'chartRemoved': {
      state.charts = state.charts.filter((c) => c.name !== msg.chart);
      if (activeChart === msg.chart) activeChart = state.charts.length ? state.charts[0].name : null;
      renderAll();
      return;
    }
    case 'clearChart': {
      const c = chartByName(msg.chart);
      if (c) c.series = [];
      renderAll();
      return;
    }
    case 'clearBoard': {
      state.charts = [];
      activeChart = null;
      renderAll();
      return;
    }
  }
}

/* -------------------------------------------------------------------- render */

let pending = false;
function scheduleRender() {
  if (pending) return;
  pending = true;
  requestAnimationFrame(() => { pending = false; renderNow(); });
}
const renderAll = scheduleRender;

function renderNow() {
  renderTabs();
  renderChartOpts();
  renderSeriesList();
  renderPlot();
}

function renderTabs() {
  const nav = $('tabs');
  nav.textContent = '';
  for (const c of state.charts) {
    const b = document.createElement('button');
    b.className = 'tab';
    b.setAttribute('aria-selected', String(c.name === activeChart));
    b.onclick = () => { activeChart = c.name; fresh.delete(c.name); renderAll(); };

    b.append(document.createTextNode(c.name));

    const badge = document.createElement('span');
    badge.className = 'badge';
    badge.textContent = c.series.length ? String(c.series.length) : '';
    b.append(badge);

    if (fresh.has(c.name)) {
      const d = document.createElement('span');
      d.className = 'fresh';
      b.append(d);
    }
    nav.append(b);
  }

  const add = document.createElement('button');
  add.className = 'tab';
  add.title = 'New chart';
  add.textContent = '+';
  add.onclick = () => {
    const name = prompt('Chart name', 'chart ' + (state.charts.length + 1));
    if (!name) return;
    if (!chartByName(name)) state.charts.push({ name, mode: 'auto', uniform: true, series: [] });
    activeChart = name;
    send({ type: 'addChart', chart: name });
    renderAll();
  };
  nav.append(add);
}

function renderChartOpts() {
  const host = $('chartOpts');
  host.textContent = '';
  const c = active();
  if (!c) {
    host.append(el('p', { class: 'hint' }, 'No chart yet.'));
    return;
  }

  const row1 = el('div', { class: 'row' });
  row1.append(el('label', { class: 'inline' }, 'Mode'));
  const sel = el('select');
  for (const [v, t] of [['auto', 'auto'], ['2d', '2D'], ['3d', '3D']]) {
    const o = el('option', { value: v }, t);
    if (c.mode === v) o.selected = true;
    sel.append(o);
  }
  sel.onchange = () => {
    c.mode = sel.value;
    send({ type: 'setChartOpts', chart: c.name, mode: c.mode, uniform: c.uniform });
    renderAll();
  };
  row1.append(sel);

  const uni = el('input', { type: 'checkbox' });
  uni.checked = c.uniform;
  uni.onchange = () => {
    c.uniform = uni.checked;
    send({ type: 'setChartOpts', chart: c.name, mode: c.mode, uniform: c.uniform });
    renderAll();
  };
  const uniLabel = el('label', { class: 'inline', title: '2D: equal x/y scale.  3D: aspectmode "data".' });
  uniLabel.append(uni, document.createTextNode(' equal aspect'));
  row1.append(uniLabel);
  host.append(row1);

  const row2 = el('div', { class: 'row' });
  row2.style.marginTop = '8px';

  const reset = el('button', { class: 'tiny' }, 'Reset view');
  reset.onclick = () => {
    zoom2d.delete(c.name);
    viewRev.set(c.name, (viewRev.get(c.name) || 0) + 1);
    renderAll();
  };

  const clear = el('button', { class: 'tiny' }, 'Clear');
  clear.onclick = () => {
    if (!c.series.length) return;
    c.series = [];
    send({ type: 'clearChart', chart: c.name });
    renderAll();
  };

  const del = el('button', { class: 'tiny' }, 'Delete chart');
  del.onclick = () => {
    if (!confirm(`Delete chart "${c.name}" and its ${c.series.length} series?`)) return;
    state.charts = state.charts.filter((x) => x !== c);
    activeChart = state.charts.length ? state.charts[0].name : null;
    send({ type: 'removeChart', chart: c.name });
    renderAll();
  };

  row2.append(reset, clear, del);
  host.append(row2);
}

function renderSeriesList() {
  const host = $('seriesList');
  host.textContent = '';
  const c = active();
  const n = c ? c.series.length : 0;
  $('seriesCount').textContent = n ? `(${n})` : '';

  if (!c || !n) {
    host.append(el('p', { class: 'hint' }, 'Nothing plotted on this chart yet.'));
    return;
  }

  const is3d = resolve3d(c);

  for (const s of c.series) {
    const row = el('div', { class: 'series-row' + (s.visible ? '' : ' hidden') });

    const swatch = el('input', { type: 'color', title: 'Series colour (overrides the palette slot)' });
    swatch.value = normalizeHex(colorFor(s));
    swatch.oninput = () => {
      s.style.color = swatch.value;
      send({ type: 'setStyle', chart: c.name, series: s.name, style: { color: swatch.value } });
      scheduleRender();
    };

    const name = el('div', { class: 'series-name', title: describe(s) }, s.name);

    const actions = el('div', { class: 'row' });

    const vis = el('button', { class: 'tiny ghost', title: s.visible ? 'Hide' : 'Show' }, s.visible ? '👁' : '🚫');
    vis.onclick = () => {
      s.visible = !s.visible;
      send({ type: 'setVisible', chart: c.name, series: s.name, visible: s.visible });
      renderAll();
    };

    const rm = el('button', { class: 'tiny ghost', title: 'Remove series' }, '✕');
    rm.onclick = () => {
      c.series = c.series.filter((x) => x !== s);
      send({ type: 'removeSeries', chart: c.name, series: s.name });
      renderAll();
    };
    actions.append(vis, rm);

    const sub = el('div', { class: 'series-sub' });

    const count = el('button', { class: 'countbtn', title: 'Show values as a table' },
      s.y.length.toLocaleString() + (s.z ? ' × 3' : ' × 2'));
    count.onclick = () => showData(s);

    const mode = el('select', { title: 'Draw mode' });
    for (const [v, t] of [['markers', 'markers'], ['lines', 'lines'], ['lines+markers', 'both']]) {
      const o = el('option', { value: v }, t);
      if ((s.style.mode || DEFAULT_DRAW_MODE) === v) o.selected = true;
      mode.append(o);
    }
    mode.onchange = () => {
      s.style.mode = mode.value;
      send({ type: 'setStyle', chart: c.name, series: s.name, style: { mode: mode.value } });
      scheduleRender();
    };

    const size = el('input', { type: 'number', min: '1', max: '30', step: '1', title: 'Marker size' });
    size.value = String(s.style.size || DEFAULT_SIZE);
    size.oninput = () => {
      const v = Number(size.value);
      if (!(v > 0)) return;
      s.style.size = v;
      send({ type: 'setStyle', chart: c.name, series: s.name, style: { size: v } });
      scheduleRender();
    };

    const shape = el('span', { class: 'muted', title: 'Marker shape — the secondary channel that separates series past the third' },
      (is3d ? SYMBOLS_3D : SYMBOLS_2D)[symbolIndex((s.style && s.style.slot) || 0)]);

    sub.append(count, mode, size, shape);
    row.append(swatch, name, actions, sub);
    host.append(row);
  }
}

function describe(s) {
  const bits = [s.name, `${s.y.length} points`];
  if (s.updatedMs) bits.push('updated ' + new Date(s.updatedMs).toLocaleTimeString());
  if (s.meta) for (const [k, v] of Object.entries(s.meta)) bits.push(`${k}: ${v}`);
  return bits.join('\n');
}

function resolve3d(chart) {
  if (chart.mode === '3d') return true;
  if (chart.mode === '2d') return false;
  return chart.series.some((s) => s.z && s.z.length);
}

// The data extent of a chart, per axis, over the series that are actually drawn.
// Returns null when there is nothing finite to measure.
function dataExtents(c, is3d) {
  const lo = { x: Infinity, y: Infinity, z: Infinity };
  const hi = { x: -Infinity, y: -Infinity, z: -Infinity };
  let any = false;

  for (const s of c.series) {
    // A hidden series is 'legendonly': Plotly leaves it out of autorange and
    // aspect, so it must be left out here too or the two disagree.
    if (!s.visible) continue;
    const axes = is3d ? AXES_3D : ['x', 'y'];
    for (const a of axes) {
      const arr = a === 'z' ? (s.z && s.z.length === s.y.length ? s.z : null) : s[a];
      if (!arr) {
        // A series with no z is drawn at z = 0 (see traceFor), so that is what it
        // contributes to the extent. Only z is ever absent; x and y always exist.
        if (a === 'z') { lo.z = Math.min(lo.z, 0); hi.z = Math.max(hi.z, 0); }
        continue;
      }
      for (const v of arr) {
        if (!Number.isFinite(v)) continue;
        if (v < lo[a]) lo[a] = v;
        if (v > hi[a]) hi[a] = v;
        any = true;
      }
    }
  }

  if (!any) return null;
  const span = (a) => (Number.isFinite(lo[a]) && Number.isFinite(hi[a]) ? hi[a] - lo[a] : 0);
  return {
    lo, hi,
    span: { x: span('x'), y: span('y'), z: span('z') },
  };
}

// Equal aspect in 3D is aspectmode "data", and that goes badly wrong on FLAT
// data. A planar toolpath - a profile at constant z, or the constant-x case this
// was found on - leaves one axis with an extent of zero, or of 7e-18 once
// floating point has had its say. Plotly normalises the other two against it and
// the scene box stops being a box: measured on a real 44-point path, aspectratio
// came back {x: 5e-12, y: 681558, z: 288527}. 680,000 units tall does not render,
// takes camera interaction down with it, and corrupts autorange on the way - the
// y range no longer even contained the data. Flat geometry is the common case in
// CAM, not an edge case.
//
// The working recipe below was arrived at by measuring the rendered pixels, not
// by reading the docs - gl-plot3d fails in ways the schema does not hint at. Two
// of the obvious fixes silently DROP THE TRACE, drawing an empty box:
//
//   * An explicit range on the flat axis. Any width at all, tested from +/-0.5
//     down to the slab: the box draws, the path does not.
//   * A thin aspect share for the flat axis. 0.04 of the largest draws nothing;
//     matching the largest draws correctly.
//
// So neither the range nor a slab is available, and what works is:
//
//   1. COLLAPSE the flat coordinate to one constant value (see traceFor). Once
//      the axis is exactly degenerate, Plotly expands it to value +/-1 on its
//      own, which is both safe and better than the alternative: left as-is, the
//      range spans the noise itself (5e-18 wide, across those ...908 vs ...915
//      tails) and every point lands on one face of the box or the other, so
//      rounding error is drawn as real depth.
//   2. Give the flat axis an aspect share equal to the LARGEST real extent.
//      Nothing extends along it, so its thickness claims no proportion and can
//      be chosen freely - and choosing the largest keeps it as far as possible
//      from the thin-share failure above.
//   3. Never set a range on it.
//
// The axes that do carry extent keep their exact 1:1 relationship, which is the
// part that has to stay true: within the plane of a planar path, a circle still
// looks like a circle. That is also why aspectmode "cube" is not the answer here
// even though it draws - it stretches each axis to fill the box independently, so
// it renders the geometry out of proportion.
//
// The ratio arithmetic is matched to Plotly's own, measured rather than assumed:
//   - the ratios are normalised so their PRODUCT is 1 (a geometric mean of 1)
//   - autorange padding is proportional - range spans {3.1875, 3.1875, 0.797}
//     against data spans {3, 3, 0.75}, all one factor - so normalised data spans
//     and normalised range spans come out identical.
// On a probe with no flat axis this reproduces what "data" computed to 16
// significant figures, so non-flat charts are provably unaffected. Setting it
// unconditionally is also what keeps the box honest across a chart switch:
// uirevision preserves scene.aspectratio (it is user-draggable), so a mode left
// un-set inherits the previous chart's box - seen live, a flat chart's
// {0.04, 1, 0.42} leaking onto the next chart's data.
//
// A genuinely thin extent is left alone. Equal aspect is the one thing this mode
// promises, and quietly fattening a real 0.1%-thick feature would be a lie about
// the geometry. FLAT_AXIS_RATIO is set low enough to mean "this is not extent, it
// is rounding error"; anything above it is real and is respected. If a real
// feature is too thin to see, that is what the equal-aspect toggle is for.
const FLAT_AXIS_RATIO = 1e-6;   // extent below this share of the largest is noise
const AXES_3D = ['x', 'y', 'z'];

// Which axes carry no real extent, and the single value each should collapse to.
// Shared by the layout and the traces so the two cannot disagree - and computed
// across every visible series, so a flat axis stays coplanar across all of them.
function flatAxes(c, is3d) {
  const ext = dataExtents(c, is3d);
  if (!ext) return null;

  const axes = is3d ? AXES_3D : ['x', 'y'];
  const largest = Math.max(...axes.map((a) => ext.span[a]));
  if (!(largest > 0)) return { ext, largest: 0, at: {} };

  const at = {};
  for (const a of axes) {
    if (ext.span[a] > largest * FLAT_AXIS_RATIO) continue;
    at[a] = Number.isFinite(ext.lo[a]) ? (ext.lo[a] + ext.hi[a]) / 2 : 0;
  }
  return { ext, largest, at };
}

function equalAspect3d(scene, flat) {
  // Nothing measurable, or every axis flat: one point, or many copies of one.
  // There is no shape to preserve, so a cube is the only sane box - and stating
  // it, rather than leaving it, is what stops the last chart's box being inherited.
  if (!flat || !(flat.largest > 0)) {
    scene.aspectmode = 'cube';
    scene.aspectratio = { x: 1, y: 1, z: 1 };
    return;
  }

  const effective = {};
  for (const a of AXES_3D) {
    effective[a] = a in flat.at ? flat.largest : flat.ext.span[a];
  }

  const geoMean = Math.cbrt(effective.x * effective.y * effective.z);
  scene.aspectmode = 'manual';
  scene.aspectratio = {
    x: effective.x / geoMean,
    y: effective.y / geoMean,
    z: effective.z / geoMean,
  };
}

// Everything the on-screen plot and an exported image must agree on: axis styling,
// fonts, legend, aspect. The two then diverge deliberately — the page layers view
// state on top (uirevision, held zoom, drag mode), an export layers on a camera and
// always autoranges. Sharing this much is what keeps a PNG looking like the page.
function baseLayout(c, is3d, th, flat) {
  const axis = (title) => ({
    title: { text: title, font: { color: th.muted } },
    gridcolor: th.grid,
    zerolinecolor: th.baseline,
    linecolor: th.baseline,
    tickfont: { color: th.muted },
  });

  const layout = {
    margin: is3d ? { l: 0, r: 0, t: 8, b: 0 } : { l: 58, r: 16, t: 10, b: 46 },
    paper_bgcolor: th.surface,
    plot_bgcolor: th.surface,
    font: { family: 'system-ui, -apple-system, "Segoe UI", sans-serif', size: 11, color: th.text2 },
    hovermode: 'closest',
    // Identity is never colour-alone: the legend is always present for >= 2
    // series, and each entry carries the marker shape as well as the hue.
    showlegend: c.series.length >= 2,
    legend: { orientation: 'h', y: -0.1, font: { color: th.text2 }, bgcolor: 'rgba(0,0,0,0)' },
  };

  if (is3d) {
    layout.scene = {
      xaxis: axis('x'),
      yaxis: axis('y'),
      zaxis: axis('z'),
      aspectmode: c.uniform ? 'data' : 'auto',
      bgcolor: th.surface,
    };
    // Only "data" divides by the extents, so only "data" can be destroyed by an
    // extent of zero. "auto" gives a cube and is already safe.
    if (c.uniform) equalAspect3d(layout.scene, flat);
  } else {
    layout.xaxis = axis('x');
    layout.yaxis = axis('y');
    if (c.uniform) { layout.yaxis.scaleanchor = 'x'; layout.yaxis.scaleratio = 1; }
  }

  return layout;
}

function renderPlot() {
  const c = active();
  const div = $('plot');
  const hasData = !!c && c.series.length > 0;

  $('empty').hidden = hasData;
  div.style.visibility = hasData ? 'visible' : 'hidden';
  if (!hasData) { Plotly.purge(div); return; }

  const is3d = resolve3d(c);
  const th = chrome();
  const flat = flatAxes(c, is3d);
  const traces = c.series.map((s) => traceFor(s, is3d, flat));

  const rev = `${c.name}|${is3d ? '3d' : '2d'}|${viewRev.get(c.name) || 0}`;
  const layout = baseLayout(c, is3d, th, flat);
  layout.uirevision = rev;

  if (is3d) {
    layout.scene.uirevision = rev;
  } else {
    // Drag pans; the wheel already zooms (scrollZoom). Switching this on the
    // modebar still sticks, because uirevision preserves it.
    layout.dragmode = 'pan';

    // Binary and explicit: either the user owns the view or the data does.
    // Asking for autorange outright matters — on a uirevision change Plotly
    // reverts to the *pre-interaction* range it stashed, which is stale, not
    // the current data extent.
    const held = zoom2d.get(c.name);
    if (held) {
      layout.xaxis.range = held.x.slice();
      layout.yaxis.range = held.y.slice();
      layout.xaxis.autorange = false;
      layout.yaxis.autorange = false;
    } else {
      layout.xaxis.autorange = true;
      layout.yaxis.autorange = true;
    }
  }

  suppressRelayout = true;
  Plotly.react(div, traces, layout, {
    responsive: true,
    displaylogo: false,
    scrollZoom: true,
    modeBarButtonsToRemove: ['lasso2d', 'select2d'],
  }).then(() => { suppressRelayout = false; }, () => { suppressRelayout = false; });

  if (!div._pbWired) {
    div._pbWired = true;

    div.on('plotly_relayout', (ev) => {
      if (suppressRelayout) return;
      const cur = active();
      if (!cur || resolve3d(cur)) return;

      // Autoscale / double-click hands the view back to the data.
      if ('xaxis.autorange' in ev || 'yaxis.autorange' in ev) { zoom2d.delete(cur.name); return; }

      if (Object.keys(ev).some((k) => /^[xy]axis\.range/.test(k))) {
        const xr = div.layout.xaxis && div.layout.xaxis.range;
        const yr = div.layout.yaxis && div.layout.yaxis.range;
        if (xr && yr) zoom2d.set(cur.name, { x: xr.slice(), y: yr.slice() });
      }
    });

    div.on('plotly_legendclick', (ev) => {
      const cur = active();
      const s = cur && cur.series[ev.curveNumber];
      if (!s) return false;
      s.visible = !s.visible;
      send({ type: 'setVisible', chart: cur.name, series: s.name, visible: s.visible });
      renderAll();
      return false;   // we own visibility state; don't let Plotly also toggle it
    });
  }
}

/* ------------------------------------------------------------------- rasterise */

// Something outside the browser wants to look at a chart — a script, a CI step, an
// agent that can read a PNG but cannot drive a mouse. Plotly rasterises in the
// browser, so the work lands here.
//
// It renders into its own off-screen div rather than the visible one. That costs a
// second Plotly instance for a moment, and buys the guarantee that a render never
// steals the tab, camera or zoom of whoever is looking at the page.
async function renderToImage(msg) {
  const url = `/render/result?id=${encodeURIComponent(msg.id)}`;
  const fail = (reason) =>
    fetch(`${url}&error=${encodeURIComponent(reason)}`, { method: 'POST' }).catch(() => {});

  const c = chartByName(msg.chart);
  if (!c || !c.series.length) { fail(`chart '${msg.chart}' has nothing to draw`); return; }

  // Detached is not enough: Plotly sizes axes from the laid-out box, and a div
  // outside the document has no box. So it goes into the page, parked off-screen at
  // the requested pixel size.
  const shim = document.createElement('div');
  shim.style.cssText =
    `position:absolute;left:-10000px;top:0;width:${msg.width}px;height:${msg.height}px;`;
  document.body.appendChild(shim);

  try {
    const is3d = resolve3d(c);
    const flat = flatAxes(c, is3d);
    const layout = baseLayout(c, is3d, chrome(), flat);
    layout.width = msg.width;
    layout.height = msg.height;

    // The eye vector is the whole point of rendering headless: it is how a caller
    // that cannot drag the mouse still gets to look from somewhere useful. It only
    // means anything in 3D, so the resolved mode goes back with the image and the
    // caller can see why a requested eye had no effect.
    if (is3d && (msg.eye || msg.up)) {
      layout.scene.camera = {};
      if (msg.eye) layout.scene.camera.eye = { x: msg.eye[0], y: msg.eye[1], z: msg.eye[2] };
      if (msg.up) layout.scene.camera.up = { x: msg.up[0], y: msg.up[1], z: msg.up[2] };
    }

    const traces = c.series.map((s) => traceFor(s, is3d, flat));
    await Plotly.newPlot(shim, traces, layout, { staticPlot: true, displaylogo: false });

    const dataUrl = await Plotly.toImage(shim, {
      format: 'png',
      width: msg.width,
      height: msg.height,
      scale: msg.scale || 1,
    });

    // Round-tripping the data URL through fetch is the shortest correct base64
    // decode to a binary body — no manual atob/Uint8Array loop.
    const blob = await (await fetch(dataUrl)).blob();
    await fetch(`${url}&mode=${is3d ? '3d' : '2d'}`, {
      method: 'POST',
      body: blob,
      headers: { 'Content-Type': 'image/png' },
    });
  } catch (err) {
    fail(String((err && err.message) || err));
  } finally {
    Plotly.purge(shim);
    shim.remove();
  }
}

/** The coordinate array for one axis, with a flat axis collapsed to its single
 *  value. The spread being removed is below FLAT_AXIS_RATIO of the largest
 *  extent - rounding error, not geometry - and leaving it in makes the renderer
 *  draw it as real depth across the whole box. See equalAspect3d. */
function coordsFor(arr, axis, flat, n) {
  if (flat && axis in flat.at) return new Array(n).fill(flat.at[axis]);
  return arr;
}

function traceFor(s, is3d, flat) {
  const color = colorFor(s);
  const si = symbolIndex((s.style && s.style.slot) || 0);
  const n = s.y.length;
  const label = escapeHtml(s.name);

  const t = {
    name: s.name,
    x: coordsFor(s.x, 'x', flat, n),
    y: coordsFor(s.y, 'y', flat, n),
    mode: s.style.mode || DEFAULT_DRAW_MODE,
    visible: s.visible ? true : 'legendonly',
    line: { color, width: 2 },
  };

  if (is3d) {
    t.type = 'scatter3d';
    const z = s.z && s.z.length === n ? s.z : new Array(n).fill(0);
    t.z = coordsFor(z, 'z', flat, n);
    t.marker = { size: Math.max(1.5, (s.style.size || DEFAULT_SIZE) * 0.6), color, symbol: SYMBOLS_3D[si] };
    t.hovertemplate = `%{x:.6g}, %{y:.6g}, %{z:.6g}<extra>${label}</extra>`;
  } else {
    t.type = n > GL_THRESHOLD ? 'scattergl' : 'scatter';
    t.marker = { size: s.style.size || DEFAULT_SIZE, color, symbol: SYMBOLS_2D[si], line: { width: 0 } };
    t.hovertemplate = `%{x:.6g}, %{y:.6g}<extra>${label}</extra>`;
  }
  return t;
}

/* ---------------------------------------------------------------- data table */

let dataSeries = null;

function showData(s) {
  dataSeries = s;
  $('dataTitle').textContent = `${s.name} — ${s.y.length.toLocaleString()} points`;
  const body = $('dataBody');
  body.textContent = '';

  const table = el('table');
  const head = el('tr');
  head.append(el('th', {}, '#'), el('th', {}, 'x'), el('th', {}, 'y'));
  if (s.z) head.append(el('th', {}, 'z'));
  table.append(head);

  const limit = Math.min(s.y.length, 500);
  for (let i = 0; i < limit; i++) {
    const tr = el('tr');
    tr.append(el('td', {}, String(i)), el('td', {}, fmt(s.x[i])), el('td', {}, fmt(s.y[i])));
    if (s.z) tr.append(el('td', {}, fmt(s.z[i])));
    table.append(tr);
  }
  body.append(table);
  if (s.y.length > limit) {
    body.append(el('p', { class: 'hint', style: 'padding:6px 10px' },
      `showing first ${limit.toLocaleString()} of ${s.y.length.toLocaleString()} — "Copy all as TSV" gives every row`));
  }
  $('dataModal').hidden = false;
}

function tsvFor(s) {
  const lines = new Array(s.y.length);
  for (let i = 0; i < s.y.length; i++) {
    lines[i] = s.z ? `${s.x[i]}\t${s.y[i]}\t${s.z[i]}` : `${s.x[i]}\t${s.y[i]}`;
  }
  return lines.join('\n');
}

function fmt(v) {
  return typeof v === 'number' ? String(Number(v.toPrecision(10))) : '';
}

/* ------------------------------------------------------------------ plumbing */

function el(tag, attrs, text) {
  const e = document.createElement(tag);
  if (attrs) for (const [k, v] of Object.entries(attrs)) e.setAttribute(k, v);
  if (text != null) e.append(document.createTextNode(text));
  return e;
}

function escapeHtml(s) {
  return String(s).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
}

/** <input type="color"> only accepts #rrggbb. */
function normalizeHex(c) {
  if (/^#[0-9a-f]{6}$/i.test(c)) return c.toLowerCase();
  if (/^#[0-9a-f]{3}$/i.test(c)) return '#' + c.slice(1).split('').map((x) => x + x).join('').toLowerCase();
  return '#888888';
}

/* ---------------------------------------------------------------------- wire */

$('themeBtn').onclick = () => {
  themePref = themePref === 'auto' ? 'light' : themePref === 'light' ? 'dark' : 'auto';
  localStorage.setItem('plotbridge.theme', themePref);
  applyTheme();
};

window.matchMedia('(prefers-color-scheme: dark)').addEventListener('change', () => {
  if (themePref === 'auto') scheduleRender();
});

$('pasteBtn').onclick = () => {
  const text = $('pasteText').value;
  if (!text.trim()) { $('pasteHint').textContent = 'Nothing to plot.'; return; }
  const chart = activeChart || 'main';
  if (!chartByName(chart)) state.charts.push({ name: chart, mode: 'auto', uniform: true, series: [] });
  activeChart = chart;
  send({
    type: 'pushText',
    chart,
    series: $('pasteName').value.trim() || 'pasted',
    text,
    replace: $('pasteReplace').checked,
  });
  $('pasteHint').textContent = 'Sent — parsed server-side.';
};

$('dataClose').onclick = () => { $('dataModal').hidden = true; };
$('dataModal').onclick = (e) => { if (e.target === $('dataModal')) $('dataModal').hidden = true; };
$('dataCopy').onclick = async () => {
  if (!dataSeries) return;
  try {
    await navigator.clipboard.writeText(tsvFor(dataSeries));
    $('dataCopy').textContent = 'Copied';
    setTimeout(() => { $('dataCopy').textContent = 'Copy all as TSV'; }, 1200);
  } catch {
    $('dataCopy').textContent = 'Clipboard blocked';
  }
};

document.addEventListener('keydown', (e) => {
  if (e.key === 'Escape') $('dataModal').hidden = true;
});

$('boardName').textContent = BOARD;
$('emptyBoard').textContent = BOARD;
applyTheme();

fetch('health').then((r) => r.json()).then((h) => {
  const drop = `${h.dataDir}\\drop`;
  $('dropPath').textContent = drop;
  $('immediateSnippet').textContent =
    `System.IO.File.WriteAllText(@"${drop}\\pts.tsv", myTsvString)`;
  $('curlSnippet').textContent =
    `curl -X POST "http://localhost:${h.port}/push?board=${BOARD}&chart=main&series=pts" ^\n` +
    `     -H "Content-Type: text/plain" --data-binary "@points.tsv"`;
}).catch(() => {});

connect();

/* ---------------------------------------------------------------- board picker
   The board lives in the URL, and everything derived from it reads it from there:
   the websocket, the curl snippet, and the address /render tells a caller to open
   when it finds no page attached. So switching boards navigates rather than
   swapping state in place - a soft switch would leave the address bar lying about
   which board this page is a rasteriser for. */

function closeBoardMenu() {
  $('boardMenu').hidden = true;
  $('boardBtn').setAttribute('aria-expanded', 'false');
  disarmDelete();
}

function goToBoard(name) {
  name = (name || '').trim();
  if (!name) return;
  if (name === BOARD) { closeBoardMenu(); return; }
  location.search = '?board=' + encodeURIComponent(name);
}

/* Delete is two clicks, not a confirm() dialog: the first arms the button, the
   second does it. A board holds a run's worth of data and the deletion reaches
   disk, so it should not be one stray click away - but a modal for something this
   small would be worse than the risk. */

let armedDelete = null;

function disarmDelete() {
  if (!armedDelete) return;
  armedDelete.textContent = '×';
  armedDelete.classList.remove('armed');
  armedDelete = null;
}

async function deleteBoard(name, row, button) {
  let ok = false;
  try {
    const res = await fetch('boards?board=' + encodeURIComponent(name), { method: 'DELETE' });
    ok = res.ok;
  } catch { /* server down - say so on the button rather than vanishing the row */ }

  if (!ok) {
    button.textContent = 'failed';
    button.classList.remove('armed');
    button.classList.add('failed');
    armedDelete = null;
    return;
  }

  // The board being viewed keeps its row - it is still where this page is pointed,
  // and the server's clearBoard broadcast has already emptied the plot behind the
  // menu, which is the feedback that the delete landed.
  if (name === BOARD) disarmDelete();
  else row.remove();
}

function boardRow(name) {
  const row = document.createElement('div');
  row.className = 'brow';
  if (name === BOARD) row.classList.add('current');

  const open = document.createElement('button');
  open.type = 'button';
  open.className = 'open';

  const tick = document.createElement('span');
  tick.className = 'tick';
  tick.textContent = name === BOARD ? '✓' : '';

  const nm = document.createElement('span');
  nm.className = 'nm';
  nm.textContent = name;

  open.append(tick, nm);
  open.onclick = () => goToBoard(name);

  const del = document.createElement('button');
  del.type = 'button';
  del.className = 'del';
  del.textContent = '×';
  del.title = 'Delete board ' + name;
  del.onclick = () => {
    if (del === armedDelete) { deleteBoard(name, row, del); return; }
    disarmDelete();
    armedDelete = del;
    del.textContent = 'delete?';
    del.classList.add('armed');
  };

  row.append(open, del);
  return row;
}

async function openBoardMenu() {
  const list = $('boardList');
  disarmDelete();
  list.replaceChildren();
  $('boardMenu').hidden = false;
  $('boardBtn').setAttribute('aria-expanded', 'true');
  $('boardInput').value = '';
  $('boardInput').focus();

  // Fetched on every open rather than cached: a board springs into existence the
  // moment something is pushed to it, which is usually while this page sits here
  // watching a different one.
  let names = [];
  try { names = await (await fetch('boards')).json(); } catch { /* offline - still list ours */ }

  // A board exists in the store only once it holds data, so the board this page is
  // watching can legitimately be missing from that list.
  const all = [...new Set([BOARD, ...names])]
    .sort((a, b) => a.localeCompare(b, undefined, { sensitivity: 'base' }));

  for (const name of all) list.append(boardRow(name));
}

$('boardBtn').onclick = () => {
  if ($('boardMenu').hidden) openBoardMenu(); else closeBoardMenu();
};

$('boardForm').onsubmit = (e) => {
  e.preventDefault();
  goToBoard($('boardInput').value);
};

document.addEventListener('mousedown', (e) => {
  // Anything but the armed button itself takes the arming back.
  if (armedDelete && e.target !== armedDelete) disarmDelete();
  if ($('boardMenu').hidden) return;
  if (!$('boardMenu').contains(e.target) && !$('boardBtn').contains(e.target)) closeBoardMenu();
});

document.addEventListener('keydown', (e) => {
  if (e.key === 'Escape' && !$('boardMenu').hidden) closeBoardMenu();
});

// Enter opens the typed board, alongside the Open button. Handled on the key rather
// than left to the form's implicit submission, so the field behaves the same however
// the keystroke arrives.
$('boardInput').onkeydown = (e) => {
  if (e.key !== 'Enter') return;
  e.preventDefault();
  goToBoard($('boardInput').value);
};

// Carried across so the feed can offer a way back to this exact board.
$('feedLink').href = 'feed?board=' + encodeURIComponent(BOARD);
