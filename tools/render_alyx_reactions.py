"""Render local Alyx GLB reaction animations as labelled skeleton GIFs and a gallery."""
import argparse
import hashlib
import html
import json
import math
import re
from pathlib import Path

from PIL import Image, ImageDraw, ImageFont
from alyx_retarget import GltfSource, q_mul, q_norm, q_rot, v_add

FPS = 20
CHAINS = [
    ['pelvis', 'spine_0', 'spine_1', 'spine_2', 'spine_3', 'neck_0', 'head', 'head_end'],
    *[['spine_3', f'clavicle_{s}', f'arm_upper_{s}', f'arm_lower_{s}', f'hand_{s}', f'hand_end_{s}'] for s in ('L', 'R')],
    *[['pelvis', f'leg_upper_{s}', f'leg_lower_{s}', f'ankle_{s}', f'ball_{s}', f'ball_end_{s}'] for s in ('L', 'R')],
]
NAMES = list(dict.fromkeys(n for c in CHAINS for n in c))
EDGES = [(NAMES.index(a), NAMES.index(b)) for c in CHAINS for a, b in zip(c, c[1:])]


def sample_clip(source, name):
    tracks, duration = source.tracks(name)
    additive = 'additive' in name
    def world(node, t, cache):
        if node in cache:
            return cache[node]
        p, q = source.local(node, tracks, t)
        if additive:
            rest = source.nodes[node]
            if (node, 'translation') in tracks:
                p = v_add(tuple(rest.get('translation', (0,0,0))), p)
            if (node, 'rotation') in tracks:
                q = q_norm(q_mul(tuple(rest.get('rotation', (0,0,0,1))), q))
        if node in source.parent:
            pp, pq = world(source.parent[node], t, cache)
            p, q = v_add(pp, q_rot(pq, p)), q_norm(q_mul(pq, q))
        cache[node] = p, q
        return p, q
    frames = []
    for f in range(math.ceil(duration * FPS) + 1):
        cache = {}
        frames.append([world(source.index[n], min(f / FPS, duration), cache)[0] for n in NAMES])
    # Keep displacement and height; translate the initial root to the view origin only.
    origin = world(source.index['root_motion'], 0, {})[0]
    frames = [[[round(p[k] - origin[k], 5) for k in range(3)] for p in frame] for frame in frames]
    return duration, frames


