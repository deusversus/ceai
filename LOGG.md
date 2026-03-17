# [Script] "EXP Multiplier" — disabled (ID: ct-20000…

*Exported 2026-03-17 17:34*

---

### **You** — 3:48 PM

[Script] "EXP Multiplier" — disabled (ID: ct-20000)

### **AI Operator** — 3:48 PM

I checked the script source and validated it.

What I found:
- Script ID: `ct-20000`
- Name: `EXP Multiplier`
- Status: disabled
- Validation: passed

Script behavior:
- Hook point: `GameAssembly.dll+0x9A18E8`
- It multiplies positive `ebx` EXP rewards by `4`
- Then it writes the result with:
  - `mov [rsi+14], ebx`

Important note:
- The script itself is syntactically valid.
- My attempt to disassemble the hook address via symbolic form failed, so I haven’t yet verified whether this hook still matches the live game code.
- That means the next safe step is to inspect the actual live bytes at `GameAssembly.dll+0x9A18E8` and confirm the assert still matches before enabling.

If you want, I can now:
1. inspect the live hook bytes,
2. verify the hook is still correct,
3. then enable the script safely if it checks out.

### **You** — 3:48 PM

[Script] "JP Multiplier" — disabled (ID: ct-20001)

### **AI Operator** — 3:48 PM

I checked `JP Multiplier` (`ct-20001`).

Findings:
- Status: disabled
- Type: Auto Assembler
- Validation: passed

What it does:
- Hook point: `GameAssembly.dll+0x99762B`
- Multiplies positive JP gain by `4`
- Writes modified JP to:
  - `[rbx+24]`
- Also preserves related JP values at:
  - `[rbx+0xC8]`
  - `[rbx+0xCC]`

So:
- The script is syntactically valid.
- I have not yet verified the live hook bytes at `GameAssembly.dll+0x99762B`, so I can’t confirm it still matches the current game build until I inspect memory.

Next safe step:
- verify the live bytes/assert at the hook,
- then enable it if the hook still matches.

If you want, I can do that now for `ct-20001`.

### **You** — 3:48 PM

[Module] GameAssembly.dll @ 0x7FF87EE80000 (size: 380,452,864 bytes)

### **AI Operator** — 3:48 PM

Verified both live hook sites against the scripts.

Results:
- `JP Multiplier` hook at `GameAssembly.dll+0x99762B`
  - Resolved: `0x7FF87F81762B`
  - Live bytes match the script’s `assert`
  - Hook looks valid for this build

- `EXP Multiplier` hook at `GameAssembly.dll+0x9A18E8`
  - Resolved: `0x7FF87F8218E8`
  - Live bytes match the script’s `assert`
  - Hook also looks valid

So both scripts appear compatible with the currently loaded `GameAssembly.dll`.

If you want, I can now safely:
- enable `JP Multiplier` (`ct-20001`), or
- enable both multiplier scripts one at a time and then have you test them in battle.

### **You** — 3:50 PM

Alright so both of these scripts hook too late. They don't effect the information shown on screen, and in fact it's SO late, the exp isn't granted until after the battle is over; so leveling up, for example, is delayed until the NEXT battle. The game has built-in bonuses for EXP and JP, so if we search up the reward chain, we might be able to hook into those instead.

### **AI Operator** — 3:50 PM

Understood. The current hooks are post-calculation/post-display, so they only alter the stored reward after the battle result flow has already used the original values.

Best next move:
- trace where EXP/JP are computed earlier,
- ideally at the same place the game applies its own reward bonus modifiers,
- then hook there instead of the final writeback.

To do that efficiently, I need one concrete reward value to anchor on.

Please do this:
1. Finish a battle with both scripts still disabled.
2. Note the exact:
   - EXP gained
   - JP gained
3. Tell me those two numbers.

Once you give me the exact reward values, I’ll:
- scan for the live reward values,
- narrow to the result structure,
- set write/access breakpoints on the correct addresses,
- trace backward to the earlier calculation code,
- and build a better hook at the bonus-calculation stage.

### **You** — 3:52 PM

7exp and 3jp (12pg --the currency)

### **AI Operator** — 3:52 PM

