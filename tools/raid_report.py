"""Read a player report ZIP and produce a standalone skeleton replay and analysis.

Usage: python tools/raid_report.py report.zip --output artifacts/player-report
No extraction, game installation, or third-party Python packages required.
"""
import argparse
import collections
import html
import json
from pathlib import Path
import zipfile


def read_report(path):
    if str(path).endswith(".partial.jsonl"):
        if Path(path).stat().st_size > 128 * 1024 * 1024:
            raise ValueError("Checkpoint exceeds 128 MiB read budget")
        data = {"frames.jsonl": [], "windows.jsonl": [], "events.jsonl": []}
        with Path(path).open(encoding="utf-8-sig") as source:
            for line in source:
                if not line.strip():
                    continue
                row = json.loads(line)
                kind = row.pop("kind", None)
                if kind == "checkpoint":
                    data["summary.json"] = {**row, "recoveredCheckpoint": True}
                elif kind == "checkpointSummary":
                    data.setdefault("summary.json", {})["checkpointSummary"] = row
                elif kind in ("frame", "window", "event"):
                    data[kind + "s.jsonl"].append(row)
        if "summary.json" not in data:
            raise ValueError("Missing checkpoint header")
        return data
    with zipfile.ZipFile(path) as archive:
        allowed = {"summary.json", "frames.jsonl", "events.jsonl", "windows.jsonl"}
        if len(archive.infolist()) > 8:
            raise ValueError("Unexpected archive layout")
        data = {}
        total = 0
        for entry in archive.infolist():
            if entry.filename not in allowed:
                continue
            total += entry.file_size
            if total > 128 * 1024 * 1024:
                raise ValueError("Report exceeds 128 MiB read budget")
            if entry.filename in data:
                raise ValueError("Duplicate report entry")
            raw = archive.read(entry).decode("utf-8-sig")
            data[entry.filename] = (json.loads(raw) if entry.filename.endswith(".json")
                                    else [json.loads(line) for line in raw.splitlines() if line.strip()])
        if "summary.json" not in data:
            raise ValueError("Missing summary.json")
        return data


def analyze(data):
    frames = data.get("frames.jsonl", [])
    events = data.get("events.jsonl", [])
    event_types = collections.Counter(str(e.get("row", e).get("type", "unknown")) for e in events)
    stages = collections.Counter(str(f.get("stage", "unknown")) for f in frames)
    windows = ({str(w.get("windowId", w.get("id"))): len(w.get("frameIds", [])) for w in data["windows.jsonl"]}
               if "windows.jsonl" in data else collections.Counter(str(f.get("windowId", f.get("window", "unknown"))) for f in frames))
    return {"summary": data["summary.json"], "replayRows": len(frames),
            "eventRows": len(events), "stages": dict(stages), "windowRows": dict(windows),
            "retainedEventTypes": dict(event_types),
            "interpretation": "Flags select motion to inspect; they do not prove a visible defect. Compare native and final stages. Sampling and output caps are reported in the summary."}


