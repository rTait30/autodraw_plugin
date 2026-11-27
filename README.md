# AutoDraw plugin

The AutoCAD side of autodraw_server. It runs the server's **operations** (one
job each - draw a sail's structure, mark seams, nest panels) and **recipes**
(operations in order) on a drawing, and lays the result back into AutoCAD. The
drawing logic is all on the server; this only sends, asks and draws.

## Setup

1. AutoCAD 2025 installed (the references come from its folder).
2. `dotnet build`, then `NETLOAD` the DLL from `bin\Debug\net8.0`.
3. `ADLOGIN`. The server is `http://localhost:5001/api` unless changed.

## The idea: one job open, or none

Everything acts on the job open in this AutoCAD (`ADOPEN`). There are three
ways to work, and `ADSTATUS` always says which one you are in:

| Mode | When | What a run does |
| --- | --- | --- |
| **No job** | A drawing with no job behind it | Works on what you select; asks for everything; keeps nothing. |
| **Attached** | A job drawn mostly by hand | Works on what you select; the job answers what it can (fabric, quantities) and a line goes in its log. Your drawing is never replaced. |
| **Loaded** | A job the server draws | The job's own drawing is laid in; every run is kept on its history, so you can step back and forward, and pick up where it stopped another day. |

Every command ends with one line saying where things stand and what to do
next, e.g. `Job 124 at draw_work_points@1. shadesail waits at step 3: ADGO to go on.`

## Commands

| Command | What it does |
| --- | --- |
| `ADLOGIN` / `ADLOGOUT` / `ADWHOAMI` | Log in and out. |
| `ADOPEN` | Open a job by number - **Load** its drawing, or **Attach** it to yours. Enter closes it. |
| `ADSTATUS` | Who you are, the job, what is waiting, the options in force. |
| `ADRUN` | Run an operation or recipe: by number, by name, or several names separated by commas. **Again** (or Enter) repeats the last run with the same answers; **Part** runs a recipe from a chosen step. |
| `ADGO` | Carry on with a recipe that stopped for you. |
| `ADBACK` / `ADFORWARD` | Step along a loaded job's history, an operation at a time. |
| `ADNOTE` | Write a note in the job's log, for the job or one item. |
| `ADLOG` | Read the job's log: what each operation did and said, and people's notes. |
| `ADRECIPE` | Make or change a recipe by typing its steps; **Save** makes one from a loaded job's last runs. |
| `ADOPTIONS` | **Pause** Each/Reviews, **Layout** Replace/Stack/Grid, **Direction** Right/Down, **Depth**. |

### Options (`ADOPTIONS`)

- **Pause** - `Reviews` (default): a recipe stops only at a step marked for a
  person to check. `Each`: it stops after every operation, to step through it.
- **Layout** - `Replace`: the result takes the place of what was sent.
  `Stack`: it is drawn beside it, and a loaded job's history is drawn beside
  its drawing, one picture per operation. `Grid`: the same, with each item in
  a lane of its own. The pictures stay on the layers they were drawn on (turn
  a layer off and it goes in the pictures too) and are never sent back.
- **Direction** - which way stacked states and the grid run.
- **Depth** - in Stack, how many of a free run's in-between states to draw,
  as pictures between what you selected and the result.

## Examples

**A one-off operation on a hand-drawn job**

```
ADOPEN          124            → Attach
ADRUN           seam_marks     → select the panels
ADRUN           <Enter>        → same again on another selection, same answers
ADLOG                          → "[ADRUN] seam_marks on 14 entities: ..."
```

**A small recipe typed in, with an answer given up front**

```
ADRUN   label_panels(height=150), seam_marks
```

**A shade sail, one operation at a time, finished another day**

```
ADOPTIONS  Pause → Each
ADOPEN     124   → Load          (saving the job already drew it up to MPanel:
                                  "shadesail stopped after step 2 of 24: Relax the mesh ...")
  ...model and flatten the sails in MPanel...
ADGO                             → runs sail_shape, stops after it
ADGO                             → next operation ... close AutoCAD whenever
ADOPEN     124   → Load          → it says where it waits; ADGO carries on
```

**Going back and noting why**

```
ADBACK
ADNOTE   "Pocket too tight on sail 2, redo after the hardware change"   → item 195
```

**Running part of a recipe on a selection**

```
ADRUN   Part → shadesail → start at step 14 → select the sails
```

## The older step automation

The older automation that walks a product's fixed steps still works, under the
`ADSTEP` prefix: `ADSTEPSTART`, `ADSTEPSTATUS`, `ADSTEPCONTINUE`, `ADSTEPRUN`,
`ADSTEPBACK`, `ADSTEPFORWARD`, `ADSTEPBRANCH`, `ADSTEPNOTE`, `ADSTEPRESET`,
`ADSTEPSTOP`, `ADSTEPSETTINGS`. It is being retired in favour of operations
and recipes; use it only for jobs already started that way.

## For developers

See `AGENTS.md` for the code map, the contract with the server and the things
that have bitten.