Good, we have anchor values:
- EXP: `7`
- JP: `3`
- PG: `12`

I started with exact scans, but there are far too many raw matches to identify the reward structure directly from values alone.

Next step needs an in-game state change so I can refine intelligently.

Please do this:
1. Fight another battle with scripts still disabled.
2. Tell me the new exact rewards for:
   - EXP
   - JP
   - PG

Even if only one of them changes, that’s useful.

Once you give me the next set, I’ll refine the scans and try to isolate the battle result values so I can trace the earlier calculation path.

### **You** — 3:53 PM

6pg 4exp and 2jp (10% up on exp and 10% up on jp)

### **AI Operator** — 3:54 PM

Good — that confirms the game is applying built-in bonuses before the final displayed reward:
- Base-looking rewards changed to:
  - EXP `4`
  - JP `2`
  - PG `6`
- Displayed/awarded rewards became:
  - EXP `4`? / with your note indicating 10% bonus logic is involved
  - JP `2`
  - PG `6`

What matters here is:
- EXP and JP bonus logic exists upstream.
- The current scripts are definitely too late because they patch the post-result storage.

I’ve narrowed one reward scan from `50,000` down to `1,328`, but that’s still too broad. To isolate the actual result structure, I need one more controlled change.

Please do one more battle and tell me the exact final rewards again:
- EXP
- JP
- PG

Ideally:
- same area/enemy type if possible,
- with the same 10% bonus setup still active.

That gives me a third data point so I can refine toward the live result fields and then trace the earlier bonus-calculation code.

### **You** — 3:55 PM

back to 12pg, 7exp, and 3jp (with the same 10% bonus to both EXP and JP again)

### **AI Operator** — 3:56 PM

We have enough to conclude the value-scan approach alone is not the right path here:

- Re-scanning exact reward values still produces `50,000` matches for each of:
  - EXP `7`
  - JP `3`
  - PG `12`

That means the reward values are too common to isolate reliably without a better anchor.

Since your goal is specifically to hook earlier in the reward chain, the better route now is code tracing, not more blind value scans.

Next step I want to take:
- identify the existing late write instructions,
- then trace backward from those routines to the earlier reward calculation / bonus application code.

I already know the late hooks:
- EXP final write around `GameAssembly.dll+0x9A18E8`
- JP final write around `GameAssembly.dll+0x99762B`

To move upstream properly, I need to catch those routines during a battle result event and inspect the surrounding execution path.

Please do this:
1. Start a battle and get to the point just before the reward/result screen appears.
2. Tell me when you are there with: `ready`

As soon as you say `ready`, I’ll set execution breakpoints on the reward routines and have you finish the battle so I can trace the earlier calculation path.

### **You** — 3:58 PM

down to one enemy now; i cant stop you from not seeing me taking the last swing (because this is the last pause before the end of th ematch)

### **AI Operator** — 3:58 PM

I hit a debugger limitation:

- EXP breakpoint set successfully at `0x7FF87F8218E8`
  - Breakpoint ID: `561314970ff84890b6f434117da33074`
- JP breakpoint failed because the debugger could not attach to the process

I will not keep retrying the same failing operation right now.

Please finish the battle now.

After the result sequence happens, tell me `done`, and I’ll pull the breakpoint hit log from the EXP routine to see what thread/path executed. If we get useful context from that, I can decide the next upstream trace step without forcing another debugger attach attempt.

### **You** — 4:17 PM

sorry, that crashed both your container (CEAI Suite app Im building) and the game. Fixed the error (I think) so let's try that again. I'm on the world map now, let me go find a battle.

### **AI Operator** — 4:17 PM

Reattached to `BDFFHD` (`PID 1412`) successfully.

Given the earlier crash, I’m going to be more conservative this time:
- no dual breakpoints at once,
- start with a single breakpoint,
- and prefer logging over intrusive breaking.

Please go enter a battle and get to the point just before the final hit.

When you’re there, say:
- `ready exp` if you want me to trace EXP first, or
- `ready jp` if you want me to trace JP first.

I recommend `ready exp` first.

### **You** — 4:18 PM

alright lets start with exp

### **AI Operator** — 4:18 PM

EXP trace breakpoint is set.

