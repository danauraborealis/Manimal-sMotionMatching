"""Build a replay gallery from the hit/stumble episodes that actually played."""
import argparse
import html
import json
from pathlib import Path
from render_upper_body_replay import render

def build(capture, output):
    data = json.loads(Path(capture).read_text(encoding='utf-8-sig'))
    final = {}
    for row in data['Samples']:
        if row['Stage'] in ('after_visual', 'after_lock') and row.get('Bones'):
            final[row['Frame']] = row
    rows = sorted(final.values(), key=lambda r: r['Time'])
    episodes, counts = [], {'hit': 0, 'Reaction': 0, 'landing': 0}
    previous = None
    for row in rows:
        pose = row.get('Pose', {})
        overlay = pose.get('UpperOverlay') or ''
        phase = overlay.split(':', 1)[0] if overlay else ('Reaction' if pose.get('Phase') == 'Reaction' else None)
        if phase in counts and phase != previous:
            counts[phase] += 1
            episodes.append(dict(phase=phase, occurrence=counts[phase], clip=overlay or pose.get('Clip'), time=row['Time']))
        previous = phase
    del data, final, rows
    output = Path(output)
    output.mkdir(parents=True, exist_ok=True)
    cards = []
    for index, event in enumerate(episodes):
        name = f'{index+1:02d}-{event["phase"]}.gif'
        render(capture, output/name, event['phase'], event['occurrence'])
        cards.append(f'<article><h2>{html.escape(event["clip"] or event["phase"])}</h2><img src="{name}" alt="Captured skeleton replay"><p>Capture time {event["time"]:.2f}s; includes recovery.</p></article>')
    (output/'manifest.json').write_text(json.dumps(dict(capture=str(capture), episodes=episodes), indent=2))
    (output/'index.html').write_text('''<!doctype html><meta charset="utf-8"><title>Reaction showcase replay</title>
<style>body{background:#111923;color:#e9f0f5;font:16px system-ui;margin:28px}main{display:grid;grid-template-columns:repeat(auto-fit,minmax(450px,1fr));gap:20px}article{background:#1c2938;padding:16px;border-radius:12px}img{width:100%}h2{font-size:18px}</style>
<h1>Captured reaction showcase</h1><p>Actual in-game skeleton motion, with front and side views. These previews do not show mesh deformation or the weapon.</p><main>'''+''.join(cards)+'</main>', encoding='utf-8')
    print(json.dumps(dict(gallery=str(output/'index.html'), episodes=len(episodes))))

if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('capture')
    parser.add_argument('output')
    args = parser.parse_args()
    build(args.capture, args.output)
