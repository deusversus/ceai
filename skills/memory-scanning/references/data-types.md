# Game Data Type Patterns by Genre

## RPGs (JRPGs, WRPGs, Action RPGs)
- HP/MP: Float (modern) or Int32 (retro/indie)
- Stats (STR, DEX, INT): Int32, often stored in contiguous struct
- EXP: Int32 or Int64 for high-level caps
- Gold: Int32; some games cap at 999,999,999
- Skill levels: Int16 or Byte
- Equipment IDs: Int32 (index into item table)
- Status effects: Bitfield (Byte or Int32)

## FPS / Shooters
- Health: Float (0.0-100.0 or 0.0-1.0 normalized)
- Ammo (magazine): Int32 or Int16
- Ammo (reserve): Int32
- Coordinates: Float[3] (x, y, z) — contiguous 12 bytes
- View angles: Float[2] (pitch, yaw) — sometimes Double
- Recoil: Float (resets to 0 after shot)
- Spread: Float (increases during fire, decays)

## Platformers / Side-scrollers
- Lives: Int32 or Byte
- Score: Int32 or Int64
- Coins/collectibles: Int32
- Position: Float[2] (x, y) — sometimes Int32 for tile-based
- Timer: Float (seconds) or Int32 (frames)
- Level ID: Int32 or Byte

## Strategy / City Builders
- Resources: Int32 or Float (often displayed as integer but stored as float)
- Population: Int32
- Timers: Float (game-time seconds)
- Unit HP: Float or Int16
- Coordinates: Float[2] or Float[3]

## Racing Games
- Speed: Float (m/s or km/h internal)
- Lap time: Float (seconds) or Double
- Position: Float[3]
- Boost meter: Float (0.0-1.0)
- Nitro: Float

## Survival / Crafting
- Hunger/Thirst/Stamina: Float (0.0-100.0 or 0.0-1.0)
- Inventory quantities: Int32 or Int16
- Durability: Float or Int32
- Temperature: Float
- Weight: Float

## Common Struct Patterns
When you find one value, adjacent memory often contains related values:
- Player stats block: [HP(4)][MaxHP(4)][MP(4)][MaxMP(4)][STR(4)][DEX(4)]...
- Position block: [X(4)][Y(4)][Z(4)][RotX(4)][RotY(4)][RotZ(4)]
- Inventory slot: [ItemID(4)][Quantity(4)][Durability(4)][Flags(4)]

Use `DissectStructure` after finding one value to map the surrounding structure.
