# Contributing

Thanks for helping. Issues and pull requests are welcome.

Before 1.0 the API may still change. Start with [docs/requirements.md](docs/requirements.md) and the
[roadmap](docs/roadmap.md), and open an issue before large changes so we can agree on the approach.

Build and test with `dotnet test GraphitiNet.slnx`. Warnings are errors. The PostgreSQL tests need PostgreSQL with
pgvector (see [docs/postgresql.md](docs/postgresql.md#testing)) and skip when it is unreachable.

## Porting rules

1. **Port from the pinned release.** Work from the upstream Graphiti release recorded in the README, not from
   upstream `main`.
2. **Keep Zep's copyright.** A file translated from Graphiti's source, prompts or tests starts with a header like this,
   and is added to [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md):

   ```csharp
   // Copyright 2024, Zep Software, Inc.
   // Copyright 2026, graphiti-net contributors
   // Licensed under the Apache License, Version 2.0. See LICENSE.
   //
   // Ported from graphiti_core/<path>.py (Graphiti v<x.y.z>) and changed for .NET.
   ```

3. **Same results as upstream.** Python Graphiti on the same inputs is the definition of correct. If you change
   behaviour on purpose, write down what and why in the docs.
4. **Prompts match upstream.** Changing a prompt or a response schema needs a parity run that shows it still matches.
5. **No secrets in commits.** Keep keys in environment variables or `dotnet user-secrets`.
6. **Sign off your commits** (`git commit -s`). By signing off you certify the
   [Developer Certificate of Origin](https://developercertificate.org/): you wrote the change or have the right to
   submit it under the project's licence.

## Naming

Don't name packages, namespaces or features in a way that suggests an official Zep product. Graphiti is Zep's brand;
this project is an independent rewrite.

## Releasing

The version is `VersionPrefix` and `VersionSuffix` in [Directory.Build.props](Directory.Build.props), and
[CHANGELOG.md](CHANGELOG.md) needs an entry for it (`scripts/check-release.sh` checks both). Pushing the tag
`v<version>`, such as `v0.2.0`, runs the checks in [ci.yml](.github/workflows/ci.yml). GitHub Actions then pushes the
packages and their symbol packages to nuget.org and makes a GitHub release.

Publishing uses nuget.org's [Trusted Publishing](https://learn.microsoft.com/en-us/nuget/nuget-org/trusted-publishing),
so no API key is stored anywhere: nuget.org gives the release job a key that lasts an hour. Set it up once:

1. **On nuget.org**, signed in as the account (or organization) that will own the packages: your name, **Trusted
   Publishing**, add a policy with owner `tekthar`, repository `graphiti-net`, workflow file `ci.yml` (the file name
   only) and environment `release`. Let it publish new packages, for packages matching `GraphitiNet*`.
2. **On GitHub**, in the repository's **Settings**:
   - **Environments:** add `release`. Limit it to tags `v*` and, if you like, require a reviewer, so a tag waits for
     a person before anything is pushed.
   - **Secrets and variables, Actions, Variables:** add `NUGET_USER`, the nuget.org profile name (not the email) of
     the account in step 1.
3. **Release:** push the tag, `git tag v<version> && git push origin v<version>`.

## Code of conduct

Be respectful and assume good intent. Harassment or discrimination of any kind isn't tolerated. Report problems to the
maintainers privately.
