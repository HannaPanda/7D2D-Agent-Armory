# Nexus mod page assets

`description.bbcode` is the source of truth for the Nexus Mods description. Edit it here, then
paste it into the mod page's description field. The rich-text editor converts the BBCode
correctly on paste, so no mode switch is needed.

## Before pasting - check this against reality

**The "Tested on" bullet** claims all three builds were launched *and played*, with models,
icon, sound and drone behaviour looked at. Headless runs do not earn that sentence -
`tb report --mod agentarmory --json` has to show the GUI stage verified for the mod version
being released. If only some builds were played, name only those.

The Credits and AI-disclosure sections are settled (drone from CGTrader under a purchased
Royalty Free License, sound from ElevenLabs, code/translations with Claude). Keep them in sync
with [`../THIRD-PARTY.md`](../THIRD-PARTY.md) if the asset situation ever changes - the licence
forbids redistributing the model file and forbids AI training, and both claims are made on the
public page.

## Images

The description **hotlinks the screenshots straight out of this repo**, so replacing one is a
commit rather than a round trip through the Nexus Images tab:

```
https://raw.githubusercontent.com/HannaPanda/7D2D-Agent-Armory/refs/heads/main/nexus/images/<file>
```

| File | What it has to show | Why the description needs it |
|---|---|---|
| `SeekerHero.jpg` | Title card. Drones mid-swarm, fire and dust, the mod name over it. | The thumbnail the mod is judged by before anyone reads a word. |
| `SeekerSwarm.jpg` | Several drones fanned out toward a group of zombies, clearly heading in *different* directions. | The one claim the text cannot prove on its own: they split up and pick their own targets. This is the money shot. |
| `SeekerPod.jpg` | The landed pod on the street, zombies still at distance. | Backs the "it follows you and waits" section. Same camera as the swarm shot, so the two read as before/after. |

A fourth shot of the Explosives skill page was planned and **dropped**: the display entry does
not render in game (see the known issue in the README), so there is nothing to photograph. Add
it back once that is fixed - the description has a natural slot for it under "How you get one".

Two of them carry an italic caption in the description, because neither reads as a *feature*
without one: a glowing ball on tarmac is just a prop until the text says it is waiting.

The description also links the **gameplay video**
(<https://www.youtube.com/watch?v=pQ4O9ZTpW90>) right under the swarm shot. Nexus does not
embed YouTube in a description, so it is a plain link on purpose - also paste it into the
mod page's **Videos** tab, which is where the player-facing embed lives.

**To swap an image:** drop the new file in `nexus/images/` under the same name, commit, push.
The description needs no edit and the mod page updates on its next load.

Rules for what goes in this folder:

- **JPEG, max 1600 px wide, `-q:v 3`:**
  ```
  ffmpeg -y -i in.png -vf "scale='min(1600,iw)':-2" -q:v 3 out.jpg
  ```
  Hotlinked 1080p PNGs would pull several MB per page load; converted, all four are well under
  a megabyte together.
- **Keep the filenames stable.** They are baked into the description; renaming one silently
  breaks a live mod page.
- The uncompressed originals are not kept here - this folder holds the web copies only.

**Two things this does not replace:**

1. **Still upload the screenshots to the mod page's Images tab.** The gallery, the thumbnail
   and the search preview all come from there, not from the description. Hotlinking only saves
   the copy-the-CDN-URL step for images embedded *in the body*.
2. **Push before pasting the description.** The URLs resolve only once `nexus/images/` is on
   GitHub.

Nexus renders `raw.githubusercontent.com` images - confirmed on the 7 Dashes to Die and Adamant
pages, which use the same setup.
