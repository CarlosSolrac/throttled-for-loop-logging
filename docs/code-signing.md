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

Signing happens in the release workflow's `publish` job, after it downloads the package that the
`build` job packed and before it pushes to NuGet, so that what is published is the signed package.
The SignPath action needs the id of an uploaded artifact, so upload the build job's package with an
`id` on that step and pass its `artifact-id` output on. The `github-release` job attaches the
artifact named `nupkg-<version>`, so after signing, upload the signed package under that name with
`overwrite: true`; otherwise the GitHub release would carry the unsigned one.

```yaml
build:
  steps:
    # ... checkout, setup-dotnet, restore, build, test, pack into artifacts/, check the package ...

    - name: Upload package
      id: upload-unsigned          # add an id so the publish job can name this artifact
      uses: actions/upload-artifact@v6
      with:
        name: nupkg-${{ needs.plan.outputs.version }}
        path: |
          artifacts/*.nupkg
          artifacts/*.snupkg
        if-no-files-found: error
  outputs:
    unsigned-artifact-id: ${{ steps.upload-unsigned.outputs.artifact-id }}

publish:
  permissions:
    actions: read          # lets SignPath fetch the artifact
    id-token: write        # NuGet trusted publishing, as today
  timeout-minutes: 120     # waits for a human to approve the signing request
  steps:
    - name: Submit signing request
      uses: signpath/github-action-submit-signing-request@v2
      with:
        api-token: ${{ secrets.SIGNPATH_API_TOKEN }}
        organization-id: ${{ vars.SIGNPATH_ORGANIZATION_ID }}
        project-slug: throttled-for-loop-logging
        signing-policy-slug: release-signing
        artifact-configuration-slug: nupkg
        github-artifact-id: ${{ needs.build.outputs.unsigned-artifact-id }}
        wait-for-completion: true
        output-artifact-directory: artifacts

    # Replace the unsigned artifact, so the GitHub release attaches what NuGet.org gets.
    - name: Upload signed package
      uses: actions/upload-artifact@v6
      with:
        name: nupkg-${{ needs.plan.outputs.version }}
        path: |
          artifacts/*.nupkg
          artifacts/*.snupkg
        overwrite: true
        if-no-files-found: error

    # ... then the existing NuGet login and Push steps, unchanged ...
```

Notes that matter:

- The action only accepts artifacts uploaded with `actions/upload-artifact`; it takes the artifact id,
  not a path.
- For an open source subscription every job in the workflow must run on GitHub-hosted runners.
- `wait-for-completion: true` blocks until an approver has approved the request in SignPath, which is
  how the "every release needs manual approval" condition is met. Give the job a generous timeout.
- The `publish` job already runs in a GitHub environment that can require a reviewer; that gate and the
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
