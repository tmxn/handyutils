# PlotBridge

Live Plotly plots for debugging. A tiny localhost server holds the data and any
number of browser pages render it, so plotting a few thousand points from a
running program is a push, not a copy-paste-parse round trip.

Two halves: a **server + page** (P0, working and verified) and a **Visual Studio
extension** (P1, built but not yet exercised against a live debug session — see
[The extension](#the-extension)). Everything the server does is usable by hand
without the extension.

## Run it

For day-to-day use, publish once and run the exe:

```bash
dotnet publish PlotBridge/server -c Release -o PlotBridge/dist
```

Then `PlotBridge\dist\PlotBridge.Server.exe` — double-click it, pin a shortcut,
or launch it from anything. Republish after changing the server or the page.

For development, skip the publish:

```bash
dotnet run --project PlotBridge/server
```

Either way, open <http://localhost:8777>. Port comes from `--port 9000` or
`PLOTBRIDGE_PORT`; it binds `127.0.0.1` only. A second instance refuses the port
with a one-line message instead of a stack trace.

**Don't run the exe out of `bin`.** `wwwroot` is not copied there — the build
leaves a `staticwebassets` manifest pointing back into the source tree, so
`bin\Debug\net9.0\PlotBridge.Server.exe` is tied to this checkout. `dist` has a
real `wwwroot` beside the exe and moves anywhere.

(The content root is pinned rather than taken from the working directory, which
is the default. Otherwise launching from a shortcut serves `/health` happily and
404s the page, because it goes looking for `wwwroot` under whatever directory the
launcher happened to set.)

```bash
powershell -ExecutionPolicy Bypass -File PlotBridge/tools/Demo.ps1
```

That pushes sample data through every input path and prints a line per check —
it doubles as the smoke test. `tools/Test-Contract.ps1` is the other one: it
pins down the debugger value-string formats the extension relies on.

## The extension

Adds **"Plot with PlotBridge"** to the magnifying-glass menu on a variable in
Locals, Autos, Watch and DataTips.

### Build and install

The "Visual Studio extension development" workload is **not** required — the
packaging targets come from the `Microsoft.VSSDK.BuildTools` NuGet package:

```bash
"C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe" PlotBridge\vsix\PlotBridge.Vsix.csproj -t:Restore,Rebuild -p:Configuration=Release
```

Then close Visual Studio and double-click
`PlotBridge\vsix\bin\Release\PlotBridge.Vsix.vsix`. It's about 40 KB — the SDK
assemblies are excluded because VS supplies them at run time.

### How the pieces connect

1. `PlotBridge.natvis` declares a `<UIVisualizer>` with a **ServiceId** GUID and
   lists the types it applies to.
2. The pkgdef maps that GUID to the package, so the debugger's `QueryService`
   loads the extension **on demand** — nothing auto-loads, and VS startup is
   untouched until you click the glyph.
3. `IVsCppDebugUIVisualizer.DisplayValue` receives the variable's
   `IDebugProperty3`, already scoped to the right frame and process.
4. `DebuggerPoints` walks the children and keeps their formatted value strings.
5. Those go to `POST /push` as `text/plain`; the server parses them.

Step 4 is why there is no memory-layout table: `IEnumDebugPropertyInfo2.Next` is
batched, so thousands of children come back in one marshalled call.

### Which children get read

Value strings only contain numbers if the element type's *display* spells them
out, and plenty of natvis entries don't — a type with an `<ExpandedItem>`-only
entry renders as `{...}`. So there are four routes, tried in order, and the status
bar names the one that fired:

| Route | When | Cost |
|---|---|---|
| `[chart3d]` / `[chart2d]` synthetic | the container exposes one | one extra enumeration |
| `[0]..[n-1]` value strings | they contain numbers | nothing extra |
| `[0..99]` range groups | the debugger bucketed the collection | one per bucket |
| each `[N]` expanded, numeric children joined | elements display as `{...}` | **one enumeration per element** |

Only the last route is slow, and only it needs the `DEBUGPROP_INFO_PROP` flag
that makes the engine build a property object per child — the flat path never
pays for it.

A **chart view** is just a synthetic node whose children render one point per
line. It is built with `IndexListItems` over a `view(rawxyz)` / `view(rawxy)`
`DisplayString`, giving tab-separated numbers. The 3D node is preferred; when
the element type has no `z` an `Optional` 3D entry drops out by itself, so the
choice settles dimensionality too. Worth adding for any container you plot
often — it turns the slow route into the fastest one.

`Test-Contract.ps1` pins down every value-string format above.

### Adding a type

Edit `%USERPROFILE%\Documents\Visual Studio 2022\Visualizers\PlotBridge.natvis`
and copy a three-line `<Type>` block. Restart the debug session — no extension
rebuild. Registered out of the box: `std::vector` of `gp_Pnt`, `gp_Vec`,
`gp_Dir`, `gp_XYZ`, `gp_Pnt2d`, `gp_Vec2d`, `gp_Dir2d`, `gp_XY`, `double`,
`float`, `int`.

**`<UIVisualizer>` must be the last child of `<Type>`,** and a `<Type>` that has
one cannot also have `<DisplayString>` or `<Expand>` — natvis.xsd defines the
body as an `xs:choice` between the two. Get it wrong and the whole file fails
schema validation and is silently ignored. Validate after editing:

```bash
powershell -ExecutionPolicy Bypass -File PlotBridge\tools\Test-Natvis.ps1
```

The extension never overwrites your copy; a newer version is written alongside
as `PlotBridge.natvis.new`.

### If the glyph doesn't appear

**A project's own .natvis beats the user directory.** natvis.xsd is explicit:
project files take precedence over the user directory, which takes precedence
over the system-wide one — regardless of `Priority`. So if your solution already
has a `<Type>` entry for the same type, its entry wins the type match.

To tell whether that's the problem, compare a type your project's natvis covers
against one it doesn't — `std::vector<double>` is usually uncontested. Glyph on
one but not the other means precedence; glyph on neither means something else.

For ground truth, turn on **Tools → Options → Debugging → Output Window →
Natvis diagnostic messages → Verbose**. The Debug output pane then names every
natvis file loaded and every entry rejected, which beats guessing.

**The bootstrap used to deadlock.** `NatvisDeployer` installs the natvis, but it
runs from package initialisation — and the package is only loaded when the
debugger queries the ServiceId, which only happens once the natvis exists.
Nothing ever ran on a fresh install. Fixed by auto-loading on `SolutionExists`
(background load); if you hit it on an older build, copy
`vsix\PlotBridge.natvis` into the Visualizers folder by hand.

### Settings

`%LOCALAPPDATA%\PlotBridge\vsix.settings`, plain `key=value`. Board, chart, mode
and replace are remembered from the dialog. `askEveryTime=false` (or ticking
"plot straight away from now on") skips the dialog. `maxPoints` caps extraction;
`port` must match the server.

## Getting data in

Five ways in, all equivalent once they land - but only `/ingest` tells you when
that happened.

### Write a file and wait for it: `POST /ingest`

The only way in that tells the caller when the data has landed. Use it from a
script or a tool, where guessing is not an option:

```
POST /ingest?file=<absolute path, percent-encoded>
```

`file` repeats, so one request can carry a whole run. Only paths cross the wire —
the server reads the files itself, so a large point set never becomes a large
request body. That is safe because the server binds `127.0.0.1` only, so the caller
is already local.

The response does not come back until every file is in the store: **200** means
`/export` and `/render` will see the data on the caller's next line, with no sleep
and no re-polling. **400** lists what failed and why, per file, and says which of
the others did land.

Destination comes from each filename, the same `__` convention the drop folder uses.
`board`, `chart` and `series` query parameters override it; `series` is refused
alongside more than one file, because each would overwrite the last and the response
would show one series with no hint the rest were swallowed.

### Drop a file

Asynchronous, so a writer cannot tell whether its file was picked up. Fine from the
Immediate Window, where a human is watching the page; use `/ingest` from anything
that has to act on the result.

Write a text file into `%LOCALAPPDATA%\PlotBridge\drop\`. The **filename picks
the destination**, split on `__`:

| File | Board | Chart | Series |
|---|---|---|---|
| `pts.tsv` | default | main | pts |
| `hull__pts.tsv` | default | hull | pts |
| `run2__hull__pts.tsv` | run2 | hull | pts |

Rewriting the file replaces the series in place. From the Visual Studio
Immediate Window on a **managed** frame:

```
System.IO.File.WriteAllText(@"C:\Users\<you>\AppData\Local\PlotBridge\drop\pts.tsv", tsv)
```

### Paste into the page

The **Paste data** panel takes raw Visual Studio *Copy Value* output as-is —
`[0] {X=1.5 Y=2.25 Z=0}` and friends. This is the workflow the tool replaces,
kept deliberately because it always works.

### POST text

```bash
curl -X POST "http://localhost:8777/push?chart=main&series=pts" -H "Content-Type: text/plain" --data-binary "@points.tsv"
```

### POST JSON

```json
{ "board": "default", "chart": "main", "series": "pts",
  "mode": "auto", "x": [1,2], "y": [3,4], "z": null,
  "style": { "mode": "markers", "size": 5, "color": "#2a78d6" },
  "replace": true }
```

Coordinates may arrive in whichever form is convenient — first one present wins:

| Field | Shape |
|---|---|
| `x` / `y` / `z` | columnar, the cheapest for large sets |
| `points` | `[[x,y], [x,y,z], …]` |
| `values` | `[y, y, …]` — plotted against index |
| `text` | anything the tolerant text parser accepts |

`replace: false` appends as `name #2` instead of overwriting.

From PowerShell, use the bundled sender rather than `ConvertTo-Json` (see
[Gotchas](#gotchas)):

```powershell
.\PlotBridge\tools\Send-PlotBridge.ps1 -Series 'hull' -X $xs -Y $ys -Chart 'geometry'
```

## Getting data out

The mirror of the four ways in. Everything here works from a script, a shell or a
CI step — no clicking, and (except for PNG) no page open.

### Text, for a pipe or a file

```bash
curl "http://localhost:8777/export?chart=stage1&format=tsv" -o points.tsv
```

`format` is `tsv` (default), `csv`, `json` or `ndjson`. `board`, `chart` and
`series` all filter and are all optional; `download=1` sends it as an attachment
with a sensible filename.

| Format | Shape | Reach for it when |
|---|---|---|
| `tsv` / `csv` | one row per point: `chart series i x y z` | awk, `Import-Csv`, pandas |
| `ndjson` | one object per series | `jq` |
| `json` | the same objects in an array | reading it yourself |

Two response headers save a round trip: **`X-PlotBridge-Series`** and
**`X-PlotBridge-Points`**. Zero series means the filter matched nothing; series
with zero points means the chart is there but empty. An empty body alone cannot
tell you which.

**`ndjson` and `json` are shaped like `POST /push`,** so an export is a valid
input — copying a chart between boards is a loop, not a conversion:

```bash
curl -s "http://localhost:8777/export?chart=stage1&format=ndjson" > series.ndjson
```

```bash
while read -r s; do curl -s -X POST "http://localhost:8777/push?board=archive" -H "Content-Type: application/json" --data-binary "$s"; done < series.ndjson
```

The delimited formats deliberately do **not** round-trip: the leading
`chart`/`series`/`i` columns would be read as coordinates by the tolerant text
parser, which takes the first three numbers on a line. A 2D series leaves `z`
empty rather than `0`, so "flat" and "no third dimension" stay distinguishable.

### PNG, from any angle

```bash
curl "http://localhost:8777/render?chart=stage1&eye=iso&width=900&height=700" -o iso.png
```

`eye` is the point of the whole thing: it is how a caller that cannot drag a mouse
still gets to look from somewhere useful. Give it `x,y,z` in Plotly camera space or
a preset — `iso`, `front`, `back`, `left`, `right`, `top`, `bottom`. `up` takes the
same forms. `width`, `height`, `scale` and `timeoutMs` do the obvious thing.

The render happens in an **off-screen div on the page**, not the visible plot, so it
never steals the tab, camera or zoom of whoever is watching — and the axis styling,
palette, legend and equal-aspect handling come from the same `baseLayout` the
on-screen plot uses, so the image matches the page.

Failures are fast and say why, rather than timing out:

| Status | Means |
|---|---|
| `503` | no page attached to that board — open it and retry |
| `404` | no such chart (the response lists the ones that exist), or it has no series |
| `400` | `eye`/`up` isn't a vector or a known preset |
| `504` | the page didn't answer inside `timeoutMs` (default 15000) |
| `502` | Plotly threw on the page; the reason is passed through |

`X-PlotBridge-Mode` reports the dimensionality the page resolved. Worth reading
when a camera argument seems ignored: **`eye` only bites in 3D**, and mode is
auto-detected from whether the series carry `z`.

### Watching what a caller renders: `/feed`

Open <http://localhost:8777/feed> and leave it up. Every image `GET /render`
produces appears there, newest first, and the page updates the moment one lands.

This exists because `/render` is otherwise a closed loop: the PNG goes back to
whoever asked for it and nowhere else, so a person sitting next to an automated
caller sees the picture only if that caller volunteers a path and leaves the file
behind. The feed removes the luck.

Attempts that produced no image are listed too, with the reason — a render against
a board with no page open is worth seeing, and an empty feed should mean "nothing
was asked for", not "something failed quietly".

The last ten are kept, in memory only; `PLOTBRIDGE_FEED_SIZE` changes the count.
Nothing is written to disk, and a restart empties it — the point is to watch, not
to archive. The page is a pure observer: it never opens a websocket, because board
clients are the pool `/render` picks a rasteriser from, and a watcher with no plot
of its own would be a bad one to pick.

## Endpoints

| Route | Purpose |
|---|---|
| `GET /` | the board page; `?board=name` picks a board |
| `GET /health` | port, data directory, known boards — the liveness probe |
| `GET /snapshot?board=` | full board state as JSON |
| `DELETE /boards?board=` | delete a board: out of the list, off disk; 404 if there is no such board |
| `POST /push` | one series in; JSON or `text/plain` |
| `POST /ingest?file=&board=&chart=&series=` | read files off disk, answering only once they are in the store |
| `POST /clear?board=&chart=` | clear one chart, or the whole board if `chart` is omitted |
| `GET /export?board=&chart=&series=&format=` | data back out as tsv/csv/json/ndjson |
| `GET /render?board=&chart=&eye=&width=&height=` | PNG, rendered by an attached page |
| `POST /render/result?id=` | where the page posts the bytes back (internal) |
| `GET /feed` | the render feed page — the last few images `/render` produced |
| `GET /feed/list?since=&waitMs=` | feed metadata as JSON; `since` blocks until it changes |
| `GET /feed/img/{id}` | the bytes of one feed entry |
| `GET /ws?board=` | WebSocket the pages listen on |

## The page

- **Board picker.** Click the board name in the top bar for the list of boards the
  server holds, or type a name to open one that does not exist yet. Choosing a
  board navigates, so the address bar always names the board on screen - which
  matters, because that is the URL `/render` asks a caller to open.
  Each row has a delete button, two clicks: the first arms it, the second removes
  the board from the list and its snapshot from disk. Clearing a board and deleting
  one are different things - `POST /clear` empties the charts and keeps the board.
- **Render feed link.** *render feed* in the top bar goes to `/feed`, and carries
  the current board so the way back lands where you left. Each feed entry names the
  board it was rendered from and links to it.
- **Charts as tabs.** Push to a new chart name and a tab appears. *Follow
  pushes* jumps to whichever chart just received data; turn it off to stay put.
- **Views survive updates.** Once you zoom, pan or rotate, re-pushing at every
  breakpoint won't yank the view. A chart you *haven't* touched still autoscales
  to whatever arrives, and *Reset view* hands control back to the data. 3D relies
  on Plotly's `uirevision`; 2D can't (see [Gotchas](#gotchas)).
- **Equal aspect** means `scaleanchor` in 2D and `aspectmode: "data"` in 3D.
  On by default, because geometry is the common case — except on a chart whose
  first push was `values`, which is a signal plot.
- **WebGL above 2000 points** in 2D (`scattergl`), so 5k+ stays interactive.
- **State is server-side.** Colours, draw modes, visibility and chart options
  persist to `%LOCALAPPDATA%\PlotBridge\boards\*.json`, survive a restart, and
  sync across every page on the board.
- Click a point count to get the values as a table, or copy the series as TSV.

### Colour

Series colours come from a fixed eight-slot categorical palette, validated for
colour-vision deficiency against both the light and dark chart surfaces. A
series keeps its slot for life, so deleting one never repaints the others.

Only the **first three** slots clear the all-pairs separation floors that a
scatter plot demands — with eight hues in play, pairs like red/orange are not
reliably distinguishable. So the slot also drives a **marker symbol**: past the
third series, shape is what actually separates them, and the legend carries
both. Colour and symbol together stay unique for 64 series on one chart. Any
series colour can be overridden with the swatch, which then wins in both themes.

**Lines are always solid.** The slot used to pick a dash pattern as well, on the
same redundancy argument. It was a mistake: on a point sequence a dashed
polyline reads as gaps in the geometry, so it misdescribes the data to repeat
something the marker shape already says.

## Gotchas

- **Windows PowerShell 5.1 `ConvertTo-Json` corrupts typed arrays.** A
  `double[]` serialises as `{"value":[…],"Count":n}`, not a JSON array, and its
  numbers follow the current culture — on a comma-decimal locale `1,5` reads as
  two numbers. `Send-PlotBridge.ps1` builds the JSON by hand with
  `InvariantCulture` and `"R"` precision to avoid both.
- **Any local process can push.** That's the point, but it means a web page you
  visit could too, so cross-origin POSTs are refused. Worst case is a stray
  plot; there's nothing sensitive behind the port.
- **3D `scaleratio` does nothing.** The original `plotly_multiseries_editor.html`
  set `scaleratio` on the scene axes, so its 3D "uniform" toggle was a no-op —
  `aspectmode` is the property that matters. Fixed here.
- **`scaleanchor` defeats `uirevision` in 2D.** With equal aspect on, Plotly's
  constrained-axis code recomputes both ranges on every data change and throws
  the user's zoom away — measured, not assumed: identical pushes hold the range
  with equal aspect off and reset it with equal aspect on. So `app.js` captures
  the range from `plotly_relayout` and re-applies it explicitly, and asks for
  `autorange: true` outright when the user hasn't zoomed (on a `uirevision`
  change Plotly otherwise reverts to a stashed pre-interaction range). 3D needs
  none of this — `aspectmode: "data"` doesn't fight `uirevision`.

## Roadmap

- **P0 — done and verified.** Server, page, all four input paths.
- **P1 — built; awaiting a live debug session.** The magnifying-glass visualizer.
  Verified statically: the VSIX packages, the natvis validates against
  `natvis.xsd`, the pkgdef carries the service→package mapping, the ServiceId is
  consistent across both, and `Test-Contract.ps1` covers the value-string
  formats. Not yet verified: that the glyph actually appears on a
  `std::vector<gp_Pnt>` and returns points. That needs the extension installed
  and a native debug session.
- **P2** — right-click an expression in the editor → plot it. A thin second entry
  point over the same extraction code, and the fallback if the service
  registration turns out to misbehave. It has to earn its `IDebugProperty3` the
  hard way (`IVsDebugger` → `IDebugStackFrame2` →
  `IDebugExpressionContext2.ParseText` → `EvaluateSync`), which is why the glyph
  came first.

  The Copilot-style hover action button in Locals is *not* an extensibility
  point — it is first-party, with no public API for adding one. The value-cell
  glyph is the supported equivalent.
- **P3** — `IDebugMemoryBytes2.ReadAt` as an opt-in fast path if child
  enumeration proves too slow on real data (`std::vector<gp_Pnt>` is a packed
  array of 24-byte triples, and `IDebugProperty3` derives from
  `IDebugProperty2`, so the same property offers both). Then a `PlotDump()`
  helper for what no decoder reaches — `NCollection_Sequence`, `TopoDS_*`,
  `Poly_Triangulation` — and break-counter series naming so successive stops
  overlay instead of overwrite.
- **P3** — a `PlotDump()` helper compiled into Debug builds, func-eval'd by the
  extension, for everything the memory decoder can't reach (`NCollection_Sequence`,
  `TopoDS_*`, `Poly_Triangulation`). Plus break-counter series naming, so
  successive stops overlay instead of overwrite.

## Layout

```
PlotBridge/
  server/
    Program.cs        endpoints, push pipeline, WebSocket loop
    Models.cs         Board / Chart / Series / Style, PushRequest
    Store.cs          in-memory state + debounced JSON snapshots
    Hub.cs            WebSocket fan-out
    DropWatcher.cs    drop-folder watching (asynchronous)
    Ingest.cs         the filename convention and file reading, shared
    TextPoints.cs     tolerant text -> coordinates
    wwwroot/          index.html, app.js, style.css, plotly.min.js
  vsix/
    PlotBridge.natvis     type registrations - edit the deployed copy, not this
    PlotBridgePackage.cs  AsyncPackage; registers the service, deploys the natvis
    VisualizerService.cs  IVsCppDebugUIVisualizer entry point
    DebuggerPoints.cs     IDebugProperty3 -> value strings, incl. group recursion
    PlotBridgeClient.cs   POST to the server
    PushDialog.cs         board / chart / series, code-only WPF
    Settings.cs           remembered choices in vsix.settings
    NatvisDeployer.cs     installs the natvis without clobbering your edits
  tools/
    Send-PlotBridge.ps1   scriptable push
    Demo.ps1              sample data through every path
    Test-Contract.ps1     debugger value strings -> expected coordinates
    Test-Natvis.ps1       schema-validate a natvis before trusting it
  dist/                   publish output - run PlotBridge.Server.exe from here
```
