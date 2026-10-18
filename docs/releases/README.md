# Release notes

One file per version: `vX.Y.Z.md`. Its text becomes the GitHub release notes (`gh release create ... --notes-file`),
and `.github/workflows/discord-release.yml` posts the same text to the JoyPro Discord channel when the release is
published (repository secret `DISCORD_WEBHOOK_URL`; picture-pack releases and pre-releases are not announced).

Keep the notes short and user-facing, in this order (leave out empty sections):

```markdown
# JoyPro X.Y.Z

One or two sentences about the version.

## New
- ...

## Fixed
- ...

## Changed
- ...
```

Discord shows about 3,800 characters; longer notes end there with a pointer to the release page.

Only facts: what is new, what was fixed, what changed for the user. No reasons that involve encryption, obfuscation,
copy protection, anti-cheat, licenses or copyright, no internal notes (work folders, test-run logs, plans, how the
work was done). "IL-2 Korea: cockpit pictures are not available" is a fact; why is not part of the notes.
`tools\export_public.ps1` fails on such text in any public file.
