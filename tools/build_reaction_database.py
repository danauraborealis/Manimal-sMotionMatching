"""Build the local, upper-hit-ready Alyx reaction database.

This is a development asset builder. It preserves the four existing directional
stumbles, then appends five selected non-additive hit-reaction clips with only
upper-body overlay tracks exposed to the runtime. Valve-derived data stays in
tmp/ and is never copied into the mod package.
"""
import argparse
import copy
import hashlib
import json
import math
from pathlib import Path

from alyx_retarget import EftSkeleton, GltfSource, retarget_clip
from export_reaction_upper_body import augment as add_upper_body_tracks


ROOT = Path(__file__).resolve().parents[1]
DEFAULT_SOURCE = ROOT / "tmp/alyx/combine_grunt.glb"
DEFAULT_SKELETON = ROOT / "tmp/alyx/eft_skeleton_bundle.json"
DEFAULT_BASE = ROOT / "tmp/alyx/reaction_upper_posedb.json"
DEFAULT_SELECTION = ROOT / "artifacts/alyx-curated/selection.json"
DEFAULT_OUTPUT = ROOT / "tmp/alyx/reaction_upper_posedb_expanded.json"

EXISTING_STUMBLES = ("stumble_n", "stumble_s", "stumble_e", "stumble_w")
CURATED_BY_ROLE = {
    "light": ("new_flinch_02_hitreact", "new_flinch_35_hitreact"),
    "heavy": ("new_flinch_06_hitreact", "new_flinch_20_hitreact", "new_flinch_11_hitreact"),
}
UPPER_BONES = (
    "Base HumanSpine1", "Base HumanSpine2", "Base HumanSpine3", "Base HumanRibcage",
    "Base HumanLCollarbone", "Base HumanLUpperarm", "Base HumanLForearm1",
    "Base HumanRCollarbone", "Base HumanRUpperarm", "Base HumanRForearm1",
)
EXPECTED_BONES = (
    "Base HumanPelvis", "Base HumanLThigh1", "Base HumanLCalf", "Base HumanLFoot",
    "Base HumanRThigh1", "Base HumanRCalf", "Base HumanRFoot",
)


def _relative(path):
    try:
        return str(Path(path).resolve().relative_to(ROOT))
    except ValueError:
        return str(Path(path).resolve())


def _assert_local_output(path):
    tmp = (ROOT / "tmp").resolve()
    resolved = Path(path).resolve()
    try:
        resolved.relative_to(tmp)
    except ValueError as exc:
        raise ValueError("Reaction databases and manifests must stay under the local tmp/ directory") from exc


def _selection_roles(path):
    selection = json.loads(Path(path).read_text(encoding="utf-8-sig"))
    if not isinstance(selection, list):
        raise ValueError("Curated selection must be a list")
    wanted = {name for names in CURATED_BY_ROLE.values() for name in names}
    found = {"light": [], "heavy": []}
    for section in selection:
        role = {"mild": "light", "strong": "heavy"}.get(section.get("id"))
        if role is None:
            continue
        for clip in section.get("clips", []):
            name = clip.get("name")
            if name in wanted:
                found[role].append(name)
    for role, expected in CURATED_BY_ROLE.items():
        if set(found[role]) != set(expected) or len(found[role]) != len(expected):
            raise ValueError(
                f"Curated shortlist changed for {role}: expected {list(expected)}, got {found[role]}"
            )
    return [(name, role) for role, names in CURATED_BY_ROLE.items() for name in names]


def _has_additive_declaration(value):
    """Look for an explicit additive marker if the exporter preserved one in extras."""
    if isinstance(value, dict):
        for key, child in value.items():
            if "additive" in str(key).lower() and child not in (False, 0, None, ""):
                return True
            if _has_additive_declaration(child):
                return True
    elif isinstance(value, list):
        return any(_has_additive_declaration(child) for child in value)
    return False


def _validate_source_clip(source, name):
    if "additive" in name.lower() or name.lower().endswith("_add"):
        raise ValueError(f"Refusing additive source clip: {name}")
    if name not in source.animations:
        raise ValueError(f"Source GLB does not contain {name}")
    animation = source.animations[name]
    if _has_additive_declaration(animation):
        raise ValueError(f"Source GLB marks {name} as additive")
    for sampler in animation.get("samplers", []):
        if sampler.get("interpolation", "LINEAR") != "LINEAR":
            raise ValueError(f"Unsupported {sampler.get('interpolation')} interpolation in {name}")
    tracks, duration = source.tracks(name)
    if not math.isfinite(duration) or duration <= 0:
        raise ValueError(f"Invalid duration for {name}: {duration}")
    required_nodes = ("root_motion", "pelvis", "spine_0", "spine_3", "arm_upper_L", "arm_upper_R")
    for node_name in required_nodes:
        node = source.index.get(node_name)
        if node is None or (node, "rotation") not in tracks:
            raise ValueError(f"{name} lacks an absolute local rotation channel for {node_name}")
    if len(tracks) < 20:
        raise ValueError(f"Unexpectedly sparse source tracks for {name}: {len(tracks)}")
    return tracks, duration, animation


