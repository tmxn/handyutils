/* ------------------------------------------------------------- PlotBridge feed
   Watches /render output so a person can see what an automated caller is looking
   at. Read-only by design: it never opens a websocket, because board clients are
   the pool /render picks a rasteriser from and a watcher with no plot would be a
   bad one to pick. */

const $ = id => document.getElementById(id);

/* ---------------------------------------------------------------------- theme */
/* Same key as the main page, so a preference set on one carries to the other. */

let themePref = localStorage.getItem('plotbridge.theme') || 'auto';

function applyTheme() {
  if (themePref === 'auto') document.documentElement.removeAttribute('data-theme');
  else document.documentElement.setAttribute('data-theme', themePref);
  $('themeBtn').textContent = 'theme: ' + themePref;
}

$('themeBtn').onclick = () => {
  themePref = themePref === 'auto' ? 'light' : themePref === 'light' ? 'dark' : 'auto';
  localStorage.setItem('plotbridge.theme', themePref);
  applyTheme();
};

applyTheme();

/* ---------------------------------------------------------------- formatting */

function ago(ms) {
  const s = Math.max(0, (Date.now() - ms) / 1000);
  if (s < 5) return 'just now';
  if (s < 60) return Math.floor(s) + 's ago';
  if (s < 3600) return Math.floor(s / 60) + 'm ago';
  if (s < 86400) return Math.floor(s / 3600) + 'h ago';
  return new Date(ms).toLocaleString();
}

function size(bytes) {
  if (!bytes) return '';
  return bytes < 1024 * 1024
    ? Math.round(bytes / 1024) + ' KB'
    : (bytes / (1024 * 1024)).toFixed(1) + ' MB';
}

function chip(text) {
  const el = document.createElement('span');
  el.className = 'chip';
  el.textContent = text;
  return el;
}

/* -------------------------------------------------------------------- render */

const cards = new Map();   // id -> element, so arrivals don't rebuild the page

function card(shot) {
  const el = document.createElement('article');
  el.className = 'shot';
  el.dataset.id = shot.id;

  if (shot.error) {
    const fail = document.createElement('div');
    fail.className = 'shot-fail';
    fail.textContent = shot.error;
    el.append(fail);
  } else {
    const img = document.createElement('img');
    img.className = 'shot-img';
    img.loading = 'lazy';
    // The dimensions are known before the bytes arrive, so declare them: without an
    // intrinsic ratio the grid sizes the row from an image of height zero and the
    // caption below ends up clipped once it loads.
    img.width = shot.width;
    img.height = shot.height;
    img.src = '/feed/img/' + encodeURIComponent(shot.id);
    img.alt = `${shot.board} / ${shot.chart}`;
    img.onclick = () => openLightbox(shot);
    el.append(img);
  }

  const meta = document.createElement('div');
  meta.className = 'shot-meta';

  // A link, not a label: seeing a render is usually the moment you want the live
  // board it came from, and that board is not necessarily the one you arrived from.
  const where = document.createElement('a');
  where.className = 'where';
  where.href = '/?board=' + encodeURIComponent(shot.board);
  where.title = 'Open board ' + shot.board;
  where.textContent = shot.board + ' / ' + shot.chart;
  meta.append(where);

  if (shot.eye) meta.append(chip('eye ' + shot.eye));
  if (shot.mode) meta.append(chip(shot.mode));
  if (!shot.error) {
    meta.append(chip(`${shot.width}×${shot.height}${shot.scale !== 1 ? ' ×' + shot.scale : ''}`));
    if (shot.bytes) meta.append(chip(size(shot.bytes)));
  }

  const when = document.createElement('span');
  when.className = 'when';
  when.dataset.at = shot.atMs;
  when.textContent = ago(shot.atMs);
  meta.append(when);

  el.append(meta);
  return el;
}

function paint(data) {
  const feed = $('feed');
  const seen = new Set();

  // Newest first, and the server already sends them that way. Insert missing cards
  // in place rather than rebuilding: a rebuild would flicker every image and drop
  // whatever the lightbox is showing.
  let anchor = null;
  for (const shot of data.shots) {
    seen.add(shot.id);
    let el = cards.get(shot.id);
    if (!el) {
      el = card(shot);
      cards.set(shot.id, el);
    }
    if (anchor === null) feed.prepend(el);
    else anchor.after(el);
    anchor = el;
  }

  for (const [id, el] of cards) {
    if (seen.has(id)) continue;
    el.remove();
    cards.delete(id);
  }

  for (const el of feed.children) el.classList.remove('newest');
  if (feed.firstElementChild) feed.firstElementChild.classList.add('newest');

  const bare = data.shots.length === 0;
  feed.hidden = bare;
  $('empty').hidden = !bare;
  $('capacity').textContent = data.capacity;
  $('counts').textContent = data.shots.length
    ? `${data.shots.length} of ${data.capacity}`
    : '';
}

// Relative times go stale on their own; nothing else on the page needs a tick.
setInterval(() => {
  for (const el of document.querySelectorAll('.when')) el.textContent = ago(Number(el.dataset.at));
}, 1000);

/* ------------------------------------------------------------------ lightbox */

function openLightbox(shot) {
  $('lbTitle').textContent = `${shot.board} / ${shot.chart} — ${shot.width}×${shot.height}`;
  $('lbOpen').href = '/feed/img/' + encodeURIComponent(shot.id);
  const body = $('lbBody');
  body.replaceChildren();
  const img = document.createElement('img');
  img.src = '/feed/img/' + encodeURIComponent(shot.id);
  body.append(img);
  $('lightbox').hidden = false;
}

function closeLightbox() {
  $('lightbox').hidden = true;
  $('lbBody').replaceChildren();
}

$('lbClose').onclick = closeLightbox;
$('lightbox').onclick = e => { if (e.target === $('lightbox') || e.target.id === 'lbBody') closeLightbox(); };
document.addEventListener('keydown', e => { if (e.key === 'Escape') closeLightbox(); });

/* ---------------------------------------------------------------- the stream */

function setLive(live, note) {
  const el = $('status');
  el.classList.toggle('live', live);
  el.classList.toggle('down', !live);
  $('statusText').textContent = note || (live ? 'watching' : 'server unreachable');
}

const sleep = ms => new Promise(r => setTimeout(r, ms));

async function watch() {
  let version = -1;
  let failures = 0;

  for (;;) {
    try {
      // The first pass takes whatever is there; after that the request parks on the
      // server until the feed actually changes, so a new image shows up the moment
      // it is rendered rather than on the next poll tick.
      const url = version < 0 ? '/feed/list' : `/feed/list?since=${version}&waitMs=25000`;
      const res = await fetch(url, { cache: 'no-store' });
      if (!res.ok) throw new Error('HTTP ' + res.status);

      const data = await res.json();
      failures = 0;
      setLive(true);
      if (data.version !== version) {
        version = data.version;
        paint(data);
      }
    } catch {
      failures++;
      setLive(false);
      await sleep(Math.min(500 * failures, 5000));
    }
  }
}

watch();

/* ------------------------------------------------------------------ navigation
   The plot page hands its board over in the query string, so the way back lands on
   the board you left rather than on default. Arriving here cold (a bookmark, or
   /health's feed url) has no board to return to, so the link stays generic. */

const FROM_BOARD = new URLSearchParams(location.search).get('board') || '';
if (FROM_BOARD) {
  $('boardLink').href = '/?board=' + encodeURIComponent(FROM_BOARD);
  $('boardLink').textContent = 'board ' + FROM_BOARD;
}
