# Security policy

graphiti-net stores what AI agents remember and sends that data to LLM providers, so a bug can be a security issue.

**Please don't open public issues for vulnerabilities.** Report them privately through GitHub's "Report a
vulnerability" button on this repository (Security → Advisories). Include the steps or episodes that reproduce the
problem if you can.

We aim to acknowledge reports within 3 working days and to publish a fix and an advisory as soon as one is ready.

## What the library does not do

Like upstream Graphiti, graphiti-net is a library and leaves these to the service that hosts it:

- authentication and authorisation;
- tenant isolation, by scoping every call to the caller's group id;
- controls against prompt injection. Ingested text is sent to the LLM, so treat it as untrusted input.

Vulnerabilities in upstream Graphiti itself should be reported to Zep through
[getzep/graphiti](https://github.com/getzep/graphiti).
