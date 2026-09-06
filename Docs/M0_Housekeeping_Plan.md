# M0 — Housekeeping: Exact Implementation Steps

**Audience:** a coding agent working in `G:\UnityProjects\TerrainGen` with no prior context on this project.
**Parent plan:** `SonomaRevisedPlan.md` (repo root), milestone M0.
**Prerequisite:** commit `a2b1332` is `HEAD` and the working tree is clean. Verify with `git log --oneline -1` and `git status --short` before starting.

M0 is preparation only. It adds no features and changes no terrain behaviour. Its job is to make the package dependencies explicit, split the code into assemblies so Burst work in M1/M2 compiles in isolation, and delete two dead scripts. Task 1 of the original M0 list (committing the prototype) is already done in `a2b1332`; the four tasks below are what remains.

---

## Ground rules

1. **Do not modify terrain logic.** No edits to `QuadtreeManager.cs`, `CoordinateTransform.cs`, `HeightmapGenerator.cs`, `WorldOriginSystem.cs`, `TerrainChunk.cs`, or the shader, except the single comment fix in Task 3.
2. **Never delete or hand-write a `.meta` file for an asset you are keeping.** Unity uses the GUID inside it to resolve references. Deleting a `.cs` file means deleting its `.cs.meta` too, in the same commit.
3. **New `.asmdef` files do not need hand-written `.meta` files.** Unity generates them on next import. Do not fabricate GUIDs.
4. **The agent probably cannot run the Unity Editor.** Every task below has a text-level verification that works without it. Task 5 lists what a human must confirm in the Editor afterwards.
5. Work on a branch: `git switch -c m0-housekeeping`.

---

## Task 1 — Promote Burst, Collections and Mathematics to direct dependencies

**Why:** all three are currently present only as transitive dependencies (depth 2 in `Packages/packages-lock.json`), pulled in by URP and the Input System. M2 requires them directly. If an unrelated package is ever removed, a transitive dependency can vanish and break the build. Declaring them directly also pins the versions.

**Current state, confirmed:**

| Package | Resolved version | Currently in `manifest.json`? |
|---|---|---|
| `com.unity.burst` | 1.8.29 | no |
| `com.unity.collections` | 6.4.0 | no |
| `com.unity.mathematics` | 1.3.3 | no |

**Edit `Packages/manifest.json`.** The `dependencies` object is alphabetically ordered and starts with `com.unity.ai.navigation`. Insert the three entries so ordering is preserved. After `"com.unity.ai.navigation": "2.0.12",` the block should read:

```json
    "com.unity.ai.navigation": "2.0.12",
    "com.unity.burst": "1.8.29",
    "com.unity.collab-proxy": "2.12.4",
    "com.unity.collections": "6.4.0",
    "com.unity.ide.rider": "3.0.39",
    "com.unity.ide.visualstudio": "2.0.27",
    "com.unity.inputsystem": "1.19.0",
    "com.unity.mathematics": "1.3.3",
    "com.unity.multiplayer.center": "1.0.1",
```

Note the three insertions are interleaved with existing lines. Do not reorder or alter any other entry.

**Do not edit `Packages/packages-lock.json` by hand.** Unity rewrites it on next import, changing the three `"depth": 2` values to `0` and `"source": "builtin"` on Collections as appropriate. That rewrite is expected and should be committed when it appears.

**Verify:**

```bash
python -c "import json; d=json.load(open('Packages/manifest.json'))['dependencies']; print({k:d[k] for k in ('com.unity.burst','com.unity.collections','com.unity.mathematics')})"
```

Expect all three keys present with the versions above, and the file still parsing as valid JSON.

---

## Task 2 — Add three assembly definitions

**Why:** everything currently compiles into the monolithic `Assembly-CSharp`. That makes Burst compilation slower to iterate on, prevents an EditMode test assembly from existing at all, and gives no compile-time guarantee that runtime code stays free of `UnityEditor` references. M1 adds tests; they need an assembly to live in.