Details:
- Address: `0x7FF87F8218E8`
- Type: `HardwareExecute`
- Action: `LogAndContinue`
- Breakpoint ID: `bd0377bc09da45aea4d3de6d43590942`

Please finish the battle now.

When the reward/result sequence has completed, tell me:
- `done`

Then I’ll pull the hit log and trace upward from there.

### **You** — 4:19 PM

any idea why setting the breakpoint froze the game?

### **AI Operator** — 4:19 PM

Yes — most likely because even a `LogAndContinue` execute breakpoint still forces the debugger path to engage, and this game/toolchain appears sensitive to that.

Most probable causes:
- The game is anti-debug fragile, even without full anti-cheat.
- The hook site may execute on a hot path or under timing-sensitive code.
- Your CEAI Suite debugger bridge may still suspend/resume threads briefly when arming hardware breakpoints.
- If the process is multithreaded around battle transitions, a debugger attach or context update can stall the main thread long enough to look like a freeze.
- Hardware breakpoints are per-thread at the CPU level; depending on your implementation, applying them may involve thread enumeration/suspension, which can hitch or freeze the game.

Important implication:
- Traditional breakpoint tracing is probably too unstable for this target right now.

Better approach now:
1. avoid breakpoints entirely,
2. use static analysis around the known late EXP/JP write routines,
3. walk nearby code and call stacks by disassembly/signatures,
4. identify earlier calculation code by pattern rather than live debugger tracing.

So instead of trapping execution, I should:
- disassemble around the EXP late hook at `0x7FF87F8218E8`,
- inspect a wider region for nearby arithmetic/bonus logic,
- look for calls/jumps feeding the final write,
- and then build a new earlier hook candidate from static code flow.

If you want, I’ll switch to that safer no-breakpoint approach now.

### **You** — 4:53 PM

okay, i want you to review the new breakpoint tools i just developed, just for you. do you see them? :)

### **AI Operator** — 4:54 PM

Yes — I see the expanded breakpoint/hook tooling now.

New breakpoint-related capabilities visible to me:
- `SetBreakpoint` with modes:
  - `Auto`
  - `Stealth`
  - `PageGuard`
  - `Hardware`
  - `Software`
- Breakpoint types:
  - `Software`
  - `HardwareExecute`
  - `HardwareWrite`
  - `HardwareReadWrite`
- Hit actions:
  - `Break`
  - `Log`
  - `LogAndContinue`
- Breakpoint management:
  - `ListBreakpoints`
  - `RemoveBreakpoint`
  - `GetBreakpointHitLog`

And importantly, I also see the stealth/non-debugger path you added:
- `InstallCodeCaveHook`
- `RemoveCodeCaveHook`
- `ListCodeCaveHooks`
- `GetCodeCaveHookHits`

That’s the big upgrade. For a game that freezes or crashes under normal debugger attachment, the stealth code cave hook path is exactly what I’d want to use instead of hardware/software breakpoints.

So yes: I see them, and they materially change how I’d approach this target.

For this game specifically, my new preferred tracing order would be:
1. `Stealth` breakpoint mode first, or directly
2. `InstallCodeCaveHook` on the late EXP/JP routines
3. trigger battle result
4. inspect captured register snapshots with `GetCodeCaveHookHits`
5. trace backward statically from the captured context

One note:
- the current context tool just reported `No process attached`, even though your injected state says `BDFFHD (PID 27712)` is attached.
- So I can see the tools, but the live backend context looks slightly out of sync right now.

If you want, I can help you test-drive the new stealth breakpoint/code-cave workflow on the EXP routine next.

### **You** — 5:00 PM

sorry, another quick restart. how about now; in sync?

### **AI Operator** — 5:00 PM

Yep — now it’s in sync.

Current context:
- Attached: `BDFFHD`
- PID: `27712`
- Arch: `x64`
- Modules: `50`
- Address table: `1454` entries
- Frozen: `0`
- Scripts: `3`
- Active scan: none

So the backend context is matching the injected state now.

And yes, I do see your new breakpoint/hook tooling. The most useful additions for this game are:
- `SetBreakpoint` with `Stealth` / `PageGuard` / `Hardware` / `Software`
- `InstallCodeCaveHook`
- `GetCodeCaveHookHits`

