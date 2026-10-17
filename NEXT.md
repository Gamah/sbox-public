# NEXT — directional shadow cascades are fit from an infinite far plane

Handoff for a fresh session. **The diagnosis below cost a headset and several rebuilds to get; do not
re-derive it.** The fix is not written yet.

## The bug

Sun (directional/CSM) shadows break whenever a VR eye view is in the frame. Local lights are never
affected. Observed by the repo owner on a real headset, on both the Steam build and this tree:

- Shadows flash, drop out, or swim — on the **flat editor viewport as well as in the headset**.
- Happens with the headset **stationary and its display off**, with VR merely enabled.
- Happens with the sun **frozen** (the light's transform not written at all).
- Never happens with VR off. Toggling VR between runs in one process flips it, eight trials.

## Root cause

`ShadowMapper.Directional.cs`, `GetCascades` — the fit builds the camera basis by unprojecting the
eight clip-space frustum corners through `View.InverseViewProjection`:

```csharp
var corner = Vector4.Transform( Corners[i], invViewProj );
viewFrustumCorners[i] = new Vector3( corner.X, corner.Y, corner.Z ) / corner.W;
```

`Corners[4..7]` sit at reverse-Z clip `z = 0`, i.e. the camera's far plane. A VR eye projection
(`VRSystem.Rendering.cs`, `CreateProjection`, built from OpenXR's four per-eye FOV tangents) has no
usable finite far plane, so **`corner.W` is zero for those corners** and every far corner lands at
infinity. The whole fit then runs on infinities:

1. `camForward = fwd / fwd.Length` → `inf/inf` = NaN, or a garbage direction when `W` is merely tiny.
2. `cameraNearDepth` becomes noise, and **goes negative**.
3. A negative `nearD` pushes `Atan2` past 90°, so the computed `fov` exceeds **180°**.
4. `fov > 180` makes `Clamp( 1 - fov / 180, 0, 1 )` clamp to zero, so `CalculateFarPlaneScale`
   degenerates to `1 / diagonalRatio` — a multiplier of up to **61×** on `FarClip`.
5. `SphereRadius` comes out NaN, or in the millions.

Measured, one frame, same scene, VR on (an 800-unit room):

| | flat view | VR eye |
|---|---|---|
| smallest `\|corner.W\|` | 0.0001 | **0** |
| `cameraFarDepth` | 9998 | **2.7e7 … 3.2e12** |
| computed `fov` | 53.9° | **107° … 201°** |
| `farPlaneScale` | 0.567 | **up to 61** |
| `FarClip` after scale | 8509 | **up to 917,313** |
| outer cascade `SphereRadius` | 10,295 | **up to 1,791,154, or NaN** |

Two consequences, both visible:

- **`SphereRadius` feeds the GPU cascade-selection sphere**, so selection is garbage → the broken
  shadows.
- **`SphereRadius` also feeds `LastCascades.RadiusSquared`**, and the cascade cache's reuse test is
  `cameraPosition.DistanceSquared( LastCascades.CameraPosition ) <= LastCascades.RadiusSquared`.
  Any comparison against NaN is false, so when eye 0 publishes NaN, **eye 1 refits and the two eyes
  render the same frame with different cascades**. `LastCascades` is a single `static` slot shared by
  every mapper instance (one exists per light binner), so the flat view then borrows whichever eye
  published last — which is why a non-VR viewport is affected at all. The slot's own comment calls
  this out: *"single slot, add a per-light dictionary if two worlds with suns interleave views in one
  frame"*.

## The fix to write

**Make the fit independent of the camera's far plane.** Cascades only ever cover out to
`CascadeDistance` (`r.shadows.csm.distance`, 500–50,000), so unprojecting the camera's actual far
plane is both unnecessary and unsafe:

- Derive `camForward` from the view matrix rather than from `farCenter - nearCenter`.
- Derive the far depth from `CascadeDistance`, not from unprojected far corners.
- Keep the near corners (clip `z = 1`) — their `W` is fine.

That fixes every camera with an infinite or absurd far plane, not just VR eyes. A clamp on
`farPlaneScale`, or a NaN guard on `SphereRadius`, treats the symptom; **don't submit that as the
fix.** Either may be worth keeping as a belt-and-braces second commit if the reviewer wants one.

A unit test fits here and `CONTRIBUTING.md` asks for one: `GetCascades` is static and takes an
`invViewProj` matrix, so an infinite-far projection can be fed in directly and the resulting
`SphereRadius` asserted finite and sane. Check whether `Sandbox.Test.Unit` can reach it before
promising it; it is a private static on an internal partial class.

## Before you write it

**Rebase onto `upstream/master` and branch from there.** This fork's `master` carries three
instrumentation commits on top of `bec8c040` that must never reach a PR:

| commit | what |
|---|---|
| `eb84196d` | `ShadowMapper.DebugLog.cs`, the `r.shadows.csm.log` convar, the `LogDirectional()` hook |
| `2a0cb0f2` | splits the cascade-cache gate into named booleans so the log can say which failed |
| `8df9b970` | logs every division in the fit that can go non-finite |

They are how the diagnosis was obtained and they are worth keeping on this fork while the fix is
being tested. The PR branch starts clean:

```bash
git fetch upstream
git checkout -b fix/csm-infinite-far-plane upstream/master
```

## Rules for this repo

- **Never touch `Facepunch/sbox-public`.** No issue, no comment, no PR, no fork sync. Every `gh`
  call carries `--repo Gamah/sbox-public`. The owner opens anything upstream themselves.
- Build, test and format through `SboxBuild`, per `AGENTS.md` — PRs run the same three steps:
  ```bash
  dotnet run --project engine/Tools/SboxBuild/SboxBuild.csproj -- build --config Developer
  dotnet run --project engine/Tools/SboxBuild/SboxBuild.csproj -- test --filter "TestCategory!=LiveBackend"
  dotnet run --project engine/Tools/SboxBuild/SboxBuild.csproj -- format
  ```
- **Upstream `master` is flaky.** Verify your own change; an unrelated failing test is not yours.
- A build fails with "being used by another process" while the editor is open. `sbox-launcher`
  outlives the editor window: `Stop-Process -Name sbox-launcher,sbox-dev -Force`.
- Keep `NEXT.md` out of the PR branch. It belongs to this fork.

## Verifying the fix needs the owner

No engine code compiles or runs on the dev host, and the repro needs a headset. The owner builds
with `Setup.bat` and launches `game\sbox-dev.exe -vr` (the titlebar VR toggle only exists when
`-vr` is on the command line — `VRSystem.Init` gates `HasHeadset` on it). With the instrumentation
commits still applied, `r.shadows.csm.log 1` prints a line per frame whose shape changes; a fixed
fit shows finite `w`, a `fov` under 180°, a `scale` near 1, and a stable `r` in the thousands, with
no `NaN` and no `OWN[...]` on eye 1.