TEMPLATE = r'''<!doctype html><meta charset="utf-8"><title>Raid motion replay</title>
<style>body{background:#12161c;color:#e7edf5;font:15px system-ui;margin:24px}select,button,input{margin:8px;padding:6px;background:#242c38;color:inherit;border:1px solid #566}canvas{display:block;background:#19212b;width:100%;height:65vh}pre{white-space:pre-wrap;font:12px monospace}summary{cursor:pointer}label{margin-right:12px}</style>
<h1>Raid motion replay</h1><p>Automatic clips from a player raid. Orange: native before placement. Cyan: final pose. Skeletons show recorded geometry; the game mesh, terrain, and weapon model are not reconstructed.</p>
<label>Window <select id="window"></select></label><label>Final stage <select id="stage"><option>pre_render</option><option>after_lock</option><option>before_visual</option><option>after_visual</option></select></label>
<label>View <select id="view"><option value="side">Side</option><option value="front">Front</option><option value="top">Top</option></select></label><button id="play">Play</button><input id="scrub" type="range" min="0" value="0" step="1"><span id="stamp"></span>
<canvas id="canvas"></canvas><pre id="info"></pre><details><summary>Report summary and capture limits</summary><pre id="summary"></pre></details>
<script>const DATA=__DATA__;
const $=id=>document.getElementById(id),frames=DATA['frames.jsonl']||[],groups=new Map();
if(DATA['windows.jsonl']){const byId=new Map(frames.map(f=>[f.frameId,f]));for(const win of DATA['windows.jsonl'])groups.set(win.windowId??win.id,win.frameIds.map(id=>byId.get(id)).filter(Boolean))}else{for(const f of frames){const id=f.windowId??f.window??0;if(!groups.has(id))groups.set(id,[]);groups.get(id).push(f)}}
for(const [id,rows] of groups){const o=document.createElement('option');o.value=id;o.textContent=`${id} · bot ${rows[0]?.botId??rows[0]?.bot??'?'} · ${rows.length} stage samples`;$('window').append(o)}
$('summary').textContent=JSON.stringify(DATA['summary.json'],null,2);
let times=[],rows=[],playing=false,last=0,index=0;
const links=[['pelvis','spine1'],['spine1','spine2'],['spine2','spine3'],['spine3','ribcage'],['ribcage','neck'],['neck','head'],['ribcage','leftupperarm'],['leftupperarm','leftforearm'],['leftforearm','lefthand'],['ribcage','rightupperarm'],['rightupperarm','rightforearm'],['rightforearm','righthand'],['pelvis','leftthigh'],['leftthigh','leftcalf'],['leftcalf','leftfoot'],['leftfoot','lefttoe'],['pelvis','rightthigh'],['rightthigh','rightcalf'],['rightcalf','rightfoot'],['rightfoot','righttoe']];
function key(k){const n=k.toLowerCase().replace(/[^a-z0-9]/g,'');return ({chest:'ribcage',leftknee:'leftcalf',rightknee:'rightcalf',leftpalm:'lefthand',rightpalm:'righthand'})[n]||n}
function body(s){let b={};for(const [k,v]of Object.entries(s.body||{})){let p=Array.isArray(v)?v:(v.p||v.position||v.pos);if(p&&p.length>=3)b[key(k)]=p}return b}
function project(p,w,h){let a=p[0],b=p[1];if($('view').value==='side')a=p[2];if($('view').value==='top'){a=p[0];b=p[2]}return[w*.5+a*h*.32,h*.85-b*h*.32]}
function drawSkeleton(ctx,s,color,w,h){const bones=body(s);ctx.strokeStyle=color;ctx.fillStyle=color;ctx.lineWidth=3;for(const [a,b]of links){if(!bones[a]||!bones[b])continue;const x=project(bones[a],w,h),y=project(bones[b],w,h);ctx.beginPath();ctx.moveTo(...x);ctx.lineTo(...y);ctx.stroke()}for(const p of Object.values(bones)){ctx.beginPath();ctx.arc(...project(p,w,h),3,0,7);ctx.fill()}}
function redraw(){const c=$('canvas'),ctx=c.getContext('2d');c.width=c.clientWidth*devicePixelRatio;c.height=c.clientHeight*devicePixelRatio;let w=c.width,h=c.height;ctx.clearRect(0,0,w,h);const t=times[index];const same=rows.filter(f=>f.time===t);const final=same.find(f=>f.stage===$('stage').value)||same.find(f=>f.stage==='after_lock')||same[0];const native=same.find(f=>f.stage==='after_visual');if(native)drawSkeleton(ctx,native.snapshot||{},'#eea950',w,h);if(final)drawSkeleton(ctx,final.snapshot||{},'#47dae1',w,h);$('stamp').textContent=t===undefined?'No replay windows':`${t.toFixed(3)} s (${$('stage').value})`;$('info').textContent=final?JSON.stringify({stage:final.stage,trigger:final.trigger,...(final.snapshot?.reaction||{}),grip:final.snapshot?.grip},null,2):'';$('scrub').value=index}
function select(){rows=groups.get(Number($('window').value))||groups.get($('window').value)||[];times=[...new Set(rows.map(f=>f.time))].sort((a,b)=>a-b);index=0;$('scrub').max=Math.max(0,times.length-1);redraw()}
$('window').onchange=select;$('stage').onchange=redraw;$('view').onchange=redraw;$('scrub').oninput=()=>{index=Number($('scrub').value);redraw()};$('play').onclick=()=>{playing=!playing;$('play').textContent=playing?'Pause':'Play';last=0};
function tick(now){if(playing&&times.length){if(!last)last=now;if(now-last>=Math.max(10,((times[index+1]??times[index]+.033)-times[index])*1000)){index=(index+1)%times.length;last=now;redraw()}}requestAnimationFrame(tick)}select();requestAnimationFrame(tick);onresize=redraw;
</script>'''


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("report", type=Path)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    data = read_report(args.report)
    args.output.mkdir(parents=True, exist_ok=True)
    (args.output / "analysis.json").write_text(json.dumps(analyze(data), indent=2), encoding="utf-8")
    payload = json.dumps(data, separators=(",", ":")).replace("<", "\\u003c").replace("\u2028", "\\u2028").replace("\u2029", "\\u2029")
    (args.output / "replay.html").write_text(TEMPLATE.replace("__DATA__", payload), encoding="utf-8")
    print(args.output / "replay.html")


if __name__ == "__main__":
    main()
