# Third-party assets

**The MIT licence in [LICENSE](LICENSE) covers the code, the XML configuration, the build
scripts and the documentation in this repository. It does NOT cover the assets listed below.**
Those are licensed to the author personally and cannot be re-licensed onward - MIT would
otherwise purport to grant everyone a redistribution right the author does not hold.

## Drone mesh and texture

- **Source:** "Hunter Drone Sci-Fi" - <https://www.cgtrader.com/3d-models/aircraft/other/hunter-drone-sci-fi>
- **Licence:** CGTrader **Royalty Free License**, purchased by the author.
- **Permitted** by that licence: use in own projects, backups and sharing within a team,
  modifying/adapting/combining with other assets, and commercial use.
- **Not permitted:** redistribution (selling or re-uploading the file) and use for training or
  fine-tuning AI models.

Where it appears in this repository:

| Path | What |
|---|---|
| `AgentArmory/Resources/seekerdrone.unity3d` | the mesh and material, compiled into a Unity AssetBundle |
| `AgentArmory/UIAtlases/ItemIconAtlas/seekerCluster.png` | the inventory icon, a render of the same drone |
| `src/unity/SeekerDrone/` | the Unity source prefab and material |

Both shipped forms are **derived, packed artefacts inside a game mod**, which is what the
licence's "use this model in your own projects" and "modify, adapt or combine" clauses are for.
What is *not* granted, by the author to anyone else, is the right to take the mesh back out and
pass it on. Do not extract the model from the bundle, and do not feed any of it to an AI
training or fine-tuning pipeline.

If you want to fork this mod, everything except the drone art is yours under MIT. Replace the
mesh, the material and the icon with your own, or buy your own licence for the same model.

## Activation sound

- `src/unity/Sounds/activate.wav`, shipped inside `seekerdrone.unity3d`.
- Generated with **ElevenLabs** (sound-effect generator, paid/premium account). Covered by the
  author's ElevenLabs licence terms, not by MIT.
