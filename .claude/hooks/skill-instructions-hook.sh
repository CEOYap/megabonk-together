#!/bin/bash
# UserPromptSubmit hook for skill-aware responses

cat <<'EOF'
REQUIRED: SKILL LOADING PROTOCOL

Before writing any code, complete these steps in order:

1. SCAN each skill below and decide: LOAD or SKIP (with brief reason)
   - csharp             (MegabonkTogether conventions: DI services, naming, file layout)
   - il2cpp             (Il2CppInterop: ClassInjector, Il2CppSystem types, delegates, casting)
   - harmony            (patching Megabonk's Assembly-CSharp)
   - bepinex            (BasePlugin lifecycle, ModConfig, logging)
   - unity              (MonoBehaviour, coroutines, pooling, main-thread rules)
   - netcode            (LiteNetLib delivery, MemoryPack messages, host authority)
   - build-and-release  (solution layout, MegabonkPath, Thunderstore/Proton, CI)

2. For every skill marked LOAD -> immediately invoke Skill(name)
   If none need loading -> write "Proceeding without skills"

3. Only after step 2 completes may you begin coding.

IMPORTANT: Skipping step 2 invalidates step 1. Always call Skill() for relevant items.

Sample output:
- csharp: LOAD - adding a new sync service
- il2cpp: LOAD - touching Il2CppSystem collections
- harmony: SKIP - no game methods patched
- bepinex: SKIP - no plugin lifecycle change
- unity: SKIP - no MonoBehaviour work
- netcode: LOAD - adding a new IGameNetworkMessage
- build-and-release: SKIP - no build change

Then call:
> Skill(csharp)
> Skill(il2cpp)
> Skill(netcode)

Now implementation can begin.
EOF
