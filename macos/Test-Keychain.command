#!/bin/bash
set -uo pipefail
cd /Users/isabella/OpenCodeTelegram-build/native-swift
swift test --filter testRealLoginKeychainInsertUpdateRead > keychain-test.log 2>&1
result=$?
cat keychain-test.log
echo "$result" > keychain-test.exit
exit "$result"
