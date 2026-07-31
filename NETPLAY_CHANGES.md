# Network chnages

Read [Readme](README.md) first and ensure the mod is installed

This will list hopefully exhaustively what are the changes made by the mod.
Those change where made for balances purposes.

## Pause ? what is that ?

There is no pause on netplay. Die

## Synchronization

Most of the stuff are synchronized but some are per player while other are fully sychronized

For example buying something from the shady guy is locally simulated meaning he won't flee when you buy somehing for the others

Charging shrines are instead fully synchronized meaning every one will get the bonus.

> [!NOTE]  
> There is a bug where you can miss either your level up or your shrine upgrade because both can't happen at the same time. Will try to fix later

Magnet (Shrine and pickup) make the xp goes to a random player (No the one that interacted won't get all the xp)

You get i-frames when leveling up/getting a reward/openning a chest. This is to counter balance the fact that the game won't be paused anymore. 5 sec for choosing a reward and 1 second when finishing

<p align="center">
  <img src="images/timer.gif" alt="timer" />
</p>

> [!NOTE]  
> You can also still move behind when selecting a reward

## Save / Steam interaction

**Leaderboard scores are never uploaded while this mod is installed** — not in netplay, and not
in singleplayer either.

That is deliberate. The game detects mods by looking for their files on disk, so a solo run with
MegabonkTogether installed is just as detectable as a co-op one. Blocking only during netplay
would leave your singleplayer scores exposed to exactly the risk this is meant to avoid. Getting
your run onto the leaderboard is the one thing that could actually get you in trouble, so the mod
refuses to submit at all.

**If you want to post a legitimate score, disable or remove the mod first** — the leaderboard
then works normally.

**Saving is disabled by default** during netplay, so your run progression is not written over.
There is an option to allow it if you would rather keep progression (`AllowSavesDuringNetplay`)
— use at your own risk.

**Steam achievements still unlock.** Playing together is still playing, so achievements are left
alone.

> **Known issue — Steam stats.** The mod contains patches intended to stop netplay statistics
> (kills, damage, and similar) reaching Steam, but they do not cover the path the game actually
> uses, so netplay numbers can still be recorded against your profile. This affects your Steam
> stats only — **not** leaderboards, which stay blocked. Being tracked properly at
> [`docs/netplay/01-critical-fixes.md`](docs/netplay/01-critical-fixes.md) as P0-0.

## Custom game balance

The mod feature some game code to try to re balance the game as more player join a session.
This is **CERTAINLY** not perfect and probably unbalanced right now and will need to be tuned as more people try and report (For any dev ,the class i'm refering is `GameBalanceService.cs`). Those are the stat being modified:

- Credit timer , The credit is a value the in game sumonner use to know how much it can spawn enemies. The more player, the more credit the summonner get

- Free Chest Spawn Rate, the more player we have in a session, the more free chest we should have on the map

- Pickup Xp : The xp you get from the pickup, since pickup are synchronized, i realized player get rapidly under level since they are sharing XP. The mod will boost a bit the reward of XP on netplay to counterbalance the fact that there are less XP. The current balance is **DOUBLE** XP for 2-4 players and **TRIPLE** XP for 5 and 6 players.

- Boss lamp charge: In the graveyard, the final boss is naturally weaker the more player with have as they can quicly charge lamp on their own. To counterbalance that, there is some additionnal time required to charge a lamp. The initial charge time when playing solo is 3, its doubled for example at 2 players and quadrupled at six players.

- Max spawnable enemies: 400 and under on a final swarm (keeping the original cap) ,500 up to 4 player and 600 for 5-6 players (untested ,you have been warned)