For this target, I’d avoid normal hardware execute breakpoints first and use:
- stealth/code-cave tracing on the known late EXP routine,
- then inspect captured registers after a battle result.

If you want, we can test your new tools safely by placing a stealth hook on the late EXP site instead of using the debugger-style breakpoint that froze the game.

### **You** — 5:01 PM

Sure, tell me what you need from me.

### **AI Operator** — 5:01 PM

Perfect.

I’ll use the safer stealth path, so I just need you to set up the in-game timing.

What I need from you:
1. Go into a battle.
2. Get to the point where one hit will end the fight.
3. Stop there and tell me:
   - `ready exp`

Once you say that, I’ll install a stealth hook on the known late EXP routine.

Then I’ll ask you to:
4. land the final hit,
5. let the reward/result sequence happen,
6. and tell me:
   - `done`

After that I’ll read the captured hook hits and use the register context to trace the earlier EXP calculation path.

Important:
- Leave the multiplier scripts disabled.
- Don’t change equipment/passives/bonuses during this test.
- If the game freezes or closes, tell me exactly when it happened:
  - on hook install,
  - on final hit,
  - or on result screen.

### **You** — 5:02 PM

ready exp

### **AI Operator** — 5:02 PM

Stealth EXP hook installed successfully.

Details:
- Hook ID: `4d584333742b4c9183ad4174fa98d661`
- Target: `0x7FF87F8218E8`
- Cave: `0x1931DC50000`

This is the safer path:
- no debugger attached,
- register capture enabled.

Please finish the battle now and let the reward sequence complete.

Then tell me:
- `done`

If anything unusual happens, tell me exactly when:
- on final hit,
- during transition,
- or on result screen.

### **You** — 5:02 PM

done

### **AI Operator** — 5:03 PM

The stealth hook was safe, but it never fired:

- Hook ID: `4d584333742b4c9183ad4174fa98d661`
- Target: `0x7FF87F8218E8`
- Result: no hits recorded

That strongly suggests one of these:
- the old EXP hook address is no longer the path used in this battle flow,
- the instruction executes in a different code path than expected,
- or the late EXP script’s hook point is valid code but not the reward path we just triggered.

So this is actually useful: it tells us the current “late EXP” site is probably not the right runtime path to trace from.

Next best move:
- inspect the existing `EXP Multiplier` script source again,
- verify the exact hook logic,
- then do the same for `JP Multiplier`,
- and identify whether those scripts are based on alternate reward routines or stale assumptions.

After that, I’ll pick a better live candidate for stealth tracing.

If you want, I’ll do that now and extract the exact instructions/bytes each script is targeting.

### **You** — 5:04 PM

the currently loaded cheat table has the character's total exp value, which we used to find the exp multiplier script area in the first place. it's populated with a value, 303 atm

### **AI Operator** — 5:04 PM

Yes — that’s a much better anchor.

From the table output, I can see a character structure entry with:
- `Level: +34 = 6`
- `EXP: +38 = 303 (Int32)`

That strongly suggests:
- the character base structure is already in the table,
- total character EXP is stored at offset `0x38`,
- and the reward routine likely updates this field directly or through a nearby function.

I tried to query the node directly by label, but your current API wants the actual node ID, not the visible label, so I can’t target that entry from the list output alone yet.

What I want to do next:
- identify the exact live address of that `EXP +0x38 = 303` field,
- then use your new stealth tracing on the character EXP value itself,
- not on the old late script hook.

That should be much better because:
- the total EXP field is specific,
- it changes only when EXP is awarded,
- and tracing what writes to it should lead us to the real award application path.

What I need from you:
- either tell me the exact address/node ID of that `EXP: +38 = 303` entry,
- or, if your UI shows the parent/base pointer for that character structure, give me that base address.

Best case:
- if the structure base is, say, `0x...`, then the EXP field is `base + 0x38`.

Once I have the exact live address for the `303` EXP field, I’ll:
1. place a stealth write trace on that value path,
2. have you win one battle,
3. inspect the captured context,
4. and trace backward to the earlier reward/bonus calculation code.

### **You** — 5:11 PM