def _new_overlay_clip(source, target, name, role, bones):
    _, duration, animation = _validate_source_clip(source, name)
    retargeted = retarget_clip(source, target, name)
    frames = retargeted["frames"]
    fps = retargeted["fps"]
    if abs((frames - 1) / fps - duration) > 1 / fps:
        raise ValueError(f"Retarget sample interval does not cover {name}: {frames} frames at {fps} fps vs {duration}s")
    if tuple(retargeted["rotations"].keys()) != tuple(bones):
        raise ValueError(f"Retargeted bone order differs for {name}")
    clip = {
        "name": name,
        "fps": fps,
        "frames": frames,
        "loop": False,
        "speedMetersPerSecond": 0.0,
        "roles": ["hitreact", role],
        # The legs are present only because the existing pose-database loader requires
        # the common bone layout. Runtime upper-hit selection consumes upperBodyRotations
        # and does not receive any stride/contact/foot/root-speed data from these clips.
        "rotations": {bone: retargeted["rotations"][bone] for bone in bones},
        "pelvisPosition": retargeted["pelvisPosition"],
    }
    for bone in bones:
        if len(clip["rotations"][bone]) != frames:
            raise ValueError(f"{name} has an incomplete lower-pose reference track for {bone}")
    if len(clip["pelvisPosition"]) != frames:
        raise ValueError(f"{name} has an incomplete pelvis reference track")
    return clip, {
        "name": name,
        "roles": clip["roles"],
        "frames": frames,
        "fps": fps,
        "durationSeconds": round(duration, 6),
        "sourceChannels": len(animation.get("channels", [])),
        "interpolation": "LINEAR",
        "sourceTrackMode": "absolute local transforms (non-additive by selected sequence convention)",
        "additive": False,
    }


def _validate_database(database, original_clips, added_names):
    if database.get("schema") != "manimal.motionmatching.posedb.v1":
        raise ValueError(f"Unexpected database schema: {database.get('schema')}")
    if tuple(database.get("bones", [])) != EXPECTED_BONES:
        raise ValueError("Reaction bone layout changed; update the builder deliberately")
    if len(database.get("clips", [])) != len(original_clips) + len(added_names):
        raise ValueError("Unexpected clip count after expansion")
    if database["clips"][:len(original_clips)] != original_clips:
        raise ValueError("Existing reaction clips changed while building the expanded database")
    seen = set()
    for clip in database["clips"]:
        name = clip.get("name")
        if name in seen:
            raise ValueError(f"Duplicate clip name: {name}")
        seen.add(name)
        frames = clip.get("frames")
        if not isinstance(frames, int) or frames < 2:
            raise ValueError(f"Invalid frame count for {name}")
        if clip.get("upperBodyRotations") is None:
            raise ValueError(f"Missing upper-body tracks for {name}")
        for bone in UPPER_BONES:
            track = clip["upperBodyRotations"].get(bone)
            if track is None or len(track) != frames:
                raise ValueError(f"Missing or incomplete upper-body track: {name}/{bone}")
            for q in track:
                if len(q) != 4 or any(not math.isfinite(float(c)) for c in q):
                    raise ValueError(f"Invalid upper-body quaternion: {name}/{bone}")
                norm = sum(float(c) * float(c) for c in q)
                if abs(norm - 1.0) > 0.01:
                    raise ValueError(f"Unnormalized upper-body quaternion: {name}/{bone} ({norm})")
    by_name = {c["name"]: c for c in database["clips"]}
    for name in added_names:
        clip = by_name[name]
        if "hitreact" not in clip.get("roles", []):
            raise ValueError(f"Added clip lacks the hitreact role: {name}")
        if any(key in clip for key in ("stride", "contacts", "feet", "rootSpeed", "rootVelocity")):
            raise ValueError(f"Upper-hit clip unexpectedly carries locomotion data: {name}")
        if clip.get("loop") is not False or clip.get("speedMetersPerSecond") != 0.0:
            raise ValueError(f"Upper-hit clip must be a non-looping overlay-only asset: {name}")
    return by_name


