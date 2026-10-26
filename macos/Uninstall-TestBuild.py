"""Remove only this migration app; preserve existing Bot installations and settings."""
import pathlib
import plistlib
import shutil
import subprocess
import os

home = pathlib.Path.home()
for kind in ('app', 'bot'):
    agent = home / 'Library/LaunchAgents' / f'org.opencode.telegram.{kind}.plist'
    if agent.exists():
        subprocess.run(['launchctl', 'bootout', f'gui/{os.getuid()}', str(agent)], check=False)
        agent.unlink()
        print('Removed login item:', agent)
for app in (home / 'Applications/OpenCodeTelegram.app', pathlib.Path('/Applications/OpenCodeTelegram.app')):
    if not app.exists():
        continue
    with (app / 'Contents/Info.plist').open('rb') as stream:
        identifier = plistlib.load(stream).get('CFBundleIdentifier')
    if identifier != 'org.opencode.telegram.desktop':
        raise RuntimeError(f'Refusing to remove unrelated app: {app}')
    shutil.rmtree(app)
    print('Uninstalled:', app)
print('Preserved settings, logs, build artifacts and pre-existing Bot installations.')
