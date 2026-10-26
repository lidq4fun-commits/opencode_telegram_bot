"""Read-only launch diagnostics; no credentials or application configuration exported."""
import subprocess

commands = [
    ['pgrep', '-fl', 'OpenCodeTelegram'],
    ['/usr/bin/log', 'show', '--last', '2h', '--style', 'compact', '--predicate',
     '(eventMessage CONTAINS "OpenCodeTelegram" OR eventMessage CONTAINS "org.opencode.telegram.desktop") AND (eventMessage CONTAINS[c] "error" OR eventMessage CONTAINS[c] "fail" OR eventMessage CONTAINS[c] "exit" OR eventMessage CONTAINS[c] "denied")'],
]
for command in commands:
    print('\nDIAGNOSTIC:', command[0], flush=True)
    try:
        result = subprocess.run(command, capture_output=True, text=True, timeout=20)
        print(result.stdout[-18000:])
        print(result.stderr[-2000:])
    except subprocess.TimeoutExpired:
        print('Timed out; no security or accessibility settings changed.')