**Namespace facts, confirmed:** runtime code uses `Sonoma.Core.CoordinateSpace`, `Sonoma.Core.Generation`, `Sonoma.Core.Quadtree`, `Sonoma.Core.Rendering`, and `Sonoma.Systems.Configuration`. The `Assets/Scripts/Tools/` scripts are in the **global namespace** and `TopologyFlyCamera.cs` uses `UnityEngine.InputSystem` plus `Sonoma.Core.*`. `Assets/Editor/TerrainSettingsCreator.cs` is global-namespace and references `Sonoma.Systems.Configuration`.

Because `TopologyFlyCamera` lives under `Assets/Scripts/Tools/`, a single asmdef at `Assets/Scripts/` covers it, and that asmdef must reference the Input System.

`Assets/TutorialInfo/` has its own editor scripts and is **outside** `Assets/Scripts/`, so it stays in `Assembly-CSharp` and is unaffected. Leave it alone.

### 2a. Create `Assets/Scripts/Sonoma.Core.asmdef`

```json
{
    "name": "Sonoma.Core",
    "rootNamespace": "Sonoma",
    "references": [
        "Unity.Burst",
        "Unity.Collections",
        "Unity.Mathematics",
        "Unity.InputSystem"
    ],
    "includePlatforms": [],
    "excludePlatforms": [],
    "allowUnsafeCode": false,
    "overrideReferences": false,
    "precompiledReferences": [],
    "autoReferenced": true,
    "defineConstraints": [],
    "versionDefines": [],
    "noEngineReferences": false
}
```

`autoReferenced: true` keeps `Assembly-CSharp` (and therefore `Assets/TutorialInfo/`) able to see Sonoma types, so nothing outside `Assets/Scripts/` breaks.

Leave `allowUnsafeCode: false` for now. If M2 later needs `NativeArray` pointer access, flip it to `true` in that milestone, not this one.

**Empty directories:** `Assets/Scripts/Editor/`, `Assets/Scripts/Systems/Performance/` and `Assets/Scripts/Systems/Streaming/` exist but contain no `.cs` files. `Assets/Scripts/Editor/` is a magic folder name — once M1+ puts editor code there it would be compiled into an editor assembly by Unity's folder rules, but under an asmdef it would instead be swallowed into `Sonoma.Core` and fail on any `UnityEditor` using. To avoid that trap later, delete the empty `Assets/Scripts/Editor/` directory and its `.meta` now; the editor assembly lives at `Assets/Editor/` (Task 2b). Leave the two empty `Systems/` folders, which M5 and the streaming work will fill.

### 2b. Create `Assets/Editor/Sonoma.Editor.asmdef`

```json
{
    "name": "Sonoma.Editor",
    "rootNamespace": "Sonoma.Editor",
    "references": [
        "Sonoma.Core",
        "Unity.Mathematics"
    ],
    "includePlatforms": [
        "Editor"
    ],
    "excludePlatforms": [],
    "allowUnsafeCode": false,
    "overrideReferences": false,
    "precompiledReferences": [],
    "autoReferenced": true,
    "defineConstraints": [],
    "versionDefines": [],
    "noEngineReferences": false
}
```

This picks up the existing `TerrainSettingsCreator.cs`. Do not add a namespace to that file; `rootNamespace` only affects newly created scripts.

### 2c. Create the test assembly

Create directory `Assets/Tests/EditMode/` and the file `Assets/Tests/EditMode/Sonoma.Tests.EditMode.asmdef`:

```json
{
    "name": "Sonoma.Tests.EditMode",
    "rootNamespace": "Sonoma.Tests",
    "references": [
        "Sonoma.Core",
        "Unity.Mathematics",
        "Unity.Collections",
        "Unity.Burst",
        "UnityEngine.TestRunner",
        "UnityEditor.TestRunner"
    ],
    "includePlatforms": [
        "Editor"
    ],
    "excludePlatforms": [],
    "allowUnsafeCode": false,
    "overrideReferences": true,
    "precompiledReferences": [
        "nunit.framework.dll"
    ],
    "autoReferenced": false,
    "defineConstraints": [
        "UNITY_INCLUDE_TESTS"
    ],
    "versionDefines": [],
    "noEngineReferences": false
}
```

`overrideReferences: true` with `precompiledReferences: ["nunit.framework.dll"]` and the `UNITY_INCLUDE_TESTS` constraint are all required for the Test Runner to pick the assembly up. Omitting any one of them produces an assembly that compiles but shows no tests.

Add one placeholder test so M1 has somewhere to write and so the wiring is provably correct. Create `Assets/Tests/EditMode/SmokeTests.cs`:

```csharp
using NUnit.Framework;
using Sonoma.Core.CoordinateSpace;

namespace Sonoma.Tests
{
    public class SmokeTests
    {
        // Placeholder proving the test assembly compiles and can see Sonoma.Core.
        // M1 replaces this file with the surface adjacency and round-trip tests.
        [Test]
        public void PlaneQuadHasPlaneSurfaceType()
        {
            var quad = BaseMeshFactory.CreatePlaneQuad(1000f);
            Assert.AreEqual(SurfaceType.Plane, quad.Type);
        }
    }
}
```

**Verify Task 2:**

```bash
find Assets -name "*.asmdef" | sort
```

Expect exactly three paths: `Assets/Editor/Sonoma.Editor.asmdef`, `Assets/Scripts/Sonoma.Core.asmdef`, `Assets/Tests/EditMode/Sonoma.Tests.EditMode.asmdef`. Confirm each parses as JSON.

---

## Task 3 — Delete the two dead scripts

### 3a. `FlyCamera` — safe, no references

`Assets/Scripts/Tools/FlyCamera.cs` (GUID `c8b9c5446ceaa6a4ea44a2aef4a1f0ab`) is the legacy-`Input` camera. It was superseded by `TopologyFlyCamera` and **its GUID no longer appears in `SampleScene.unity`**. It is also the last violator of the project's new-Input-System rule.

Delete both files:

```bash
git rm "Assets/Scripts/Tools/FlyCamera.cs" "Assets/Scripts/Tools/FlyCamera.cs.meta"
```

### 3b. `DemoTerrainSpawner` — still referenced by the scene

`Assets/Scripts/Tools/DemoTerrainSpawner.cs` (GUID `c496cd82e84ae3148bb3b4a6e0475bdd`) duplicates `QuadtreeManager.BuildMesh`, which M2 replaces with a Burst job.

**Important:** unlike `FlyCamera`, this one is still wired into the scene. `SampleScene.unity` has a GameObject named `TerrainSpawner` carrying the component. It is already disabled (`m_IsActive: 0` on the GameObject, `m_Enabled: 0` on the component), so it does nothing at runtime, but deleting the script without removing the GameObject leaves a permanent "missing script" warning on scene load.

Delete the script:

```bash
git rm "Assets/Scripts/Tools/DemoTerrainSpawner.cs" "Assets/Scripts/Tools/DemoTerrainSpawner.cs.meta"
```

Then remove the GameObject. **Preferred method:** open `Assets/Scenes/SampleScene.unity` in the Unity Editor, delete the `TerrainSpawner` object from the Hierarchy, and save. Let Unity rewrite the YAML.

**Fallback if the Editor is unavailable,** editing the YAML directly. Verify the line numbers still match before cutting, since any earlier scene edit shifts them:

- Delete lines **459 through 506 inclusive**. This is three contiguous YAML documents: `--- !u!1 &1612700490` (the GameObject, starting line 459), `--- !u!114 &1612700491` (the DemoTerrainSpawner component, line 476), and `--- !u!4 &1612700492` (the Transform, line 492). The block ends immediately before `--- !u!1 &1981064309` at line 507.
- Delete the line `  - {fileID: 1612700492}` from the `m_Roots` list in the `SceneRoots` document near line 617.