def build(base_path, source_path, skeleton_path, selection_path, output_path, manifest_path):
    for path in (output_path, manifest_path):
        _assert_local_output(path)
    outputs = {Path(output_path).resolve(), Path(manifest_path).resolve()}
    if len(outputs) != 2:
        raise ValueError("Database and manifest outputs must be different files")
    inputs = {Path(p).resolve() for p in (base_path, source_path, skeleton_path, selection_path)}
    if outputs & inputs:
        raise ValueError("Database and manifest outputs must not overwrite any build input")
    base = json.loads(Path(base_path).read_text(encoding="utf-8-sig"))
    if base.get("schema") != "manimal.motionmatching.posedb.v1":
        raise ValueError("Base database has an unsupported schema")
    if tuple(base.get("bones", [])) != EXPECTED_BONES:
        raise ValueError("Base database uses an unexpected reaction bone layout")
    names = [clip.get("name") for clip in base.get("clips", [])]
    if len(names) != len(set(names)):
        raise ValueError("Base database contains duplicate clip names")
    missing_stumbles = set(EXISTING_STUMBLES) - set(names)
    if missing_stumbles:
        raise ValueError(f"Base database is missing current stumbles: {sorted(missing_stumbles)}")
    original_clips = copy.deepcopy(base["clips"])
    selected = _selection_roles(selection_path)
    duplicates = set(names) & {name for name, _ in selected}
    if duplicates:
        raise ValueError(f"Selected hit clips already exist in base database: {sorted(duplicates)}")

    source = GltfSource(Path(source_path))
    target = EftSkeleton(Path(skeleton_path))
    database = copy.deepcopy(base)
    source_records = []
    for name, role in selected:
        clip, record = _new_overlay_clip(source, target, name, role, database["bones"])
        database["clips"].append(clip)
        source_records.append(record)

    # Only the new hitreact-role clips are sampled here. Existing four stumble tracks
    # are retained exactly as authored in reaction_upper_posedb.json.
    add_upper_body_tracks(database, source, target, role="hitreact")
    added_names = [name for name, _ in selected]
    clip_map = _validate_database(database, original_clips, added_names)
    for name in added_names:
        # The single upper-body exporter also emits all ten tracks required by runtime.
        for bone in UPPER_BONES:
            track = clip_map[name]["upperBodyRotations"][bone]
            if len(track) != clip_map[name]["frames"]:
                raise ValueError(f"Upper-body export produced the wrong frame count: {name}/{bone}")

    output_path = Path(output_path)
    manifest_path = Path(manifest_path)
    output_path.parent.mkdir(parents=True, exist_ok=True)
    manifest_path.parent.mkdir(parents=True, exist_ok=True)
    encoded = json.dumps(database, separators=(",", ":"), ensure_ascii=False).encode("utf-8")
    output_path.write_bytes(encoded)
    manifest = {
        "schema": "manimal.motionmatching.reaction-build.v1",
        "database": _relative(output_path),
        "databaseSha256": hashlib.sha256(encoded).hexdigest(),
        "baseDatabase": _relative(base_path),
        "sourceGlb": _relative(source_path),
        "targetSkeleton": _relative(skeleton_path),
        "selection": _relative(selection_path),
        "scope": "local upper-hit reaction asset; no runtime source, installation, or package changes",
        "baseClipsPreserved": list(names),
        "addedClips": source_records,
        "upperBodyBones": list(UPPER_BONES),
        "lowerPoseUse": "Retargeted leg channels remain only to satisfy the shared PoseClip schema; the pelvis track is the chest-reference input. Added clips contain no stride, contacts, feet, root speed, or root velocity, so the full-body stumble matcher cannot select them.",
        "assumptions": [
            "The five selected GLB sequence names are not additive and carry absolute local joint transforms; the GLB exporter does not provide an explicit additive boolean for these clips.",
            "The source viewer treats only explicitly additive-named clips as deltas; these five selected names are rendered as absolute source poses.",
            "The chest/pelvis reference comes from direction-retargeting the same local source clip onto the EFT skeleton; source root translation is not exported into the overlay asset.",
            "The 0.4/0.25 light and 0.65/0.5 heavy blend strengths remain runtime policy, not encoded in this asset database.",
        ],
    }
    manifest_path.write_text(json.dumps(manifest, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")

    # Validate the serialized file as the C# loader will see it, rather than only the
    # in-memory objects passed between the exporters.
    written = json.loads(output_path.read_text(encoding="utf-8"))
    _validate_database(written, original_clips, added_names)
    print(json.dumps({
        "output": _relative(output_path),
        "manifest": _relative(manifest_path),
        "schema": written["schema"],
        "clips": len(written["clips"]),
        "preservedStumbles": list(EXISTING_STUMBLES),
        "addedHitReactClips": added_names,
        "addedFrames": {r["name"]: r["frames"] for r in source_records},
        "bytes": len(encoded),
        "sha256": hashlib.sha256(encoded).hexdigest(),
        "upperBodyTracksValidated": len(UPPER_BONES) * len(written["clips"]),
        "overlayOnlyHitClips": True,
    }, indent=2))
    return database, manifest


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source", type=Path, default=DEFAULT_SOURCE, help="local combine_grunt GLB export")
    parser.add_argument("--skeleton", type=Path, default=DEFAULT_SKELETON, help="local EFT skeleton JSON")
    parser.add_argument("--base", type=Path, default=DEFAULT_BASE, help="existing four-stumble upper-body database")
    parser.add_argument("--selection", type=Path, default=DEFAULT_SELECTION, help="curated stage shortlist JSON")
    parser.add_argument("--output", type=Path, default=DEFAULT_OUTPUT, help="expanded database output (must stay under tmp/)")
    parser.add_argument("--manifest", type=Path, help="build manifest; defaults beside the output")
    args = parser.parse_args()
    manifest = args.manifest or args.output.with_suffix(".manifest.json")
    build(args.base, args.source, args.skeleton, args.selection, args.output, manifest)


if __name__ == "__main__":
    main()
