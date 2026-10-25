"""Explicit short silent recordings verify published process lifetime and crash recovery."""
import argparse
from http.server import ThreadingHTTPServer
import json
import os
from pathlib import Path
import signal
import subprocess
import threading
import time
from audio_service_cli_smoke import Script

def main():
    p=argparse.ArgumentParser(); p.add_argument('--smith',required=True); p.add_argument('--data-dir',required=True); p.add_argument('--output',required=True); args=p.parse_args()
    def command(*words):
        r=subprocess.run([args.smith,*words,'--data-dir',args.data_dir,'--json'],capture_output=True,text=True,encoding='utf-8',timeout=40,creationflags=subprocess.CREATE_NO_WINDOW)
        assert r.returncode==0,r.stdout+r.stderr
        return json.loads(r.stdout.strip().splitlines()[-1])['payload']
    report={}
    command('service','start')
    first=command('audio','capture','start'); time.sleep(2)
    pid=command('service','status')['processId']
    os.kill(pid,signal.SIGTERM) # Only this test's dedicated service, identified through its own data-directory endpoint.
    time.sleep(.3); command('service','start')
    recovered=command('audio','capture','status',first['Id']); assert recovered['State']=='Interrupted',recovered
    assert command('service','status')['capture'] is None
    report['crashRecovery']=recovered
    for force in (False,True):
        capture=command('audio','capture','start'); time.sleep(1.2)
        command('service','stop',*(['--force'] if force else [])); command('service','start')
        final=command('audio','capture','status',capture['Id'])
        assert final['State']==('Interrupted' if force else 'Completed'),final
        report['forcedStop' if force else 'gracefulStop']=final
    assert not (Path(args.data_dir)/'sessions.db').exists(),'Terminal capture initialized conversation storage'
    server=ThreadingHTTPServer(('127.0.0.1',0),Script); threading.Thread(target=server.serve_forever,daemon=True).start()
    try:
        chat=subprocess.run([args.smith,'chat','--data-dir',args.data_dir,'--json','--provider','local','--endpoint',f'http://127.0.0.1:{server.server_port}/v1',
            '--model','scripted-audio-acceptance','--workspace',str(Path(args.output).resolve()),'--delegation','manual'],
            input='/audio capture start\n/exit\n',capture_output=True,text=True,encoding='utf-8',timeout=30,creationflags=subprocess.CREATE_NO_WINDOW)
        assert chat.returncode==0,chat.stdout+chat.stderr
        events=[json.loads(line) for line in chat.stdout.splitlines()]
        capture=next(e['payload']['result'] for e in events if e['type']=='audio.capture' and 'result' in e['payload'])
        assert command('audio','capture','status',capture['Id'])['State']=='Recording'
        assert any('remains active' in e.get('payload',{}).get('message','') for e in events)
        report['chatExitSurvival']=command('audio','capture','stop',capture['Id'])
        import sqlite3
        with sqlite3.connect(Path(args.data_dir)/'sessions.db') as db:
            assert db.execute('select count(*) from capture_references where capture_id=?',(capture['Id'],)).fetchone()[0]==1
    finally: server.shutdown()
    assert command('service','status')['capture'] is None
    out=Path(args.output); out.mkdir(parents=True,exist_ok=True); (out/'process-capture.json').write_text(json.dumps(report,indent=2))
    print(json.dumps({'crashRecovery':True,'normalStop':True,'forceStop':True,'chatExitSurvival':True,'conversationReference':True}))

if __name__=='__main__': main()