Confirm the boundaries first:

```bash
sed -n '459p;476p;492p;506,507p' Assets/Scenes/SampleScene.unity
```

Expect `--- !u!1 &1612700490`, `--- !u!114 &1612700491`, `--- !u!4 &1612700492`, a `m_LocalEulerAnglesHint` line, then `--- !u!1 &1981064309`.

### 3c. Fix the stale comment left behind

`Assets/Scripts/Core/CoordinateSpace/BaseMeshFactory.cs` line 10 reads:

```csharp
        // Single quad on XZ plane centered at origin. Kept for DemoTerrainSpawner.
```

`CreatePlaneQuad` is **still used** by `CreatePlane`, which `QuadtreeManager` calls, so keep the method and change only the comment:

```csharp
        // Single quad on XZ plane centered at origin. Used by CreatePlane.
```

**Verify Task 3:**

```bash
grep -rn "FlyCamera\|DemoTerrainSpawner" --include="*.cs" Assets/
grep -n "c8b9c5446ceaa6a4ea44a2aef4a1f0ab\|c496cd82e84ae3148bb3b4a6e0475bdd" Assets/Scenes/SampleScene.unity
```

The first must return only the `TopologyFlyCamera.cs` class declaration line. The second must return nothing. Also confirm `grep -c "fileID: 1612700492" Assets/Scenes/SampleScene.unity` returns 0.

---

## Task 4 — Correct the stale facts in `CLAUDE.md`

Three statements in `CLAUDE.md` are now wrong. Fix exactly these, and nothing else:

1. **Unity version.** It says `Unity 6 (6000.3.5f2)` in the Project Overview and `Unity Editor Version: 6000.3.5f2` under Opening the Project. `ProjectSettings/ProjectVersion.txt` says **`6000.4.4f1`**. Update both mentions.

2. **Phase status.** The Project Overview claims Phase 1 is "substantially complete" with Phase 2 next, while the roadmap marks Phase 2 complete and Phase 3 current. The review in `SonomaRevisedPlan.md` found Phase 2 is not complete: generation is synchronous and single-threaded, and seam handling relies on skirts rather than the stitching it claims. Replace the **Current Status** paragraph with:

   > **Current Status:** A working synchronous prototype. Original Phase 1 is complete; Phase 2 is only partly done (seam stitching is order-dependent and cracks are covered by skirts; generation is neither threaded nor budgeted). A design review on 2026-09-05 produced `SonomaRevisedPlan.md`, which supersedes the 5-phase roadmap below with milestones M0–M7. Work against that plan.

3. **Tools inventory.** The Code Organization block lists `Tools/  # DemoTerrainSpawner, FlyCamera`. Both are gone. Change it to `Tools/  # TopologyFlyCamera`. Also update the Non-Obvious Implementation Details bullets that name `DemoTerrainSpawner` (the duplicate-`BuildMesh` note) and `FlyCamera` (the legacy-`Input` note) — delete the `FlyCamera` bullet entirely, and reword the `BuildMesh` bullet to drop the reference to the now-deleted duplicate.

Add a short note under Testing that an EditMode test assembly now exists at `Assets/Tests/EditMode/` and is run from Window → General → Test Runner.

Do **not** rewrite the architecture sections. The revised plan supersedes them, and `CLAUDE.md` already links to it.

---

## Task 5 — Verification

### Text-level checks the agent runs

