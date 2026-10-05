## Summary

Brief description of changes.

## Related Issue

Fixes #(issue number)

## Type of Change

- [ ] Bug fix (non-breaking change that fixes an issue)
- [ ] New feature (non-breaking change that adds functionality)
- [ ] Breaking change (fix or feature that would cause existing functionality to not work as expected)
- [ ] Contract format change (the schemas, the semantic rules, or what a contract records)
- [ ] Documentation update

## Changes

-
-

## Checklist

- [ ] I have read the [CONTRIBUTING](../CONTRIBUTING.md) guidelines
- [ ] Build is warning-free (`npm run build`, `dotnet build src/backend/Tisilia.slnx`)
- [ ] Tests pass (`dotnet test src/backend/Tisilia.slnx`, `npm run typecheck`, `npm test`)
- [ ] A change in behaviour brings a test; a fix brings one seen to fail without the fix
      (see [CONTRIBUTING.md](../CONTRIBUTING.md#what-a-change-has-to-bring-with-it)) — or the
      pull request says why the change cannot be tested
- [ ] Formatting passes (`dotnet format src/backend/Tisilia.slnx --verify-no-changes`)
- [ ] No package under `src/backend`, and not `@kkdev92/tisilia-runtime`, takes a third-party runtime dependency
- [ ] Nothing that a contract cannot describe exactly is guessed, rounded or let through: it is a diagnostic with a fix
- [ ] No credential reaches a diagnostic, a contract, a generated file, a payload or browser storage
- [ ] I have updated documentation if needed
