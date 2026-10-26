"""Stop legacy Bot jobs without deleting their programs, settings or login plists."""
import os
import pathlib
import plistlib
import subprocess
import signal

legacy = '/Users/isabella/Developer/opencode-telegram-bot/dist/cli.js'
for file in (pathlib.Path.home() / 'Library/LaunchAgents').glob('*.plist'):
    with file.open('rb') as stream:
        item = plistlib.load(stream)
    arguments = item.get('ProgramArguments', [])
    if file.name in ['com.isabella.opencode-telegram-bot.mac.plist', 'com.isabella.opencode-telegram-bot.plist'] or any(legacy in str(argument) for argument in arguments):
        result = subprocess.run(['launchctl', 'bootout', f'gui/{os.getuid()}', str(file)], capture_output=True, text=True)
        print('Stopped legacy login job:', file.name, 'result:', result.returncode)
processes = subprocess.run(['ps', '-axo', 'pid,command'], capture_output=True, text=True).stdout
for line in processes.splitlines():
    fields = line.strip().split(None, 1)
    if len(fields) == 2 and legacy in fields[1] and 'node' in fields[1].split()[0]:
        try:
            os.kill(int(fields[0]), signal.SIGTERM)
            print('Stopped legacy Bot PID:', fields[0])
        except ProcessLookupError:
            pass
print('Legacy files and settings preserved. Login jobs may resume at next login.')
