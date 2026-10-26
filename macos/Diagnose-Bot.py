"""Read-only, redacted service diagnosis. Never prints settings or message contents."""
import pathlib
import subprocess
import re
home = pathlib.Path.home() / 'Library/Application Support/OpenCodeTelegram'
def redact(text):
    text = re.sub(r'\b\d{5,}:[A-Za-z0-9_-]{20,}', '[BOT TOKEN REDACTED]', text)
    text = re.sub(r'(?i)(authorization|password|token|api.key)(["\s:=]+)[^\s,}]+', r'\1\2[REDACTED]', text)
    return text
processes = subprocess.run(['ps', '-axo', 'pid,ppid,command'], capture_output=True, text=True).stdout
print('PROCESSES')
for line in processes.splitlines():
    if any(word in line for word in ['OpenCodeTelegram', 'opencode serve', '/dist/cli.js', 'opencode service']):
        print(redact(line))
print('APP DATA FILES')
if home.exists():
    for path in home.rglob('*'):
        if path.is_file() and len(path.relative_to(home).parts) < 4:
            print(path.relative_to(home), path.stat().st_size)
    for path in home.rglob('*.log'):
        print('ERRORS:', path.relative_to(home))
        lines = path.read_text(errors='replace').splitlines()[-300:]
        for line in lines:
            if re.search(r'(?i)error|failed|conflict|unauthoriz|409|polling|started|listening|missing', line):
                print(redact(line)[:1200])
print('PORT OWNERS')
print(subprocess.run(['lsof', '-nP', '-iTCP:4096', '-iTCP:49374', '-sTCP:LISTEN'], capture_output=True, text=True).stdout)
