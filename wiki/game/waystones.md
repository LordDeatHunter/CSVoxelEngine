# Waystones

Waystones are rune-carved stones, two blocks tall, that let you travel between places you have already found. They only generate in the world; you cannot craft or place one.

<ShotGallery slugs="waystone_surface" hero />

Both blocks, as they stand (a waystone is two cells tall and breaks as one):

<ModelViewer kind="block" id="waystone_surface" />

## Finding them

Every waystone stands in a small structure of its own a short walk from a ruin, and only some ruins get one. Each realm has its own layout:

| Realm | Structure | Where |
|---|---|---|
| Surface | An open pavilion with a lantern over the waystone | On level, dry land beside a surface ruin |
| Underground | A sealed, lamp-lit crypt with the waystone on a dais | Deep in the rock near an underground room; you dig in to reach it |
| Hell | A basalt ring crowned with ember crystal | On a cavern floor in the same hell layer as its ruin |
| Sky | A frost-stone plaza under a crystal-keyed arch | On the same island as a sky ruin |

<ShotGallery slugs="waystone_underground,waystone_sky,waystone_hell" />

## Discovering a waystone

Right-click a waystone (either of its two blocks) to activate it. The first time you do, it is added to your list of discovered waystones and a popup shows its name. Sneaking, or holding a block, places against the waystone instead.

Every waystone has a random name, four syllables like "Kafuziro", given the first time anyone activates it. The first player ever to activate a waystone gets a **Rename** button on that popup and is the only one who can rename it. A name keeps plain printable characters only and is at most 24 characters long.

## Traveling

Right-clicking a waystone you already know opens the teleport list: every waystone you discovered, with its realm and its distance, nearest first, and a search box. Click one to travel there. You must be standing at a waystone to use the list; the distance and the realm do not matter.

A trip needs **more than half of your mana**, and it **uses all of it**. Creative and flying players travel free. You arrive on a free spot beside the destination waystone, facing away from it. If the destination is far away, you are held over it until the terrain there has loaded.

## Lost waystones

Waystones can be mined with a pickaxe: mining either of its two blocks brings the whole stone down, and it crumbles into stone bricks. Explosions can destroy them too. A waystone that has lost either block stays in the list of everyone who discovered it, grayed out and marked as lost, and cannot be traveled to.

## Multiplayer

On a server the waystones and their names belong to the world, so every player sees the same names. Each player has their own list of discovered waystones, and the server checks every trip: that you stand at a waystone, that you discovered the destination, that it is not lost, and your mana. Only the first player who ever activated a waystone can rename it, and the new name reaches everyone who has discovered it.
