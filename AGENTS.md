# AGENTS GUIDE

This file is for whoever - person or agent - works on this repo next. It is the
map, the contract with the server, and the list of things that have already
cost somebody an afternoon.

## What this is

A C# AutoCAD plugin (.NET 8, AutoCAD 2025), loaded with `NETLOAD`, driven
entirely from AutoCAD's command line. It is the CAD-side client of
**autodraw_server**: it submits the drawing, asks the server to take steps, and
lays the drawing the server sends back into the open document.

It holds no drawing logic of its own. Every decision about what is drawn, where
and why belongs to the server. This side transfers, erases, redraws, and asks
the designer the questions the server said to ask.

## The other half, and where the contract is written

The server is a separate repo - `C:\repos\autodraw_server`. The wire contract
lives there and nowhere else:

- `automation/AUTHORING.md` **section 6b** - pieces, object ids and groups. Read
  this before touching `PieceGroupService`.
- `automation/AUTHORING.md` **section 3** the step contract, **9** what survives
  a step, **9a/9b** what a step hands out.
- `automation/record_dxf.py` - the actual reader and writer of the XDATA this
  plugin parses. If you are unsure what a stamp looks like, read
  `_identity_tags` and `_encode` rather than guessing.

There are no tests here and no way to check a guess about the contract locally.
Read the server's source; it is a short walk and it is authoritative.

## Layout

`Notes/Structure.txt` and `README.md` are both **out of date** - they name files
and commands (`ProjectData.cs`, `UiRenderer.cs`, `ADLSC`, `ADSSC`) that no
longer exist. Trust this section instead.

| File | What it is |
| --- | --- |
| `autodraw.cs` | `IExtensionApplication`. Builds the two global statics, `autodraw.Auth` and `autodraw.AutoDraw`, on load. |
| `Commands/AuthCommands.cs` | `ADLOGIN`, `ADLOGOUT`, `ADWHOAMI`. |
| `Commands/AutoDrawCommands.cs` | Everything else. The whole designer-facing CLI. |
| `Services/ApiService.cs` | HTTP. Bearer token on every request; `BaseUrl` defaults to `http://localhost:5001/api`. |
| `Services/AuthService.cs` | Login, token, role. |
| `Services/AutoDrawService.cs` | One method per automation endpoint, plus the session's project and branch. |
| `Services/DxfTransferService.cs` | Erase, export, import. The mechanics of the redraw. |
| `Services/AutoDrawVisualizer.cs` | The INFO status board and the NOTES panel. |
| `Services/PieceGroupService.cs` | One group per drawn piece, rebuilt after every redraw. |
| `Models/*.cs` | DTOs, mirroring the server's JSON. No logic. |

## The commands

| Command | What it does |
| --- | --- |
| `ADLOGIN` / `ADLOGOUT` / `ADWHOAMI` | Auth only. |
| `ADSTART` | Load a project by id and lay its whole drawing in, replacing what is there. The resync of last resort. |
| `ADSTATUS` | Login, project, next step, and whether the drawing is in step with the record. |
| `ADCONTINUE` | Submit and take one substep. |
| `ADRUN` | Submit and run on until something needs a person. One submission and one reply however many steps run. |
| `ADBACK` | Undo back to a substep. Full resync from the record. |
| `ADFORWARD` | Redo, without running the step again - the server restores a drawing it already made. |
| `ADBRANCH` | List the lines of work and load one's tip. |
| `ADNOTE` | Attach a note to the state now loaded. |

## The redraw contract

Every command that changes state ends in the same shape, and it is the shape to
preserve when adding one:

1. submit the drawing (or just ask, for the ones that do not submit)
2. erase
3. `DxfTransferService.ImportBase64` - the server's DXF, cloned into modelspace
4. one transaction that rebuilds the derived things: **groups**, the INFO board,
   the NOTES panel
5. `ed.Regen()`

There are two erases and the difference matters:

- **`EraseOwned`** removes only entities carrying `AUTODRAW` XDATA, and spares
  anything marked `owner=external`. This is the ordinary `ADCONTINUE` / `ADRUN`
  path: the server owns what it drew and regenerates it, and MPanel's mesh is
  left alone because the server cannot redraw it.
- **`EraseAllExcept`** removes everything but INFO and NOTES. Used by `ADSTART`,
  `ADBACK`, `ADFORWARD`, `ADBRANCH`, and by `ADCONTINUE` when the server sets
  `Resync` - all cases where the record is authoritative and the server is
  sending the full scope back, MPanel's geometry included.

INFO and NOTES are decoration. They are excluded from the submission and from
the ordinary erase, and rebuilt from scratch every cycle so they always describe
the state now loaded.

## XDATA: how an entity says what it is

Under the `AUTODRAW` appid, as a run of 1000 strings, one name/value pair each:

```
fid:=178 | tag=spread | object:=1 | item:=0 | panel=P1 | role=panel.cut
```

(Shown on one line for reading. On the entity each pair is its own string -
which is why `EraseOwned` can compare one against `owner=external` exactly.)

- `name=text` - the value is a **string**
- `name:=json` - the value is **JSON-encoded**

The marker is required rather than inferred, and it is not decoration: a panel
id really is the string `"P1"`, so `object:=1` is the number 1, and guessing
would hand either one back as the wrong thing.

What travels:

- **`fid`** - the record's id for this entity, how the server recognises its own
  work coming back.
