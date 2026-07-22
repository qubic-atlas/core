#!/bin/bash
# Build the annexplorer decentralized verifier from the (patched) Qubic Core scorer.
set -e
clang++ -std=c++17 -O2 -mavx2 -mbmi -mbmi2 -mlzcnt -mpopcnt -mrdrnd -fshort-wchar \
  -DNO_UEFI -DNDEBUG -w \
  -I./coretree/src -I./coretree \
  -I./coretree/lib/platform_common -I./coretree/lib/platform_os -I./coretree/lib/platform_efi \
  verifier.cpp -o verifier
echo "built ./verifier"
