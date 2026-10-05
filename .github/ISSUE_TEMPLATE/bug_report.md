---
name: Bug Report
about: Report a bug to help improve Tisilia
title: '[Bug] '
labels: bug
assignees: ''
---

> **Do not paste credentials, tokens, real request or response bodies, or real data.** A reproduction with made-up values
> is always enough. An issue is public and stays that way — anything posted here should be assumed to be permanent.

## Environment

- **Tisilia**: output of `dotnet tisilia version`
- **.NET SDK**: output of `dotnet --version`
- **Node and TypeScript**: `node --version`, and the TypeScript version the client is compiled with
- **OS**: (e.g., Windows 11, Ubuntu 24.04)
- **Where it happens**: export / generate / check / conformance / the generated client / the Nuxt module / the Explorer

## Description

A clear description of the bug.

## Steps to Reproduce

1.
2.
3.

## Expected Behavior

What you expected to happen.

## Actual Behavior

What actually happened.

## Code Example

```csharp
// The smallest endpoint and types that show it, with made-up values
```

```ts
// The call, if it is the client that misbehaves
```

## Diagnostics or Failure

The diagnostic as printed — its `TIS` code, its `[SV…]` rule, the message and the JSON pointer — or, for a call, the
failure's `kind` and fields. An excerpt of the contract around the pointer helps too.

## Additional Context

Any other relevant information.
