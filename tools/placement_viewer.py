# -*- coding: utf-8 -*-
"""Create a standalone, offline before/after leg-skeleton replay from a capture.

python tools/placement_viewer.py capture.json --output replay.html
Also accepts geometrySchema=1 fleet JSONL. Skeleton lines do not represent mesh
thickness or prove mesh collision. No remote assets or server are required.
"""
import argparse
import json
import math
from pathlib import Path


def vector(value):
    if isinstance(value, dict):
        value = [value.get(k) for k in ("X", "Y", "Z")]
    if not isinstance(value, (list, tuple)) or len(value) != 3:
        return None
    return [round(v, 5) for v in value] if all(isinstance(v, (int, float)) and math.isfinite(v) for v in value) else None


def solver_info(probe, fleet=False):
    names = ("fade", "normal", "soleError", "ankleLimit", "midCorrection") if fleet else (
        "Fading", "GroundNormalAvailable", "SoleTargetError", "AnkleLimitDegrees", "MidpointCorrection")
    result = {label: probe.get(name) for label, name in zip(("fade", "normal", "error", "angle", "middle"), names)}
    result['stopStep'] = probe.get('stopStep' if fleet else 'StopCorrectionStep')
    result['avoidance'] = probe.get('swingAvoidance' if fleet else 'SwingAvoidance')
    return result


def capture_leg(sample, side):
    leg = sample.get("Grounder", {}).get("LeftLeg" if side == "L" else "RightLeg", {})
    points = [vector(leg.get(k + "Position")) if leg.get("Has" + k + "Position") else None for k in ("Hip", "Knee", "Foot", "Toe")]
    return points if all(points[:3]) else None


JUMP_DISTANCE_M = 0.08
PLACEMENT_ADDED_JUMP_M = 0.04
DERIVATIVE_CONTINUITY_DT_S = 0.10
GEOMETRY_CONTINUITY_DT_S = 0.30
CLEARANCE_EPSILON = 0.001


def _finite_number(value):
    return isinstance(value, (int, float)) and not isinstance(value, bool) and math.isfinite(value)


def _distance(a, b):
    if not (isinstance(a, (list, tuple)) and isinstance(b, (list, tuple)) and len(a) == 3 and len(b) == 3):
        return None
    if not all(_finite_number(value) for value in (*a, *b)):
        return None
    return math.sqrt(sum((a[index] - b[index]) ** 2 for index in range(3)))


def _frame_foot(frame, stage, side):
    points = (frame.get(stage) or {}).get(side) or []
    return points[2] if len(points) > 2 else None


def _frame_time(frame):
    return frame.get("t", 0.0) if _finite_number(frame.get("t")) else 0.0


def _frame_number(frame):
    return frame.get("f", 0) if _finite_number(frame.get("f")) else 0


def _clearance_state(frame):
    values = frame.get("clearance") or []
    values = list(values) if isinstance(values, (list, tuple)) else []
    values += [None] * (4 - len(values))
    before, requested, accepted, fraction = values[:4]
    limited = _finite_number(fraction) and fraction < 1.0 - CLEARANCE_EPSILON
    if not limited and _finite_number(requested) and _finite_number(accepted):
        limited = accepted + CLEARANCE_EPSILON < requested
    return {
        "limited": bool(limited),
        "before_m": before if _finite_number(before) else None,
        "requested_m": requested if _finite_number(requested) else None,
        "accepted_m": accepted if _finite_number(accepted) else None,
        "fraction": fraction if _finite_number(fraction) else None,
    }


def _add_issue(issues, frame, index, kind, label, **details):
    issue = {
        "bot": str(frame.get("bot", "")),
        "index": index,
        "frame": _frame_number(frame),
        "time": _frame_time(frame),
        "kind": kind,
        "label": label,
    }
    issue.update(details)
    frame.setdefault("issues", []).append(issue)
    issues.append(issue)