- **`object`** - which drawn piece the entity belongs to. The machinery's own
  idea, not any product's: a piece is a panel, a doubler, whatever the step
  drew. Unique across the whole drawing, not per step.
- **`owner=external`** - somebody else's geometry, adopted but not regenerable.
  Never erased by `EraseOwned`.
- `tag=`, `role=`, and the product's own identity fields - the plugin does not
  interpret any of these and should not start.

## Groups are rebuilt here, every redraw

A group refers to its members by their **handles**, and handles only survive a
file being *opened*. This plugin parses the server's DXF and clones the entities
into a document that is already open, so they arrive with new handles and the
groups the server sent refer to nothing.

`PieceGroupService.Rebuild(tr, db)` runs at **all five** redraw sites -
`ADSTART`, `ADBACK`, `ADBRANCH`, `ADFORWARD`, and `Advance`, which is both
`ADCONTINUE` and `ADRUN` - inside the transaction that already rebuilds the
board. It needs nothing from the server: it buckets modelspace by each entity's
`object:=` and makes one selectable group per bucket, named `PIECE_<object>`.

Four rules, all of which are easy to break silently:

- **No `object`, no group.** Geometry the server does not own carries no
  `AUTODRAW` XDATA at all, and some that it does own has none on purpose - the
  fabric roll outline and its margin lines, which must not move when a panel is
  dragged.
- **Only `object:=`.** See the marker, above.
- **R2000 or later.** R12 has no objects section, so it cannot carry a group,
  and anything saving down to it drops them with no error. The only file this
  plugin writes is the submission, at `DwgVersion.Current` - keep it that way.
- **Replace, do not accumulate.** Every rebuild deletes the existing `PIECE_`
  groups first. Skip that and each redraw leaves the last one's corpses behind,
  holding entities that have since been erased.

Nothing reads a group back - the server does not care whether they exist. They
are for the designer at the manual `nest_panels` step, where the job is to slide
whole pieces into a tighter nest: the server has already squared each piece to
the roll and laid the pieces down the fabric, so what is left is translation,
not rotation. A piece is around seventeen entities - cut outline, corner stamps,
the fitting's slits, seam marks, labels - and without a group every move is a
window-select of all seventeen. Anything missed stays behind at the old
position, which is silent, and surfaces much later as a panel missing its
labels.

Because `object` is the machinery's idea rather than a product's, this is the
same loop for every product and every step. Do not teach it what a panel is.

## Building, and the NETLOAD lock

```bash
dotnet build
```

References come from `C:\Program Files\Autodesk\AutoCAD 2025\` by hint path, so
AutoCAD 2025 has to be installed. There are no tests and no CI. The build
succeeding and then running it in AutoCAD is the whole of verification.

**The lock.** `NETLOAD` maps the DLL for the life of the AutoCAD session and
.NET cannot unload it, so while AutoCAD is open the build fails on the copy into
`bin\` with `MSB3027 ... locked by: AutoCAD Application`. The compile itself has
already succeeded at that point; only the copy failed. To confirm code compiles
without closing AutoCAD:

```bash
dotnet build --no-incremental -o <some scratch dir>
```

The iteration loop today is: close AutoCAD, build, reopen, re-`NETLOAD`. Ways
out, in order of effort - **none implemented yet**:

1. **Never build into the file AutoCAD holds.** A post-build copy to
   `%LOCALAPPDATA%\autodraw\run\autodraw_<timestamp>.dll`, and `NETLOAD` that.
   The build never collides, and AutoCAD can stay open while you work.
2. **Stop typing NETLOAD.** A small `acad.lsp` that netloads the newest DLL in
   that folder at startup. The loop becomes edit, build, restart AutoCAD.
3. **No restart at all**: a loader stub, netloaded once, that reads the real
   assembly's *bytes* into a collectible `AssemblyLoadContext` and registers the
   commands itself. Real, but it leans on
   `Autodesk.AutoCAD.Internal.Utils.AddCommand`, which is unsupported and shifts
   between releases, and every reload resets the statics - `autodraw.Auth`
   included, so you log in again each time.
4. **For scripted checks**, `accoreconsole.exe /i test.dwg /s test.scr` is
   headless AutoCAD: seconds to start, a fresh process each run so nothing stays
   locked, and the script can `NETLOAD`, `ADSTART`, `ADRUN` and `SAVEAS`. No
   GUI, so it will not replace looking at the drawing.

## Things that have bitten

- **The drawing out of step with the record.** After a restart the session has
  no project while the drawing still holds the work - or worse, the drawing is
  empty while the record holds geometry, and submitting it would report every
  entity as deleted. `CountStamped` against the record's `EntityCount` is the
  check; `ADSTATUS` prints it and `Advance` refuses on it.
- **`ApiService.BaseUrl` points at Flask directly**, not at Vite. The dev
  server's `/api` proxy only exists while `npm run dev` is running.
- **Stale docs in this repo.** `Notes/Structure.txt` and `README.md` describe a
  plugin that no longer exists. Fix or delete them if you touch them; do not
  believe them.

## Style

Match what is there. Small static services, one job each; comments that say
*why* rather than what; no registries, factories or abstraction layers without a
requirement in hand. The server repo's `AGENTS.md` and
`DEVELOPMENT_PHILOSOPHY.md` set the tone for both halves and are worth reading
once.
