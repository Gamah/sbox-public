# Pull Request

## Summary

Directional shadows are missing in VR: an eye renders cascades so large that nothing in the scene lands in its shadow map, and depending on the frame they come out NaN instead, which makes the shadows that do show up flicker and swim.
This restores them.

## Motivation & Context

Cascades were fit from the camera's far frustum corners.
A VR eye projection has nothing finite at clip depth 0, so those corners unproject with `w = 0` and land at infinity: camera forward went NaN, and the cascade spheres came out NaN or millions of units across, leaving cascade selection on the GPU to read a garbage sphere.
A NaN radius also fails the cascade cache's reuse test, so views that should share one fit disagree about where the cascades are.

Fixes:

## Implementation Details

Camera forward now comes from the near plane's normal, which is also the axis an asymmetric projection wants.
When the far corners come back non-finite, the near corners are walked out along their own rays to the cascade distance instead: only a perspective projection can lose its far corners that way, and its corners all run from the camera at the view-space origin.
Everything downstream is unchanged, and symmetric perspective and orthographic views fit bit-identically.

## Screenshots / Videos (if applicable)

Before (master) / after (this branch), same scene in the headset:

## Checklist

- [x] Code follows existing style and conventions
- [x] No unnecessary formatting or unrelated changes
- [ ] Public APIs are documented (if applicable) — n/a, internal
- [ ] Unit tests added where applicable and all passing — `RenderView` has no hook for a custom projection, so the case isn't reachable from `Sandbox.Test.Unit`; verified on a headset
- [x] I'm okay with this PR being rejected or requested to change 🙂