def add_issue_events(frames):
    """Attach inspectable issue markers while preserving timing and raw/final motion evidence.

    These are triage markers for the replay, not conclusions about a placement bug. A large frame-to-frame foot
    movement keeps both the pre-placement and final distances plus ``dt_s`` so a capture hitch or sparse fleet sample
    remains visible and is not attributed to the placer by the viewer.
    """

    by_bot = {}
    for frame in frames:
        frame["issues"] = []
        by_bot.setdefault(str(frame.get("bot", "")), []).append(frame)

    issues = []
    for bot, rows in by_bot.items():
        rows.sort(key=lambda value: (_frame_time(value), _frame_number(value)))
        previous = None
        previous_phase = None
        previous_movement_state = None
        previous_clearance = {"limited": False}
        for index, frame in enumerate(rows):
            phase = frame.get("phase")
            phase_name = str(phase or "").strip().lower()
            if phase_name in {"stop", "hold"} and phase_name != previous_phase:
                _add_issue(issues, frame, index, "phase-entry", phase_name.title() + " entry", phase=phase)

            movement_state = frame.get("movement_state")
            if movement_state != previous_movement_state and movement_state in {"Jump", "JumpLanding", "FallDown", "VaultingFallDown", "VaultingLanding"}:
                _add_issue(issues, frame, index, "native-motion", "Native " + movement_state + " entry", phase=movement_state)
            previous_movement_state = movement_state

            clearance = _clearance_state(frame)
            if clearance["limited"] and not previous_clearance["limited"]:
                _add_issue(issues, frame, index, "clearance-onset", "Clearance intervention onset", **clearance)

            if previous is not None:
                dt = _frame_time(frame) - _frame_time(previous)
                if _finite_number(dt) and dt > 0.0:
                    for side in ("L", "R"):
                        raw_distance = _distance(_frame_foot(previous, "before", side), _frame_foot(frame, "before", side))
                        final_distance = _distance(_frame_foot(previous, "after", side), _frame_foot(frame, "after", side))
                        if raw_distance is None or final_distance is None:
                            continue
                        if max(raw_distance, final_distance) < JUMP_DISTANCE_M:
                            continue
                        added = final_distance - raw_distance
                        hitch = dt > DERIVATIVE_CONTINUITY_DT_S
                        placement_candidate = added >= PLACEMENT_ADDED_JUMP_M and not hitch
                        if hitch:
                            label = "Heuristic foot jump (time gap; inspect raw/final)"
                        elif placement_candidate:
                            label = "Heuristic foot jump (placement-added candidate)"
                        elif raw_distance >= JUMP_DISTANCE_M:
                            label = "Heuristic foot jump (raw motion)"
                        else:
                            label = "Heuristic foot jump (final motion)"
                        _add_issue(
                            issues,
                            frame,
                            index,
                            "foot-jump",
                            label,
                            side=side,
                            dt_s=dt,
                            raw_before_m=raw_distance,
                            final_after_m=final_distance,
                            placement_added_m=max(0.0, added),
                            raw_rate_mps=raw_distance / dt,
                            final_rate_mps=final_distance / dt,
                            hitch_suspect=hitch,
                            derivative_continuous=dt <= DERIVATIVE_CONTINUITY_DT_S,
                            geometry_continuous=dt <= GEOMETRY_CONTINUITY_DT_S,
                        )

            previous = frame
            previous_phase = phase_name
            previous_clearance = clearance

    issues.sort(key=lambda value: (str(value.get("bot", "")), value.get("time", 0.0), value.get("frame", 0)))
    return issues