i fixed the label problem instead; now they resolve by id first then label fallback

### **AI Operator** — 5:11 PM

Yes — label fallback is working now.

I tested it against the EXP entries by ID, and the lookup succeeds. One thing I noticed though:

- the entries resolve,
- but they currently only report relative addresses like `+38`,
- not the fully resolved live pointer/base address,
- and the values are `?` right now.

So the label/ID fix is good, but for tracing writes I still need the actual live resolved address of the EXP field, not just the structure offset.

What I learned from the table:
- There are 4 party-member EXP entries:
  - `432574db`
  - `ded06d7e`
  - `fcffd04a`
  - `a4975e14`

What I need next:
- refresh the table so those entries populate live values and, ideally, resolved addresses.

If your backend is attached and stable now, the next safe step is:
1. refresh the address table,
2. inspect one EXP node again,
3. if it resolves to a real address, I can trace that exact EXP field with a stealth write hook.

Right now I can’t proceed to the hook because:
- current context says no process attached,
- and the EXP nodes are unresolved (`?`).

Once the process is attached again, I’ll:
- refresh the table,
- identify which EXP entry is the `303` one,
- and use that as the new tracing anchor.

### **You** — 5:13 PM

yeah, i tried to use the save/resume session feature but its stale; so i loaded it up manually. you should be good to go, sorry

### **AI Operator** — 5:13 PM

Nice — we have a solid anchor now.

I resolved the live EXP field:
- Table entry: `EXP`
- Node ID: `ct-75`
- Resolved address: `0x19317FAE678`
- Current value: `309`

I’ve set a stealth write breakpoint on that exact EXP value:
- Breakpoint ID: `c31a47722e8c46778a0464e4b6570725`
- Address: `0x19317FAE678`
- Type: write
- Mode: `Stealth`

This is the right test now: trace what writes the character’s total EXP directly, instead of the old late reward hook.

Please do this:
1. Enter a battle.
2. Finish it normally.
3. After the reward/result sequence completes, tell me:
   - `done`

If anything goes wrong, tell me exactly when:
- on battle start,
- on final hit,
- on result screen,
- or after returning to map.

### **You** — 5:21 PM

that froze the game, so frozen i cant even get task manager to end th etask on the game

### **AI Operator** — 5:21 PM

Got it. That means your current `Stealth` write-breakpoint implementation on a data address is still too intrusive or has a bug path.

Most likely:
- the stealth mode for data write breakpoints is internally doing something heavier than expected,
- or the write trap on a frequently touched page is stalling the process,
- or the engine is not yet safe for write-watch style stealth on live data.

Important conclusion:
- we should stop using breakpoint-style tracing on this target for now.
- I will not retry the same operation again immediately.

What we learned:
- `Stealth` code hook on code address was safe.
- `Stealth` write breakpoint on data address froze the game.
- So your new tooling is currently safer for code interception than for data-write monitoring.

My review of the new tools so far:
- `InstallCodeCaveHook` looks promising and was stable in practice.
- `SetBreakpoint(... mode=Stealth)` on a data write target appears unsafe right now.
- The API shape is good, but implementation-wise I’d currently trust:
  - code cave hooks on executable addresses
  - more than stealth write breakpoints on data pages

Recommended next direction for your tool development:
1. distinguish clearly between:
   - stealth execute hook
   - stealth data write watch
2. expose whether stealth write mode is implemented via:
   - page protection,
   - VEH/page guard,
   - code patching,
   - or polling
3. add a timeout/failsafe auto-remove if the target thread stops responding after hook install
4. add a “single-hit then auto-disable” option for risky breakpoints
5. log whether the hook target is code or data and reject unsupported unsafe combinations early

For the game-hacking goal, once you restart:
- I recommend we avoid data breakpoints entirely,
- and instead use the resolved EXP field to find the code path indirectly:
  - inspect nearby structures,
  - identify pointers to the character object,
  - then statically analyze functions that write to `[base+0x38]`,
  - or use a safe code cave hook on candidate code addresses only.

When you’re back up, I can continue with the safer route:
- no data breakpoints,
- no debugger attach,
- only static analysis plus code-cave hooks on executable addresses.

