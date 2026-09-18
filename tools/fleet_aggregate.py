"""Cross-raid aggregate of fleet reports: pooled anomaly counts per movement-minute, layer against control.

    python tools/fleet_aggregate.py <artifacts/fleet/<stamp>/manifest.json | fleet-*.jsonl ...>

Takes the streams named in a runner manifest (or given directly), runs tools/fleet_report.py's analysis on each,
and pools counts and exposure (moving seconds) across raids before dividing, separately for layer raids and
control raids, per situation and per clip. Also lists the worst episodes across all raids with their stream, bot
and frame so they can be pulled up.
"""
import json
import subprocess
import sys
from collections import Counter, defaultdict
from pathlib import Path

HERE = Path(__file__).resolve().parent


def report_for(stream):
    out = subprocess.run([sys.executable, str(HERE / "fleet_report.py"), str(stream), "--episodes", "20"], capture_output=True, text=True, encoding="utf-8")
    if out.returncode != 0:
        return None
    return json.loads(out.stdout)


def main():
    streams = []
    for arg in sys.argv[1:]:
        p = Path(arg)
        if p.suffix == ".json":
            for entry in json.loads(p.read_text(encoding="utf-8")):
                if entry.get("Stream"):
                    streams.append((Path(entry["Stream"]), bool(entry.get("Control")), entry.get("Map")))
        else:
            streams.append((p, False, None))
    groups = {"layer": [], "control": []}
    for stream, control, game_map in streams:
        if not stream.exists():
            continue
        rep = report_for(stream)
        if rep is None:
            continue
        rep["_map"] = game_map or (rep.get("meta") or {}).get("map")
        rep["_control"] = control or any(b.get("control") for b in (rep.get("meta") or {}).get("bots", []) if isinstance(b, dict))
        groups["control" if rep["_control"] else "layer"].append(rep)
    result = {"raids": [], "pooled": {}}
    for kind, reps in groups.items():
        flags = defaultdict(Counter)
        exposure = Counter()
        clips = defaultdict(Counter)
        totals = Counter()
        sustained = Counter()
        bots = 0
        for rep in reps:
            result["raids"].append({"kind": kind, "map": rep["_map"], "file": rep["file"], "bots": len(rep["bots"]),
                                    "moving_seconds": rep["moving_seconds_by_situation"], "totals": rep["totals"], "perf": rep.get("perf_ms_per_frame_p50_max")})
            bots += len(rep["bots"])
            for tg, secs in rep["moving_seconds_by_situation"].items():
                exposure[tg] += secs
            for tg, c in rep["by_situation"].items():
                for fl, n in c.items():
                    flags[tg][fl] += n
            for clip, c in rep.get("flags_by_clip", {}).items():
                for fl, n in c.items():
                    clips[clip][fl] += n
            for fl, n in rep["totals"].items():
                totals[fl] += n
            for fl, n in rep.get("sustained_episodes", {}).items():
                sustained[fl] += n
        result["pooled"][kind] = {
            "raids": len(reps), "bots": bots, "moving_seconds": dict(exposure), "totals": dict(totals),
            "sustained_episodes_per_moving_minute": {fl: round(n / max(sum(exposure.values()), 1e-6) * 60, 2) for fl, n in sustained.items()},
            "per_moving_minute": {tg: {fl: round(n / max(exposure[tg], 1e-6) * 60, 1) for fl, n in c.items()} for tg, c in flags.items()},
            "by_clip": {k: dict(v) for k, v in sorted(clips.items(), key=lambda kv: -sum(kv[1].values()))[:20]},
        }
    episodes = []
    for kind, reps in groups.items():
        for rep in reps:
            for e in rep["episodes"]:
                e = dict(e); e["file"] = Path(rep["file"]).name; e["kind"] = kind; e["map"] = rep["_map"]
                episodes.append(e)
    episodes.sort(key=lambda e: -(e["frames"] * (1 + len(e["flags"]))))
    result["worst_episodes"] = episodes[:25]
    print(json.dumps(result, indent=1, default=str))


if __name__ == "__main__":
    main()