def read_frames(path):
    frames = []
    if path.suffix.lower() == ".jsonl":
        with path.open(encoding="utf-8-sig") as stream:
            for line in stream:
                try:
                    s = json.loads(line)
                except ValueError:
                    continue
                if s.get("k") != "s":
                    continue
                before, after = {}, {}
                for side in ("L", "R"):
                    f = s.get(side, {})
                    before[side] = [vector(f.get(k)) for k in ("h0", "k0", "a0", "to0")]
                    after[side] = [vector(f.get(k)) for k in ("h", "k", "a", "to")]
                if not all(stage[side][i] for stage in (before, after) for side in ("L", "R") for i in range(3)):
                    continue
                frames.append(dict(t=s["t"], f=s["f"], bot=str(s["b"]), yaw=s.get("yaw", 0),
                    root=[s["x"], s["y"], s["z"]], before=before, after=after,
                    clip=s.get("clip"), phase=s.get("ph"), driver=s.get("drv"),
                    movement_state=(s.get("se") or {}).get("State"), grounded=(s.get("se") or {}).get("Grounded") if (s.get("se") or {}).get("HasContext") else None,
                    clearance=[s.get("L", {}).get(k) for k in ("clearance0", "clearanceWanted", "clearance", "clearanceFraction")],
                    solver=[solver_info(s.get(side, {}), True) for side in ("L", "R")],
                    valid=not (s.get("vis") == 0 or s.get("simp") == 1 or s.get("sus") == 1),
                    lock=[bool(s.get(side, {}).get("lk")) for side in ("L", "R")]))
    else:
        data = json.loads(path.read_text(encoding="utf-8-sig"))
        pairs = {}
        for sample in data.get("Samples", []):
            stage = sample.get("Stage")
            if stage in ("after_visual", "after_lock"):
                pairs.setdefault(sample["Frame"], {})[stage] = sample
        for f, pair in sorted(pairs.items()):
            a, b = pair.get("after_visual"), pair.get("after_lock")
            if a is None or b is None:
                continue
            before = {side: capture_leg(a, side) for side in ("L", "R")}
            after = {side: capture_leg(b, side) for side in ("L", "R")}
            root_snapshot = b.get("Root") or {}
            root = vector(root_snapshot.get("Value")) if root_snapshot.get("Available") is not False else None
            if root is None or not all(before.values()) or not all(after.values()):
                continue
            pose = b.get("Pose") or {}
            frames.append(dict(t=b["Time"], f=f, bot=str(data.get("LocalBotId", "puppet")),
                yaw=b.get("BodyYaw", 0), root=root, before=before, after=after,
                clip=pose.get("Clip"), phase=pose.get("Phase"), driver=pose.get("Driver"),
                movement_state=(pose.get("SprintEntry") or {}).get("State"), grounded=(pose.get("SprintEntry") or {}).get("Grounded") if (pose.get("SprintEntry") or {}).get("HasContext") else None,
                clearance=[(pose.get("PlacerL") or {}).get(k) for k in ("ClearanceBefore", "ClearanceRequested", "ClearanceAfter", "ClearanceFraction")],
                solver=[solver_info(pose.get("Placer" + side) or {}) for side in ("L", "R")],
                valid=not (b.get("HasIsVisible") and not b.get("IsVisible") or b.get("UsedSimplifiedSkeleton")),
                lock=[bool((pose.get("Placer" + side) or {}).get("Locked")) for side in ("L", "R")]))
    add_issue_events(frames)
    return frames


