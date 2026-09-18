"""Build a local, curated reaction gallery from reviewed clips and source metadata."""
import html
import json
import shutil
import zipfile
from pathlib import Path

from PIL import Image
from alyx_retarget import GltfSource
from render_alyx_reactions import render, sample_clip


def main():
    output = Path('artifacts/alyx-curated')
    selection = json.loads((output / 'selection.json').read_text(encoding='utf-8'))
    event_lookup = {r['clip']: r for r in json.loads(Path('artifacts/alyx-death-preview/ragdoll-events.json').read_text(encoding='utf-8'))}
    old = json.loads(Path('artifacts/alyx-reactions/manifest.json').read_text(encoding='utf-8'))
    sampled = json.loads((output / 'sampled.json').read_text(encoding='utf-8'))
    source = None
    sections = []
    manifest = []
    for stage in selection:
        cards = []
        for item in stage['clips']:
            name = item['name']
            gif = output / (name + '.gif')
            if name in old:
                v = old[name][0]
                assert v['models'][0] == 'combine_grunt'
                shutil.copyfile(Path('artifacts/alyx-reactions') / v['gif'], gif)
                duration = v['duration']
            elif name in sampled:
                duration = sampled[name]['duration']
            else:
                if source is None:
                    source = GltfSource('tmp/alyx/combine_grunt.glb')
                duration, frames = sample_clip(source, name)
                render(name, 'combine_grunt', duration, frames, gif)
            events = event_lookup.get(name, {}).get('events', [])
            event_text = '; '.join(f'{e["event"].replace("AE_NPC_", "")} at {e["seconds"]:.3f}s (source frame {e["frame"]})' for e in events)
            if not events:
                event_text = 'No powered-ragdoll events in the source animation.'
            r = dict(stage=stage['title'], **item, duration=duration, model='combine_grunt', gif=gif.name, powered_ragdoll_events=events)
            manifest.append(r)
            im = Image.open(gif)
            assert im.n_frames > 1
            for f in range(im.n_frames):
                im.seek(f)
                im.load()
            cards.append(f'''<article><h3>{html.escape(name)}</h3><div class="label">{duration:.3f}s | {html.escape(item['priority'])}</div>
<img loading="lazy" src="{gif.name}" alt="{html.escape(name)} front and side skeleton playblast">
<p><b>Why:</b> {html.escape(item['why'])}</p><p><b>Use:</b> {html.escape(item['use'])}</p><p><b>Watch for:</b> {html.escape(item['risk'])}</p>
<details><summary>Source physics events</summary><p>{html.escape(event_text)}</p></details><a href="{gif.name}" target="_blank">Open playblast</a></article>''')
        sections.append(f'<section id="{stage["id"]}"><h2>{html.escape(stage["title"])}</h2><p class="intro">{html.escape(stage["description"])}</p><div class="grid">'+''.join(cards)+'</div></section>')
    nav = ''.join(f'<a href="#{s["id"]}">{html.escape(s["title"])}</a>' for s in selection)
    page = '''<!doctype html><html lang="en"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>Alyx reaction shortlist</title>
<style>html{scroll-behavior:smooth}body{margin:0;background:#0c141f;color:#e6eef8;font:16px/1.55 system-ui}header,main{max-width:1480px;margin:auto;padding:26px}h1{font-size:34px;margin-bottom:6px}h2{font-size:26px}h3{font-size:18px;margin:0}header p,.intro{max-width:1000px;color:#b6c6d9}nav{display:flex;gap:12px;flex-wrap:wrap;margin-top:20px}a{color:#79ccff}nav a{padding:9px 14px;background:#20324a;border-radius:7px;text-decoration:none}.grid{display:grid;grid-template-columns:repeat(auto-fit,minmax(min(100%,440px),1fr));gap:20px}article{background:#162335;border:1px solid #293c55;border-radius:12px;padding:18px}article p{font-size:14px;margin:10px 0}.label{font-size:13px;color:#ffba8b;margin:7px 0}img{width:100%;height:auto;background:#111a24}section{margin-bottom:48px;scroll-margin-top:20px}summary{cursor:pointer;color:#b6c6d9;font-size:13px}details p{overflow-wrap:anywhere}b{color:#fff}.notice{border-left:3px solid #79ccff;padding-left:16px}</style>
<header><h1>Alyx reactions: prototype shortlist</h1><p>COUNT selected clips across four stages. Choose a stage below. Blue is left; orange is right. Front and side views play at normal speed, sampled at 20 fps with an end hold.</p>
<p class="notice"><b>These are source skeleton previews, not finished Tarkov integrations.</b> All original arm motion remains visible. Suggested torso masks, attenuation, weapon grips and procedural physics are not applied. Mild and stronger recoil stages are extraction candidates; none is certified to preserve Tarkov aiming or firing. Existing Tarkov flinches remain the baseline.</p>
<p>Stages describe our proposed use, not Valve's clip categories. Powered-ragdoll events are shown separately; the GIF continues through the full animation and does not simulate the event's physics. Directional stumbles begin mid-stride and require a gait-aware entry and exit.</p><nav>NAV</nav></header><main>SECTIONS</main></html>'''
    page = page.replace('COUNT', str(len(manifest))).replace('NAV', nav).replace('SECTIONS', ''.join(sections))
    (output / 'index.html').write_text(page, encoding='utf-8')
    (output / 'manifest.json').write_text(json.dumps(manifest, indent=2), encoding='utf-8')
    lines = ['# Alyx reaction shortlist', '', 'Raw source previews; proposed masks and Tarkov integration are not applied.', '']
    for stage in selection:
        lines.extend(['## '+stage['title'], '', stage['description'], ''])
        for item in stage['clips']:
            lines.append('- '+item['name']+': '+item['why']+' Use: '+item['use']+' Risk: '+item['risk'])
        lines.append('')
    (output / 'shortlist.md').write_text('\n'.join(lines), encoding='utf-8')
    archive = output.parent / 'alyx-curated-playblasts.zip'
    with zipfile.ZipFile(archive, 'w', zipfile.ZIP_DEFLATED) as z:
        for file in ['index.html','manifest.json','shortlist.md','selection.json'] + [r['gif'] for r in manifest]:
            z.write(output / file, 'alyx-curated/' + file)
    print(json.dumps({'clips':len(manifest),'gallery':str(output/'index.html'),'archive':str(archive),'archive_mb':round(archive.stat().st_size/1048576,1)}))


if __name__ == '__main__':
    main()
