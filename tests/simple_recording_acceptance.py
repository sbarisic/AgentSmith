"""Live requested-analysis acceptance. Uses an existing controlled capture, never records."""
import argparse
import json
import os
from pathlib import Path
import queue
import subprocess
import threading
import time


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--smith', required=True)
    parser.add_argument('--data-dir', required=True)
    parser.add_argument('--capture', required=True)
    parser.add_argument('--output', required=True)
    parser.add_argument('--direct', action='store_true')
    parser.add_argument('--prompt', default='Summarize that recording with timestamp citations.')
    args = parser.parse_args()
    output = Path(args.output); output.mkdir(parents=True, exist_ok=True)
    data = Path(args.data_dir)
    captures = sorted((json.loads(p.read_text(encoding='utf-8')) for p in (data/'audio/recordings').glob('*/manifest.json')), key=lambda m: (m['Started'],m['Id']), reverse=True)[:20]
    selection = next(i + 1 for i, m in enumerate(captures) if m['Id'] == args.capture)
    command = [args.smith, 'chat', '--data-dir', args.data_dir, '--workspace', str(Path.cwd()), '--delegation', 'manual', '--model', 'gpt-6-astra', '--provider', 'ChatGpt', '--json']
    child = subprocess.Popen(command, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True, encoding='utf-8', creationflags=subprocess.CREATE_NO_WINDOW)
    events = queue.Queue()
    def read():
        for line in child.stdout: events.put(line)
        events.put(None)
    threading.Thread(target=read, daemon=True).start()
    def send(text): child.stdin.write(text+'\n'); child.stdin.flush()
    started = time.monotonic(); rows = []; selected = False; asked = False; exited = False
    try:
        with (output/'chat.ndjson').open('w', encoding='utf-8') as log:
            while time.monotonic()-started < 900:
                try: line = events.get(timeout=15)
                except queue.Empty: continue
                if line is None: break
                log.write(line); log.flush()
                if not line.startswith('{'): continue
                ev = json.loads(line); rows.append(ev)
                if ev['type'] == 'conversation' and not selected:
                    send('/audio recordings'); selected = True
                elif ev['type'] == 'audio.selection' and 'Enter a number' in ev['payload'].get('message',''):
                    send(str(selection))
                elif ev['type'] == 'audio.selection' and ev['payload'].get('captureId') == args.capture and not asked:
                    send('/audio summarize' if args.direct else args.prompt); asked = True
                elif args.direct and ev['type'] == 'audio.analysis' and ev['payload'].get('result',{}).get('summary'):
                    send('/exit'); exited = True
                elif ev['type'] == 'question.pending':
                    raise AssertionError('Selected capture unexpectedly asked another question: '+line)
                elif ev['type'] == 'run.outcome' and not exited:
                    assert ev['payload']['state'] == 'Completed', ev
                    send('/exit'); exited = True
        code = child.wait(timeout=20)
        assert code == 0 and asked and exited, (code, asked, exited)
        tools = [e['payload'] for e in rows if e['type'] == 'tool.completed']
        analyses = [json.loads(e['content']) for e in tools if e['name'] == 'audio_analyze']
        if args.direct: analyses = [e['payload']['result'] for e in rows if e['type']=='audio.analysis' and e['payload'].get('result',{}).get('summary')]
        assert analyses and all(a.get('artifactId') for a in analyses), tools
        metrics = dict(seconds=time.monotonic()-started, modelRequests=sum(e['type']=='model.requested' for e in rows), tools=[t['name'] for t in tools], results=analyses,
                       conversationId=next(e['payload'].get('id') for e in rows if e['type']=='conversation'))
        (output/'metrics.json').write_text(json.dumps(metrics,indent=2),encoding='utf-8')
        print(json.dumps({k:v for k,v in metrics.items() if k!='results'}))
    finally:
        if child.poll() is None:
            child.terminate(); child.wait(timeout=20)


if __name__ == '__main__': main()
