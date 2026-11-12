# Contributing

Thanks for helping. Issues and pull requests are welcome.

The project is in planning. Start with [docs/requirements.md](docs/requirements.md), and open an issue before large
changes so we can agree on the approach.

## Porting rules

1. **Port from the pinned release.** Work from the upstream Graphiti release recorded in the README, not from
   upstream `main`.
2. **Keep Zep's copyright.** A file translated from Graphiti's source, prompts or tests starts with a header like this,
   and is added to [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md):

   ```csharp
   // Copyright 2024, Zep Software, Inc.
   // Copyright 2026, net-graphiti contributors
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

## Code of conduct

Be respectful and assume good intent. Harassment or discrimination of any kind isn't tolerated. Report problems to the
maintainers privately.
