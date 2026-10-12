# Contributing to Torii

Thank you for your interest in contributing to Torii! We aim to provide an open, collaborative environment for everyone involved. These guidelines outline our development process, PR expectations, and repository scope.

## Foreword on Contributions & Standards

Our team values **high-quality, human-crafted contributions**. We respect clean engineering and maintainability:

- **Single-responsibility changes**: We prefer smaller, testable, focused pull requests over large monolithic diffs.
- **Clean separation**: Never mix unrelated build, dependency, or CI changes into feature or performance PRs.
- **Thorough local testing**: Code should be validated and tested locally prior to opening a pull request. Contributions generated carelessly or without verification may be closed without review.

## Table of contents

1. [Reporting bugs](#reporting-bugs)
2. [Providing general feedback & discussions](#providing-general-feedback--discussions)
3. [Scope: Torii vs Upstream osu!](#scope-torii-vs-upstream-osu)
4. [Submitting pull requests](#submitting-pull-requests)
5. [Development workflow & coding standards](#development-workflow--coding-standards)
6. [Resources & Community](#resources--community)

## Reporting bugs

A **bug** is a situation where something is clearly and objectively broken. Examples of applicable bug reports include:

- The game crashes when entering a screen or playing a beatmap.
- Torii-specific UI elements (e.g. briefing overlay, aura settings) glitch or fail to render.
- Network or profile sync issues when connecting to the Torii server.
- Severe performance regressions specific to Torii or Torii Nova builds.

To track bug reports, use the [Torii GitHub Issues](https://github.com/ShikkesoraSIM/torii-osu/issues) tracker. When opening an issue:

- Search existing open and closed issues first to check if the problem has already been reported or resolved.
- Use the **Torii Bug Report** template, filling out your OS, build version / stream (Torii or Torii Nova), logs, and reproduction steps.
- Provide logs from your logs directory (on desktop: accessible via game settings; on Android: `Android/data/sh.shikkesora.torii/files/logs`).

## Providing general feedback & discussions

If you wish to:

- Provide subjective feedback on Torii UI, audio, or client feel.
- Propose new client polish features, auras, or quality-of-life enhancements.
- Discuss development of the experimental Torii Nova stream.

Please open a GitHub Discussion or join the discussion in our [Discord server](https://discord.gg/zeuKQquCSE).

## Scope: Torii vs Upstream osu!

Torii is an independent community fork of osu! (lazer). To keep development maintainable and clean:

- **Torii Scope**:
  - Torii client features (auras, briefing overlays, custom layout options, theme tweaks).
  - Torii Nova experimental engine improvements (platform optimizations, threading, low-latency tests).
  - Interoperability with Torii server infrastructure (`g0v0-server`).
  - Bug fixes and client-side performance enhancements.
- **Upstream Scope (ppy/osu)**:
  - Fundamental rhythm game rulesets (standard, taiko, catch, mania scoring and PP calculations).
  - Core beatmap decoding formats and standards.
  - osu!framework foundational primitives (these should ideally be contributed upstream to `ppy/osu-framework`).

Because Torii periodically rebases and merges updates from upstream `ppy/osu`, general rhythm game mechanics or core framework features should usually be proposed upstream first.

## Submitting pull requests

We welcome pull requests from community contributors. To ensure smooth review:

1. **Discuss before big changes**: If you plan to implement a new feature or refactor existing systems, open an issue or discussion on Discord first so we can align on architecture and direction.
2. **Branch off `master`**: Always create your feature branch from the latest `master` of `ShikkesoraSIM/torii-osu`.
3. **Keep PRs focused**: Keep your PR to a single logical change. Do not bundle formatting overhauls, submodule bumps, or CI updates with feature code.
4. **Target branch**: All pull requests should target `master`.
5. **Allow edits**: Keep the *Allow edits from maintainers* option enabled so maintainers can assist with minor fixes or rebases.

## Development workflow & coding standards

- **C# & .NET**: Torii targets .NET 8 / modern C#. Follow standard C# idioms and conventions used throughout the repository.
- **Building**: Validate that your changes compile cleanly with `dotnet build osu.Desktop/osu.Desktop.csproj`.
- **Testing**: Add unit tests or visual tests (`osu.Game.Tests`) wherever practical. Ensure existing tests pass (`dotnet test`).
- **Code Style**: Format your code cleanly matching the repository's `.editorconfig` rules. You can run `dotnet format` or use the repository's inspection scripts (`InspectCode.sh` / `InspectCode.ps1`).
- **Submodules & Pins**: Keep framework submodule references intact and matching repository configuration.

## Resources & Community

- [Torii Issues](https://github.com/ShikkesoraSIM/torii-osu/issues): Bug reports and feature proposals.
- [Torii Discord](https://discord.gg/zeuKQquCSE): Development discussions, testing feedback, and community chat.
- [Torii Web Portal](https://lazer.shikkesora.com/): Official portal for Torii.
- [`ppy/osu-framework` Wiki](https://github.com/ppy/osu-framework/wiki): Documentation and architectural guides for the 2D framework powering the client.
