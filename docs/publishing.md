# Publishing to NuGet.org

Releasing is a tag push. `.github/workflows/release.yml` builds, tests, packs and pushes
the package and its symbols; everything else on this page is the one-time setup around it,
and all of it needs a human — nothing here can be automated from the repository alone.

## Before the first release

### 1. Pick a package ID that is free

**`ThrottledLogging` is taken.** It belongs to an unrelated package by `coldhighsun`
(a per-key duplicate-message suppressor), first published April 2026 and still active —
[nuget.org/packages/ThrottledLogging](https://www.nuget.org/packages/ThrottledLogging).
Pushing under that ID fails with a 403, so the ID has to change before anything is published.

The ID is `<PackageId>` in `src/ThrottledLogging/ThrottledLogging.csproj`. It is independent
of the assembly name and the namespace, so only that one line has to move — though if the
assembly name stays `ThrottledLogging` too, a consumer who somehow referenced both packages
would have two `ThrottledLogging.dll` files to choose between. Renaming the assembly as well
avoids that, at the cost of a wider change.

Checked and free at the time of writing: `CarlosK.ThrottledLogging`,
`ThrottledOperationLogging`, `ThrottledLogging.Operations`, `OperationThrottledLogging`,
`ThrottledProgressLogging`. Confirm whichever you choose is still free just before publishing:

```bash
curl -s -o /dev/null -w '%{http_code}\n' \
  https://api.nuget.org/v3/registration5-semver1/<lowercased-id>/index.json
# 404 = free, 200 = taken
```

A prefixed ID (`CarlosK.*`) is also the one that can be reserved: nuget.org grants an ID
prefix to an owner, so `CarlosK.*` can be made yours and nobody else's, which is not
possible for a bare word like `ThrottledLogging`.

### 2. Create the NuGet.org account

Sign in at [nuget.org](https://www.nuget.org) with a Microsoft account and choose a
username. That username is the package owner and it is what the publishing setup below
refers to.

### 3. Set up credentials — one of two ways

**Trusted publishing (recommended).** No long-lived key exists anywhere. The workflow proves
it is this repository using a GitHub OIDC token and gets an API key that expires in an hour.

- On nuget.org: your username → **Trusted Publishing** → add a policy with
  repository owner `CarlosSolrac`, repository `throttled-logging-dotnet`, workflow file
  `release.yml`, environment `nuget`.
- On GitHub: Settings → Secrets and variables → Actions → **Variables** → add
  `NUGET_USER` with your nuget.org username (the username, not the email address).

The workflow uses trusted publishing whenever `NUGET_USER` is set.

**An API key (the fallback).** On nuget.org: your username → **API Keys** → create a key
scoped to **Push** for your chosen package ID (glob patterns such as `CarlosK.*` work), with
the shortest expiry you can live with. Then on GitHub: Settings → Secrets and variables →
Actions → **Secrets** → add `NUGET_API_KEY`. Keys expire, so this is a recurring errand;
trusted publishing is not.

Either way the credential lives in GitHub, never in the repository.

### 4. Decide whether a release needs approval

The workflow's job runs in a GitHub environment named `nuget`. Add a required reviewer to it
under Settings → Environments → `nuget` and every tag push pauses for your approval before
anything reaches NuGet.org. Leave it without protection rules and the publish proceeds
unattended. Given that a published version can never be deleted, the approval is worth having.

## Releasing

1. Bump `<Version>` in `Directory.Build.props` and merge that to `main`.
2. Tag the merge commit and push the tag:

```bash
git tag v0.2.0
git push origin v0.2.0
```

The tag must match `<Version>` exactly, with a leading `v`. The workflow compares the two and
refuses to publish if they disagree, which is what catches a tag without a bump and a bump
without a matching tag. `Directory.Build.props` is the source of truth; the tag only confirms it.

A prerelease version (`0.1.0-alpha`) is published as a prerelease automatically — NuGet reads
the suffix, there is no separate flag.

## Rehearsing

Run the workflow manually (Actions → Release → Run workflow). It does everything except the
push and leaves the `.nupkg` and `.snupkg` as a build artifact you can download and inspect —
`unzip -l` it, or open it in [NuGet Package Explorer](https://github.com/NuGetPackageExplorer/NuGetPackageExplorer).

Locally, `dotnet pack src/ThrottledLogging/ThrottledLogging.csproj -c Release -o artifacts`
produces the same two files.

## What cannot be undone

A published version is permanent. It can be **unlisted**, which hides it from search and from
version resolution, but the file stays downloadable forever for anyone who already depends on
it, and **that version number can never be reused**. Deletion happens only for legal or
security reasons, by asking NuGet support.

So the first push of a new package ID is the decision that sticks: after it, the ID and every
version number you burn are permanent. Rehearse with a manual run, and consider starting on a
prerelease version.

## What the package already carries

Set in `Directory.Build.props` and the library's `.csproj`, so none of this needs doing again:
MIT licence expression, `README.md` as the package readme, project and repository URLs,
authorship, tags, deterministic CI builds, SourceLink for step-into debugging, and a `.snupkg`
symbol package published alongside the main one.