```bash
# All three asmdefs exist and parse
find Assets -name "*.asmdef" | sort
python -c "import json,glob; [json.load(open(p)) for p in glob.glob('Assets/**/*.asmdef', recursive=True)]; print('asmdefs OK')"

# manifest.json parses and has the three packages
python -c "import json; json.load(open('Packages/manifest.json')); print('manifest OK')"

# Dead scripts gone, no dangling scene references
test ! -f Assets/Scripts/Tools/FlyCamera.cs && test ! -f Assets/Scripts/Tools/DemoTerrainSpawner.cs && echo "scripts removed"
grep -c "c496cd82e84ae3148bb3b4a6e0475bdd" Assets/Scenes/SampleScene.unity   # expect 0

# Every remaining .cs under Assets/Scripts has a .meta beside it
for f in $(find Assets/Scripts -name "*.cs"); do test -f "$f.meta" || echo "MISSING META: $f"; done
```

### Optional compile check without opening the Editor

If the Unity 6000.4.4f1 executable is available and licensed, this compiles the project and exits:

```bash
"C:/Program Files/Unity/Hub/Editor/6000.4.4f1/Editor/Unity.exe" -batchmode -quit -nographics -projectPath "G:/UnityProjects/TerrainGen" -logFile - 
```

Treat any line containing `error CS` as a failure. Do not attempt this if it would prompt for a licence.

### Checks that require a human in the Editor

State plainly in the handoff that these are outstanding:

1. Open the project. The Console must be free of compile errors, and of "The referenced script on this Behaviour is missing" warnings on `SampleScene`.
2. Window → General → Test Runner → EditMode shows `Sonoma.Tests.EditMode` with `PlaneQuadHasPlaneSurfaceType`, and it passes.
3. Press Play on `SampleScene`. Terrain still generates on the cylinder/sphere topology and `TopologyFlyCamera` still drives, exactly as before M0.
4. `Packages/packages-lock.json` will have been rewritten on first import. Commit that change.

---

## Task 6 — Commit

Two commits keep the mechanical package/assembly work separate from the deletions:

```bash
git add Packages/manifest.json Assets/Scripts/Sonoma.Core.asmdef Assets/Editor/Sonoma.Editor.asmdef Assets/Tests
git commit -m "M0: declare Burst/Collections/Mathematics, split into assemblies"
```

```bash
git add -A
git commit -m "M0: remove FlyCamera and DemoTerrainSpawner, refresh CLAUDE.md"
```

Commit message bodies should say what and why in a line or two each. End every commit message with:

```
Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
```

After the human confirms the Editor checks and `packages-lock.json` is regenerated, commit that file too, then merge `m0-housekeeping` into `main`.

---

## Definition of done

- [ ] `com.unity.burst` 1.8.29, `com.unity.collections` 6.4.0, `com.unity.mathematics` 1.3.3 are direct entries in `Packages/manifest.json`.
- [ ] Three asmdefs exist: `Sonoma.Core`, `Sonoma.Editor`, `Sonoma.Tests.EditMode`. The empty `Assets/Scripts/Editor/` folder is gone.
- [ ] `FlyCamera.cs`, `DemoTerrainSpawner.cs` and their `.meta` files are deleted, and the `TerrainSpawner` GameObject is removed from `SampleScene.unity` with no dangling `fileID` in `m_Roots`.
- [ ] The `BaseMeshFactory` comment no longer names `DemoTerrainSpawner`.
- [ ] `CLAUDE.md` states Unity 6000.4.4f1, the corrected phase status, and the current `Tools/` contents.
- [ ] A human has confirmed: no compile errors, no missing-script warnings, the placeholder EditMode test passes, and `SampleScene` plays as it did before.

## Out of scope for M0

Do not start any of these; they are M1 and later:

- Cube-sphere topology, `NodeId` integer addressing, or edge adjacency tables.
- Any change to noise, precision, stitching, skirts, or the LOD metric.
- Burst jobs, `NativeArray` conversion, or touching `QuadtreeManager.BuildMesh`.
- Deleting `Docs/Phase1_Step1_Checklist.md`. It is superseded but harmless, and removing it is bundled with the M1 documentation pass.