def render(name, model, duration, frames, path):
    try:
        font = ImageFont.truetype('C:/Windows/Fonts/segoeui.ttf', 15)
        small = ImageFont.truetype('C:/Windows/Fonts/segoeui.ttf', 12)
    except OSError:
        font = small = ImageFont.load_default()
    # Fixed camera and scale throughout the clip: no frame-dependent recentering.
    mins = [min(p[k] for f in frames for p in f) for k in range(3)]
    maxs = [max(p[k] for f in frames for p in f) for k in range(3)]
    floor = min(0, mins[1])
    scale = min(155, 270 / max(.1, maxs[1] - floor), 290 / max(.1, maxs[0] - mins[0], maxs[2] - mins[2]))
    result = []
    for fi, points in enumerate(frames):
        im = Image.new('RGB', (720, 410), '#111a24')
        d = ImageDraw.Draw(im)
        d.text((16, 8), name, font=font, fill='#f1f5f9')
        mode = 'Illustrative additive on GLB rest pose' if 'additive' in name else 'Source skeleton'
        d.text((16, 31), model + '  |  ' + mode + '  |  1x speed', font=small, fill='#9eafc1')
        for view, axis in enumerate((0, 2)):
            cx = 180 + 360 * view
            center = (mins[axis] + maxs[axis]) / 2
            base = 352 + floor * scale
            def proj(p):
                return (cx + (p[axis] - center) * scale, base - p[1] * scale)
            ground_y = base
            d.line((view * 360 + 12, ground_y, view * 360 + 348, ground_y), fill='#40536a', width=1)
            for g in range(-10, 11):
                x = cx + (g * .25 - center) * scale
                if view * 360 + 12 <= x <= view * 360 + 348:
                    d.line((x, ground_y - 4, x, ground_y + 4), fill='#40536a')
            d.text((view * 360 + 16, 60), 'FRONT' if view == 0 else 'SIDE', font=small, fill='#9eafc1')
            for a, b in EDGES:
                color = '#56bfff' if NAMES[b].endswith('_L') else '#ff9b64' if NAMES[b].endswith('_R') else '#dfe8f3'
                d.line([proj(points[a]), proj(points[b])], fill=color, width=4)
                x, y = proj(points[b])
                d.ellipse((x-3,y-3,x+3,y+3), fill=color)
            x, y = proj(points[NAMES.index('head')])
            d.ellipse((x-9,y-9,x+9,y+9), outline='#dfe8f3', width=2)
        d.line((360, 60, 360, 360), fill='#293749')
        t = min(fi / FPS, duration)
        d.text((16, 376), f'{t:.2f} / {duration:.2f} s     Blue: left   Orange: right    Grid: 0.25 m', font=small, fill='#b7c7d8')
        d.rectangle((16, 401, 16 + 688 * t / max(duration, .001), 404), fill='#56bfff')
        result.append(im)
    result[0].save(path, save_all=True, append_images=result[1:], duration=[50] * (len(result)-1) + [550], loop=0, optimize=False)
    return result[len(result)//2]


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('--source', type=Path, default=Path('tmp/alyx'))
    p.add_argument('--output', type=Path, default=Path('artifacts/alyx-reactions'))
    p.add_argument('--reuse-fullbody', action='store_true', help='Reuse existing full-body GIFs; rebuild additive previews and inventory')
    args = p.parse_args()
    args.output.mkdir(parents=True, exist_ok=True)
    records = {}
    # Grunt is the representative where available. Preserve distinct exported model variants.
    paths = sorted(args.source.glob('combine_*.glb'), key=lambda p: (p.stem != 'combine_grunt', p.stem))
    for path in paths:
        if 'physics' in path.stem:
            continue
        source = GltfSource(path)
        assert all(n in source.index for n in NAMES), path
        assert all(not n.get('scale') or n['scale'] == [1,1,1] for n in source.nodes), 'Unsupported scaled source'
        for name in sorted(source.animations):
            if not re.search(r'flinch|hit.?react', name, re.I):
                continue
            clip = source.animations[name]
            assert all(s.get('interpolation', 'LINEAR') == 'LINEAR' for s in clip['samplers'])
            duration, frames = sample_clip(source, name)
            # Millimetre precision suppresses insignificant exporter float differences.
            signature = hashlib.sha256(json.dumps([[[round(v,3) for v in p] for p in f] for f in frames]).encode()).hexdigest()
            group = records.setdefault(name, [])
            same = next((r for r in group if r['signature'] == signature), None)
            if same:
                same['models'].append(path.stem)
                continue
            stem = re.sub(r'[^a-zA-Z0-9_-]', '_', name) + ('__' + path.stem if group else '')
            record = dict(name=name, models=[path.stem], duration=duration, frames=len(frames), signature=signature, gif=stem+'.gif')
            group.append(record)
            if not (args.reuse_fullbody and (args.output / record['gif']).exists() and 'additive' not in name and name != 'flinch_gesture_zero'):
                poster = render(name, path.stem, duration, frames, args.output / record['gif'])
                poster.save(args.output / (stem+'.png'))
            print(f'{name} / {path.stem}: {duration:.3f}s', flush=True)
        del source
    (args.output / 'manifest.json').write_text(json.dumps(records, indent=2), encoding='utf-8')
    cards = []
    for name, variants in sorted(records.items()):
        r = variants[0]
        category = 'Additive / reference' if 'additive' in name or 'zero' in name else 'Running' if name.endswith('_run') else 'Hit reactions' if 'hitreact' in name else 'Flinches'
        def media(v):
            return f'<p>{html.escape(", ".join(v["models"]))}  |  {v["duration"]:.3f} s</p><img loading="lazy" src="{v["gif"]}" alt="{html.escape(name)} skeleton animation"><a href="{v["gif"]}" target="_blank">Open playblast</a>'
        extra = '<details><summary>Other source model variants ('+str(len(variants)-1)+')</summary>'+''.join(media(v) for v in variants[1:])+'</details>' if len(variants)>1 else ''
        cards.append(f'<article data-category="{category}" data-name="{html.escape(name)}"><h2>{html.escape(name)}</h2><span>{category}</span>{media(r)}{extra}</article>')
    document = '''<!doctype html><meta charset="utf-8"><title>Alyx reaction playblasts</title>
<style>body{background:#0b121b;color:#e8eff8;font:16px system-ui;margin:32px}h1{font-size:30px}header{max-width:1000px}p{color:#adbed1;font-size:14px}input,select{padding:12px;background:#1b2b40;color:white;border:1px solid #47607d;border-radius:6px;margin:8px}main{display:grid;grid-template-columns:repeat(auto-fit,minmax(460px,1fr));gap:20px}article{background:#142031;padding:18px;border-radius:12px;min-width:0}h2{font-size:17px;overflow-wrap:anywhere}img{width:100%;height:auto}a,summary{color:#71c8ff}summary{cursor:pointer;padding:15px 0}span{font-size:12px;color:#ffb589}[hidden]{display:none!important}</style>
<header><h1>Alyx  |  flinches & hit reactions</h1><p>COUNT named clips. Front and side skeleton playblasts at normal speed, sampled at 20 fps, with a half-second hold before each repeat. Blue = left; orange = right. Fixed camera per clip preserves root travel.</p><p>GLB source animation, before Tarkov retargeting. Explicitly additive clips are illustrative reconstructions over the GLB rest pose (local translation + offset; rest rotation * delta), not a recreation of Source 2 gameplay blending. Identical sampled skeleton motion across models is grouped; distinct model variants are expandable.</p></header>
<input id="search" placeholder="Search clip name"><select id="category"><option>All</option><option>Flinches</option><option>Hit reactions</option><option>Running</option><option>Additive / reference</option></select><p id="count"></p><main>CARDS</main>
<script>function filter(){let n=0;document.querySelectorAll('article').forEach(a=>{a.hidden=!(a.dataset.name.toLowerCase().includes(search.value.toLowerCase())&&(category.value==='All'||a.dataset.category===category.value));if(!a.hidden)n++});document.getElementById('count').textContent=n+' clips shown'}search.oninput=category.onchange=filter;filter()</script>'''.replace('COUNT', str(len(records))).replace('CARDS', ''.join(cards))
    (args.output / 'index.html').write_text(document, encoding='utf-8')
    print(json.dumps({'names':len(records),'distinct_previews':sum(map(len,records.values())),'gallery':str(args.output/'index.html')}))


if __name__ == '__main__':
    main()
