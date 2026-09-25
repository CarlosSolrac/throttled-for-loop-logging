# Code signing with SignPath

The published policy lives in [`CODE_SIGNING_POLICY.md`](../CODE_SIGNING_POLICY.md). This document is
the build-side half: what the repository already does to satisfy the
[SignPath Foundation conditions](https://signpath.org/terms.html), and what still has to be wired up
once the free subscription is granted.

## What the repository already does

| Condition | Where it is met |
| --- | --- |
| OSI-approved licence, no dual licensing, no proprietary components | MIT, see [`LICENSE`](../LICENSE); the only dependencies are Microsoft's `Microsoft.Extensions.*` abstractions, MIT as well. |
| Functionality documented | [`README.md`](../README.md), and the design document under `docs/design/`. |
| Code signing policy published | [`CODE_SIGNING_POLICY.md`](../CODE_SIGNING_POLICY.md), summarised in the README. |
| Binaries built from source in a verifiable way | `.github/workflows/ci.yml` builds, tests and packs on GitHub-hosted runners with `ContinuousIntegrationBuild=true`; `Directory.Build.props` turns on deterministic builds and SourceLink. |
| Product name and product version metadata set and identical in every build | `Directory.Build.props` sets `Product`, `AssemblyVersion`, `FileVersion` and `InformationalVersion` from the single `Version` property, and disables the source-revision suffix so the informational version does not change from build to build. |

## What the release workflow needs to add

Signing happens in the release workflow's `publish` job, after the package is packed and before it
is pushed to NuGet, so that what is published is the signed package. The `github-release` job
attaches whatever `publish` uploaded, so upload the signed package under the name it downloads.

```yaml
permissions:
  contents: read
  actions: read          # needed while the repository is private and the SignPath GitHub App is not installed
  id-token: write        # only if NuGet trusted publishing is used in the same job

steps:
  # ... checkout, setup-dotnet, restore, build, test, pack into artifacts/ ...

  - name: Upload unsigned package
    id: upload-unsigned
    uses: actions/upload-artifact@v4
    with:
      name: nupkg-unsigned
      path: artifacts/*.nupkg
      if-no-files-found: error

  - name: Submit signing request
    uses: signpath/github-action-submit-signing-request@v2
    with:
      api-token: ${{ secrets.SIGNPATH_API_TOKEN }}
      organization-id: ${{ vars.SIGNPATH_ORGANIZATION_ID }}
      project-slug: throttled-for-loop-logging
      signing-policy-slug: release-signing
      artifact-configuration-slug: nupkg
      github-artifact-id: ${{ steps.upload-unsigned.outputs.artifact-id }}
      wait-for-completion: true
      output-artifact-directory: artifacts-signed

  - name: Push signed package
    run: dotnet nuget push 'artifacts-signed/*.nupkg' --source https://api.nuget.org/v3/index.json --api-key "$NUGET_API_KEY"
```

Notes that matter:

- The action only accepts artifacts uploaded with `actions/upload-artifact`; it takes the artifact id,
  not a path.
- For an open source subscription every job in the workflow must run on GitHub-hosted runners.
- `wait-for-completion: true` blocks until an approver has approved the request in SignPath, which is
  how the "every release needs manual approval" condition is met. Give the job a generous timeout.
- The release job already runs in a GitHub environment that can require a reviewer; that gate and the
  SignPath approval are independent, and both are worth keeping.
- These steps are written here rather than committed into
  [`.github/workflows/release.yml`](../.github/workflows/release.yml), because the SignPath
  organization id, project slug and API token they need do not exist until the subscription does,
  and a workflow referencing them would fail on the next release.

## Artifact configuration

The artifact configuration is created in SignPath, not in this repository. A NuGet package of a
multi-targeted library signs the assemblies inside the package and then the package itself, and
pins the metadata that the conditions require:

```xml
<artifact-configuration xmlns="http://signpath.io/artifact-configuration/v1">
  <nupkg-file>
    <nuget-sign />
    <directory path="lib">
      <directory path="net8.0">
        <pe-file path="ThrottledLogging.dll" product-name="ThrottledForLoopLogging" product-version="0.1.0-alpha">
          <authenticode-sign />
        </pe-file>
      </directory>
      <directory path="net10.0">
        <pe-file path="ThrottledLogging.dll" product-name="ThrottledForLoopLogging" product-version="0.1.0-alpha">
          <authenticode-sign />
        </pe-file>
      </directory>
    </directory>
  </nupkg-file>
</artifact-configuration>
```

Check the element names against the
[artifact configuration reference](https://docs.signpath.io/artifact-configuration/reference) when you
paste it in, and parameterise the version rather than hard-coding it if you would rather not edit the
configuration on every release.

## Steps that happen outside this repository

1. The repository is public, and everyone with write access has two-factor authentication enabled.
   The free programme is for open source projects, and both are conditions of it.
2. A release of the package exists in the form that should be signed — the conditions require the
   software to be released already. Publishing is described in
   [`docs/publishing.md`](publishing.md).
3. Apply at <https://signpath.org/apply>, linking the repository, the release, and
   `CODE_SIGNING_POLICY.md`.
4. After acceptance: create the SignPath project, the signing policy, the artifact configuration and
   an API token; add `SIGNPATH_API_TOKEN` as a repository secret and `SIGNPATH_ORGANIZATION_ID` as a
   repository variable; install the SignPath GitHub App.
5. Add the signing steps above to the release workflow, and confirm the project name used in the
   artifact configuration matches the `Product` property in `Directory.Build.props`.
