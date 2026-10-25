"""Real Windows console Ctrl+C acceptance against a local scripted provider; no cloud requests."""
import argparse
import ctypes as c
from ctypes import wintypes as w
from http.server import ThreadingHTTPServer
import json
import os
from pathlib import Path
import subprocess
import threading
import time
from audio_service_cli_smoke import Script

class QuestionScript(Script):
    def do_POST(self):
        self.rfile.read(int(self.headers['Content-Length']))
        delta={'tool_calls':[{'index':0,'id':'exit-question','type':'function','function':{'name':'user_ask','arguments':json.dumps({'question':'Keep this pending?', 'choices':['one','two']})}}]}
        chunk={'choices':[{'index':0,'delta':delta,'finish_reason':'tool_calls'}]}
        self.reply('data: '+json.dumps(chunk)+'\n\ndata: [DONE]\n\n','text/event-stream')

class Coord(c.Structure): _fields_ = [('x', w.SHORT), ('y', w.SHORT)]
class Rect(c.Structure): _fields_ = [('left',w.SHORT),('top',w.SHORT),('right',w.SHORT),('bottom',w.SHORT)]
class Info(c.Structure): _fields_ = [('size',Coord),('cursor',Coord),('attributes',w.WORD),('window',Rect),('maximum',Coord)]
class Key(c.Structure): _fields_ = [('down',w.BOOL),('repeat',w.WORD),('virtual',w.WORD),('scan',w.WORD),('character',w.WCHAR),('control',w.DWORD)]
class Event(c.Union): _fields_ = [('key',Key),('padding',c.c_byte*16)]
class Input(c.Structure): _fields_ = [('kind',w.WORD),('event',Event)]

def main():
    parser=argparse.ArgumentParser(); parser.add_argument('--smith',required=True); parser.add_argument('--output',required=True); parser.add_argument('--question',action='store_true'); args=parser.parse_args()
    out=Path(args.output); out.mkdir(parents=True,exist_ok=True)
    server=ThreadingHTTPServer(('127.0.0.1',0),QuestionScript if args.question else Script)
    threading.Thread(target=server.serve_forever,daemon=True).start()
    startup=subprocess.STARTUPINFO(); startup.dwFlags=subprocess.STARTF_USESHOWWINDOW; startup.wShowWindow=0
    environment = dict(os.environ); environment.pop('TERM', None)
    child=subprocess.Popen([args.smith,'chat','--provider','local','--endpoint',f'http://127.0.0.1:{server.server_port}/v1',
        '--model','scripted-audio-acceptance','--workspace',str(out),'--data-dir',str(out/'console-state'),
        '--delegation','manual','--no-color'],creationflags=subprocess.CREATE_NEW_CONSOLE,startupinfo=startup,env=environment)
    kernel=c.WinDLL('kernel32',use_last_error=True)
    kernel.CreateFileW.argtypes=[w.LPCWSTR,w.DWORD,w.DWORD,c.c_void_p,w.DWORD,w.DWORD,w.HANDLE]; kernel.CreateFileW.restype=w.HANDLE
    kernel.ReadConsoleOutputCharacterW.argtypes=[w.HANDLE,w.LPWSTR,w.DWORD,Coord,c.POINTER(w.DWORD)]
    kernel.GetConsoleScreenBufferInfo.argtypes=[w.HANDLE,c.POINTER(Info)]
    kernel.WriteConsoleInputW.argtypes=[w.HANDLE,c.POINTER(Input),w.DWORD,c.POINTER(w.DWORD)]
    kernel.FreeConsole()
    for attempt in range(100):
        if kernel.AttachConsole(child.pid): break
        if child.poll() is not None: raise AssertionError(f'Console process exited: {child.returncode}')
        time.sleep(.05)
    else:
        child.terminate(); child.wait(timeout=10)
        raise AssertionError(f'AttachConsole failed: {c.get_last_error()}')
    kernel.SetConsoleCtrlHandler(None,True)
    output=kernel.CreateFileW('CONOUT$',0xC0000000,3,None,3,0,None)
    input_handle=kernel.CreateFileW('CONIN$',0xC0000000,3,None,3,0,None)
    kernel.GetConsoleMode.argtypes=[w.HANDLE,c.POINTER(w.DWORD)]; kernel.SetConsoleMode.argtypes=[w.HANDLE,w.DWORD]
    mode=w.DWORD(); assert kernel.GetConsoleMode(output,c.byref(mode)); assert kernel.SetConsoleMode(output,mode.value|4)
    def screen():
        info=Info(); assert kernel.GetConsoleScreenBufferInfo(output,c.byref(info)),c.get_last_error()
        length=info.size.x*min(info.size.y,info.cursor.y+3); buffer=c.create_unicode_buffer(length+1); read=w.DWORD()
        assert kernel.ReadConsoleOutputCharacterW(output,buffer,length,Coord(0,0),c.byref(read)),c.get_last_error()
        return buffer.value
    def keys(text):
        for char in text:
            event=Input(); event.kind=1; event.event.key=Key(True,1,13 if char=='\r' else 8 if char=='\b' else ord(char.upper()),0,char,0)
            written=w.DWORD(); assert kernel.WriteConsoleInputW(input_handle,c.byref(event),1,c.byref(written)),c.get_last_error()
    evidence={}
    try:
        deadline=time.monotonic()+30
        while 'Enter send' not in screen():
            if child.poll() is not None or time.monotonic()>deadline: raise AssertionError(screen())
            time.sleep(.1)
        for _ in range(3):
            assert kernel.GenerateConsoleCtrlEvent(0,0); time.sleep(.2); assert child.poll() is None
        evidence['idleRepeatedCtrlCSurvived']=True
        keys('kept draft'); time.sleep(.3)
        assert kernel.GenerateConsoleCtrlEvent(0,0); time.sleep(.3)
        assert child.poll() is None and 'kept draft' not in screen()
        evidence['draftCleared']=True
        if args.question:
            keys('ask\r'); deadline=time.monotonic()+15
            while 'Choose an option' not in screen():
                assert child.poll() is None and time.monotonic()<deadline,screen()
                time.sleep(.1)
            keys('\r'); keys('note kept'); time.sleep(.3)
            assert kernel.GenerateConsoleCtrlEvent(0,0); time.sleep(.3)
            assert child.poll() is None and 'note kept' in screen()
            keys('\x1b'); time.sleep(.3); keys('do not submit\r'); time.sleep(.3)
            assert 'No answer was submitted' in screen()
            keys('\r'); time.sleep(1); assert 'note kept' in screen(), screen()
            evidence['pickerDraftAndCommandReturn']=True
            keys('\x1b'); time.sleep(.3)
        keys('/exit\r'); time.sleep(.3); keys('\r') # completion, then explicit submission
        code=child.wait(timeout=15); assert code==(3 if args.question else 0),code
        evidence['explicitExitCode']=code
        (out/('console-question-exit.json' if args.question else 'console-exit.json')).write_text(json.dumps(evidence,indent=2))
    finally:
        if child.poll() is None: child.terminate(); child.wait(timeout=10)
        kernel.FreeConsole(); server.shutdown()
    print(json.dumps(evidence))

if __name__=='__main__': main()
