# Publishing Seen Better Days

When a `design.json` is added under `SeenBetterDays/ShippedDesigns`:

1. Add a brief line to `<ChangeLog>` in `SeenBetterDays/Properties/PublishConfiguration.xml`. Name the building and state, and credit the `author` from `design.json`.
2. Update the shipped design count in the `<LongDescription>` heading. Keep the rest of the description generic: do not add individual designs or designer names there.
3. Publish with `NewVersion`; `Update` changes listing metadata but does not ship new files or revise an existing version's public changelog. Before publishing, verify that each new `design.json` appears under the deployed mod's `Designs` folder.

Prepare replies to mod users for the maintainer to post. Do not send or post replies yourself.