TEMPLATE = r'''<!doctype html>
<html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>Placement replay</title><style>
body{font:15px system-ui,sans-serif;margin:24px;color:#e6edf5;background:#131820}main{max-width:1200px;margin:auto}h1{font-size:24px;font-weight:600}p{color:#adbaca;line-height:1.5}.controls{display:flex;gap:16px;align-items:center;flex-wrap:wrap;margin:16px 0}button,select{font:inherit;padding:8px;color:inherit;background:#263344;border:1px solid #60718a;border-radius:5px}input[type=range]{width:100%}label{display:flex;gap:8px;align-items:center}.views{display:grid;grid-template-columns:repeat(3,minmax(0,1fr));gap:12px}canvas{display:block;width:100%;height:360px;background:#19222f;border-radius:6px}.meta{min-height:50px;white-space:pre-wrap;font-variant-numeric:tabular-nums}.before{color:#f4b65e}.after{color:#63c3ff}.note{font-size:13px}#events{max-width:100%}@media(max-width:750px){body{margin:12px}.views{grid-template-columns:1fr}canvas{height:290px}}
</style></head><body><main><h1>Foot and leg placement replay</h1>
<p><span class="before">Dashed: before placement</span> · <span class="after">Solid: after placement</span> · Circle: left leg · Square: right leg</p>
<div class="controls"><button id="play" type="button">Play</button><label>Bot <select id="bot"></select></label><label>Speed <select id="rate"><option value="0.25">¼×</option><option value="0.5">½×</option><option value="1" selected>1×</option></select></label><label><input type="checkbox" id="before" checked>Before</label><label><input type="checkbox" id="after" checked>After</label></div>
<label for="time">Frame <output id="stamp"></output></label><input id="time" type="range" min="0" value="0" step="1"><div class="controls"><label>Jump to game frame <input id="jump" type="number" min="0" step="1" style="width:110px;font:inherit"></label><button id="jumpButton" type="button">Go</button></div>
<div class="controls"><label>Issue <select id="issue"></select></label><button id="previousIssue" type="button">Previous issue</button><button id="nextIssue" type="button">Next issue</button></div>
<div id="meta" class="meta"></div><div class="views"><canvas id="front" aria-label="Front view of leg joints"></canvas><canvas id="side" aria-label="Side view of leg joints"></canvas><canvas id="top" aria-label="Top view of leg joints"></canvas></div>
<p class="note">Views follow the body's position and facing. Lines show joint centers, not body mesh; a crossing in one projection does not prove a collision. Missing or culled frames are labeled. Before/after stages share the same game frame. Issue markers are heuristic; foot jumps retain raw/final displacement and dt so a capture hitch is not assigned to placement by itself.</p>
<p id="source" class="note"></p></main><script>
const payload=__DATA__;
const byBot=new Map();for(const s of payload.frames){if(!byBot.has(s.bot))byBot.set(s.bot,[]);byBot.get(s.bot).push(s)}
const bot=document.getElementById('bot'), slider=document.getElementById('time'),play=document.getElementById('play');
const issueSelect=document.getElementById('issue'),previousIssue=document.getElementById('previousIssue'),nextIssue=document.getElementById('nextIssue');
for(const [id,ss] of byBot){ss.sort((a,b)=>a.t-b.t||a.f-b.f);const o=document.createElement('option');o.value=id;o.textContent=id+' ('+ss.length+' frames)';bot.append(o)}
let frames=[],issues=[],issueIndex=-1,running=false,started=0,originTime=0;
function chooseIssues(){issues=(payload.issues||[]).filter(i=>String(i.bot)===String(bot.value));issueSelect.replaceChildren();if(!issues.length){const o=document.createElement('option');o.textContent='No heuristic issues';issueSelect.append(o);issueSelect.disabled=true;previousIssue.disabled=true;nextIssue.disabled=true;issueIndex=-1;return}issueSelect.disabled=false;for(const [index,i] of issues.entries()){const o=document.createElement('option');o.value=index;o.textContent=i.label+' · frame '+i.frame+' · '+Number(i.time).toFixed(3)+' s';issueSelect.append(o)}issueIndex=0;issueSelect.value='0';previousIssue.disabled=false;nextIssue.disabled=false}
function choose(){frames=byBot.get(bot.value)||[];slider.max=Math.max(0,frames.length-1);slider.value=0;running=false;play.textContent='Play';chooseIssues();draw()}
function goIssue(index){if(!issues.length)return;issueIndex=(index+issues.length)%issues.length;const target=issues[issueIndex];slider.value=Math.max(0,Math.min(frames.length-1,target.index));issueSelect.value=String(issueIndex);slider.oninput()}
function local(p,s){if(!p)return null;const x=p[0]-s.root[0],y=p[1]-s.root[1],z=p[2]-s.root[2],a=s.yaw*Math.PI/180;return [x*Math.cos(a)-z*Math.sin(a),y,x*Math.sin(a)+z*Math.cos(a)]}
function pane(name,s){const canvas=document.getElementById(name),dpr=window.devicePixelRatio||1,w=canvas.clientWidth,h=canvas.clientHeight;canvas.width=w*dpr;canvas.height=h*dpr;const c=canvas.getContext('2d');c.scale(dpr,dpr);const scale=Math.min(w/2.1,(h-50)/1.7),cx=w/2,cy=name==='top'?h/2:h-35;
const xy=p=>name==='front'?[cx+p[0]*scale,cy-p[1]*scale]:name==='side'?[cx+p[2]*scale,cy-p[1]*scale]:[cx+p[0]*scale,cy-p[2]*scale];
c.strokeStyle='#33445a';c.lineWidth=1;for(let q=-1;q<=1.51;q+=.25){c.beginPath();if(name==='top'){c.moveTo(cx+q*scale,0);c.lineTo(cx+q*scale,h)}else{c.moveTo(0,cy-q*scale);c.lineTo(w,cy-q*scale)}c.stroke()}c.fillStyle='#b5c4d7';c.font='14px system-ui';c.fillText(name==='front'?'Front · horizontal = body right':name==='side'?'Side · horizontal = body forward':'Top · up = body forward',12,22);c.fillText('Grid 0.25 m',12,h-12);
for(const stage of ['before','after']){if(!document.getElementById(stage).checked)continue;c.strokeStyle=c.fillStyle=stage==='before'?'#f4b65e':'#63c3ff';c.lineWidth=stage==='before'?2:3;c.setLineDash(stage==='before'?[5,4]:[]);for(const side of ['L','R']){const ps=(s[stage][side]||[]).map(p=>local(p,s));c.beginPath();let pen=false;for(const p of ps){if(!p){pen=false;continue}const [x,y]=xy(p);if(pen)c.lineTo(x,y);else c.moveTo(x,y);pen=true}c.stroke();for(const p of ps.slice(0,3)){if(!p)continue;const[x,y]=xy(p);c.beginPath();if(side==='L')c.arc(x,y,3.5,0,2*Math.PI);else c.rect(x-3.5,y-3.5,7,7);c.fill()}}
const left=s[stage].L?.[0],right=s[stage].R?.[0];if(left&&right){const a=xy(local(left,s)),b=xy(local(right,s));c.beginPath();c.moveTo(...a);c.lineTo(...b);c.stroke()}}
c.setLineDash([]);const o=xy([0,0,0]);c.fillStyle='#dae6f5';c.fillRect(o[0]-3,o[1]-3,6,6);if(s.issues?.length){c.strokeStyle='#ff6b6b';c.fillStyle='#ff6b6b';c.lineWidth=2;for(const issue of s.issues){if(issue.kind==='foot-jump'&&issue.side){const point=s.after?.[issue.side]?.[2];const p=point&&xy(local(point,s));if(p){c.beginPath();c.arc(p[0],p[1],8,0,2*Math.PI);c.stroke();c.beginPath();c.moveTo(p[0]-11,p[1]);c.lineTo(p[0]+11,p[1]);c.moveTo(p[0],p[1]-11);c.lineTo(p[0],p[1]+11);c.stroke()}}else{c.beginPath();c.arc(o[0],o[1],8,0,2*Math.PI);c.stroke()}}}
}
function draw(){
if(!frames.length){document.getElementById('meta').textContent='No paired geometry available.';return}
const s=frames[Number(slider.value)];document.getElementById('stamp').textContent=s.f+' · '+s.t.toFixed(3)+' s';
let meta=(s.clip||'Native animation')+' · '+(s.phase||'control')+' · '+(s.driver||'driver unrecorded')+'\nLocks L/R: '+s.lock.map(v=>v?'yes':'no').join(' / ')+(s.valid?'':' · CULLED / SIMPLIFIED / SUSPENDED');
meta+='\nMovement: '+(s.movement_state||'unknown')+' · grounded: '+(s.grounded==null?'unknown':s.grounded?'yes':'no');
if(s.clearance?.every(v=>Number.isFinite(v))){const [a,b,c,f]=s.clearance;meta+='\nBone clearance: '+(a*100).toFixed(1)+' cm before → '+(b*100).toFixed(1)+' cm requested → '+(c*100).toFixed(1)+' cm accepted · correction '+(f*100).toFixed(1)+'%'}
if(s.solver?.some(p=>p.error!=null)){const fmt=(v,k=1)=>Number.isFinite(v)?(v*k).toFixed(1):'?';meta+='\nFinal sole target error L/R (cm): '+s.solver.map(p=>fmt(p.error,100)).join(' / ')+' · ankle limitation (degrees): '+s.solver.map(p=>fmt(p.angle)).join(' / ')+'\nGround normal available L/R: '+s.solver.map(p=>p.normal==null?'?':p.normal?'yes':'no').join(' / ');meta+='\nSwing detour L/R (cm): '+s.solver.map(p=>fmt(p.avoidance,100)).join(' / ')+' · corrective stop step L/R: '+s.solver.map(p=>p.stopStep?'yes':'no').join(' / ')}
if(s.issues?.length){meta+='\nIssues at this frame:';for(const issue of s.issues){if(issue.kind==='foot-jump'){meta+='\n· '+issue.label+' · '+issue.side+' foot: raw '+(issue.raw_before_m*100).toFixed(1)+' cm, final '+(issue.final_after_m*100).toFixed(1)+' cm, added '+(issue.placement_added_m*100).toFixed(1)+' cm, dt '+issue.dt_s.toFixed(3)+' s';if(issue.hitch_suspect)meta+=' (time gap; inspect both stages)'}else if(issue.kind==='clearance-onset'){meta+='\n· '+issue.label+' · accepted '+(issue.accepted_m==null?'?':(issue.accepted_m*100).toFixed(1)+' cm')+' / requested '+(issue.requested_m==null?'?':(issue.requested_m*100).toFixed(1)+' cm')+' · fraction '+(issue.fraction==null?'?':(issue.fraction*100).toFixed(1)+'%')}else meta+='\n· '+issue.label+' · phase '+(issue.phase||s.phase||'unknown')}}
document.getElementById('meta').textContent=meta;for(const name of ['front','side','top'])pane(name,s)}
play.onclick=()=>{running=!running;play.textContent=running?'Pause':'Play';if(running){if(Number(slider.value)>=frames.length-1)slider.value=0;started=performance.now();originTime=frames[Number(slider.value)].t;requestAnimationFrame(tick)}};
function tick(now){if(!running)return;const wanted=originTime+(now-started)/1000*Number(document.getElementById('rate').value);let i=Number(slider.value);while(i<frames.length-1&&frames[i+1].t<=wanted)i++;slider.value=i;draw();if(i===frames.length-1){running=false;play.textContent='Play'}else requestAnimationFrame(tick)}
slider.oninput=()=>{running=false;play.textContent='Play';const current=Number(slider.value),match=issues.findIndex(i=>i.index===current);if(match>=0){issueIndex=match;issueSelect.value=String(match)}draw()};bot.onchange=choose;issueSelect.onchange=()=>goIssue(Number(issueSelect.value));previousIssue.onclick=()=>goIssue(issueIndex-1);nextIssue.onclick=()=>goIssue(issueIndex+1);for(const id of ['before','after'])document.getElementById(id).onchange=draw;document.getElementById('rate').onchange=()=>{started=performance.now();originTime=frames[Number(slider.value)].t};document.getElementById('jumpButton').onclick=()=>{const target=Number(document.getElementById('jump').value);let closest=0;for(let i=1;i<frames.length;i++)if(Math.abs(frames[i].f-target)<Math.abs(frames[closest].f-target))closest=i;slider.value=closest;slider.oninput()};window.addEventListener('resize',draw);document.getElementById('source').textContent=payload.source;choose();
</script></body></html>'''


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("capture", type=Path)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    frames = read_frames(args.capture)
    if not frames:
        parser.error("No paired leg geometry: use a puppet capture or geometrySchema=1 fleet stream")
    issues = [issue for frame in frames for issue in frame.get("issues", [])]
    payload = json.dumps({"source": str(args.capture.resolve()), "frames": frames, "issues": issues}, separators=(",", ":"), allow_nan=False).replace("<", "\\u003c")
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(TEMPLATE.replace("__DATA__", payload), encoding="utf-8")
    print(f"Saved {len(frames)} frames and {len(issues)} heuristic issues to {args.output.resolve()}")


if __name__ == "__main__":
    main()
