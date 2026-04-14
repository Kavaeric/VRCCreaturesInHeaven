# Anchor teleport

## Overview

A player teleport/transformation system that moves an arbitrary number of players from one location to another, preserving their position and orientation relative to an anchor point. Intended for use for any teleportation/transition requiring spatially coherent player transport.

## Components

### Zone anchor (entry)

- A single GameObject point that acts as the origin reference for the teleport.
- Has an associated bounding box defining the teleport zone.
- On teleport trigger/event, collects all players currently within the bounding box.
- Players outside the bounding box at trigger time are not affected.

### Zone anchor (exit)

- A single GameObject point that acts as the target reference.
- Has an associated bounding box collider defining the valid landing area.
- The exit anchor's full transform (position, rotation, scale) is applied to remap player positions.

## Behaviour on teleport trigger

1. Collect all players inside the entry bounding box.
2. For each collected player, record their position and rotation relative to the entry anchor's origin.
3. Remap each relative transform through the exit anchor's transform:
   - **Position:** Scale the relative offset by the ratio of exit scale to entry scale, then apply to exit anchor position.
   - **Rotation:** Apply the exit anchor's rotation delta relative to the entry anchor.
4. If the remapped exit position falls outside the exit bounding box, clamp it to within bounds.
5. Teleport each player to their remapped position and rotation.

## Transform behaviour & examples

### Basic position transform

Players maintain their relative spacing from the anchor origin. Players outside of the entry bounding box will not be moved.

In this example, players ① and ② are teleported to the exit zone while keeping their relative position to the anchor (●), i.e. they are teleported 10 metres along the Z-axis.

As player ③ is outside the bounding box, they are not affected by the teleport action at all.

```
Entry (0, 0, 0)
●─────────┐
│          │
│    ①    │
│       ② │  ③
└──────────┘

Exit (0, 0, 10)
●─────────┐
│          │
│    ①    │
│       ② │
└──────────┘
```

### Rotation

If the exit anchor is rotated relative to the entry, all players are rotated by the same delta.

Here, the entry and exit anchors are located at the same spot, but the exit is rotated 90 degrees anticlockwise. Player ① hence gets teleported to the top-right corner of the corresponding zone and is also subsequently facing to the left instead of up.

```
Entry (0)   Exit (90 CCW)
┌─────────●──────────┐
│          │     ←① │
│      ↑  │          │
│      ①  │          │
└──────────┴──────────┘

```

### Scale

If the exit anchor is scaled the distances between players and the anchor origin scale accordingly.

Here, the exit anchor is scaled to be 2x the size on one of its axes; subsequently all three players are more spaced out along that axis at the exit position.

```
Entry (1, 1, 1)
●─────────┐
│  ①      │
│    ②    │
│      ③  │
└──────────┘

Exit (2, 1, 1)
●─────────────────┐
│    ①            │
│        ②        │
│            ③    │
└──────────────────┘
```

This does not affect player height or avatar scale, which is a separate system concern.

### Exit clamping box

Optionally a world creator can define an exit clamping box that restricts player destination transforms. This is seperate from scaling as it simply clamps player transforms to the bounds.

Here, the exit's clamping box is only about half as tall as the entry's bounding box. The players are transformed as normal, however because player ③'s exit position (Ⓧ) is outside of the new clamping box, their position is then clamped to be within the new bounds.

```
Entry
●─────────┐
│  ①      │
│    ②    │
│          │
│        ③│
└──────────┘

Exit
●─────────┐
│  ①      │
│    ②  ③│
└────────↑┘
         Ⓧ
```

## Notes

- The bounding box at the exit acts as a clamp, not a filter — all collected players are teleported, but their landing positions are constrained to valid space.
- Player avatar scale is out of scope for this system and would be handled separately.
- Smooth interpolation of player position between the entry and exit is out of scope for this system.
