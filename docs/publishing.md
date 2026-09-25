# Publishing to NuGet.org

Releasing is a version bump. Change `<Version>` in `Directory.Build.props`, merge it to `main`,
and `.github/workflows/release.yml` does the rest once CI is green on that commit: it builds,
tests and packs, pushes the package and its symbols to NuGet.org, creates the tag `v<Version>`
on that commit, and creates a GitHub release with the packages attached. Nobody pushes a tag by
hand. Everything else on this page is the one-time setup around it, which needs a human.

## Before the first release

### 1. The package ID

The package is **`ThrottledForLoopLogging`**, set as `<PackageId>` in
`src/ThrottledLogging/ThrottledLogging.csproj`. It matches the repository name and was free on
NuGet.org as of 2026-09-22.

It is deliberately *not* `ThrottledLogging`: that ID belongs to an unrelated package by
`coldhighsun` (a per-key duplicate-message suppressor), published since April 2026 and still
active — [nuget.org/packages/ThrottledLogging](https://www.nuget.org/packages/ThrottledLogging).
Pushing under it would fail with a 403.

The package ID is independent of the assembly name and the namespace, which both remain
`ThrottledLogging`. That is fine in practice; the only wrinkle is that a consumer who somehow
referenced both packages would have two `ThrottledLogging.dll` files to choose between.

Confirm the ID is still free just before the first publish — until a package is pushed, nothing
reserves it:

```bash
curl -s -o /dev/null -w '%{http_code}\n' \
  https://api.nuget.org/v3/registration5-semver1/throttledforlooplogging/index.json
# 404 = free, 200 = taken
```

NuGet can reserve an ID *prefix* to an owner, but only a prefix — a bare word like
`ThrottledForLoopLogging` cannot be reserved, so publishing early is what claims it. Moving to a
prefixed ID such as `CarlosK.ThrottledForLoopLogging` is the option that could be reserved, and
it has to be decided before the first push, not after.

### 2. Create the NuGet.org account

Sign in at [nuget.org](https://www.nuget.org) with a Microsoft account and choose a
username. That username is the package owner and it is what the publishing setup below
refers to.

### 3. Set up credentials — one of two ways

**Trusted publishing (recommended).** No long-lived key exists anywhere. The workflow proves
it is this repository using a GitHub OIDC token and gets an API key that expires in an hour.

- On nuget.org: your username → **Trusted Publishing** → add a policy with
  repository owner `CarlosSolrac`, repository `throttled-for-loop-logging`, workflow file
  `release.yml`, environment `nuget`.
- On GitHub: Settings → Secrets and variables → Actions → **Variables** → add
  `NUGET_USER` with your nuget.org username (the username, not the email address).

The workflow uses trusted publishing whenever `NUGET_USER` is set.

**An API key (the fallback).** On nuget.org: your username → **API Keys** → create a key
scoped to **Push** for `ThrottledForLoopLogging` (glob patterns work too), with
the shortest expiry you can live with. Then on GitHub: Settings → Secrets and variables →
Actions → **Secrets** → add `NUGET_API_KEY`. Keys expire, so this is a recurring errand;
trusted publishing is not.

Either way the credential lives in GitHub, never in the repository.

### 4. Decide whether a release needs approval

The workflow's job runs in a GitHub environment named `nuget`. Add a required reviewer to it
under Settings → Environments → `nuget` and every release pauses for your approval before
anything reaches NuGet.org. Only a run that is about to push a version not yet on NuGet.org
reaches that job, so ordinary merges never ask. The job that runs in the environment downloads
the already-built package and pushes it; no code from the repository runs there. Leave it without protection rules and the publish proceeds
unattended. Given that a published version can never be deleted, the approval is worth having.

## Releasing

1. Change `<Version>` in `Directory.Build.props` to the new version, in the same pull request as
   the changes it ships or in one of its own.
2. Merge it to `main`.

That is all. When CI passes on the merge commit, the Release workflow starts on its own and:

- reads the version from the project, exactly as `dotnet pack` will stamp it;
- asks NuGet.org and GitHub whether that version has already been released;
- if not, builds, tests and packs the commit, waits for approval if the `nuget` environment
  requires one, and pushes the package;
- then creates the tag `v<Version>` on that commit and a GitHub release with the `.nupkg` and
  `.snupkg` attached and generated notes. A version with a suffix, such as `0.3.0-beta`, is marked
  as a prerelease on GitHub, and NuGet.org treats it as one too.

What gets published is the newest commit on `main` whose CI passed, not necessarily the commit
that changed the version. If two pull requests merge close together, the release is built from
the later one, once. A run for an older commit either releases the newest commit instead (when
that commit's CI has already passed) or stands aside for that commit's own run, and just before
pushing, the workflow checks `main` again and stands aside if it moved while building or
waiting for approval. If the newest commit's CI fails, nothing is released until a later commit
on `main` passes. If you bump the version twice before either is released, only the second
version is published.

A merge that leaves the version alone releases nothing: the workflow sees the version is already
out and stops with a notice. So does a failed CI run, which never reaches the release at all;
the next green run on `main` picks the unreleased version up.

`Directory.Build.props` is the only place the version lives. The workflow writes the tag from
it, and every package records the commit it was built from, so the tag always goes on the commit
NuGet.org's package came from. Do not push `v*` tags yourself. They do not trigger anything, and
a tag that already exists on a different commit stops the release with an error rather than
publish code that does not match it.

### Releasing by hand

Actions → Release → Run workflow, on `main`, releases whatever version `main` carries without
waiting for CI. It does only the steps that are still missing:

- a version not on NuGet.org yet is built from `main`, published, tagged and released;
- a version already on NuGet.org without a GitHub release (the release step failed) gets its tag
  and release on the commit recorded inside the published package, with the package and symbols
  downloaded from NuGet.org attached. If NuGet.org is still validating the symbols, it waits up
  to about fifteen minutes and otherwise fails, so the next run tries again. It is never rebuilt from whatever `main` is now. The next
  green CI run on `main` does this too, without asking.

Only the version `main` currently carries is looked at. If a release step failed and `main` has
since moved to a newer version, create the older tag and release by hand: the commit is in the
`<repository commit="…">` element of the package's `.nuspec` on NuGet.org.

### When something fails

- **CI failed on the merge.** Nothing was released. Fix it and merge again; the version bump is
  still on `main`, so the next green run releases it.
- **The push to NuGet.org failed.** Usually missing or expired credentials (see step 3 above).
  Fix them, then re-run the failed jobs of that Release run, or run the workflow by hand.
- **NuGet.org says the version already exists.** The workflow downloads the package that is
  there. If it was built from the same commit (a re-run after a push that went through), the
  release carries on and attaches the package NuGet.org serves. If not, it stops: change
  `<Version>` and merge again.
- **The package is on NuGet.org but the tag or GitHub release is missing.** The next green CI run
  on `main` finishes it, or run the workflow by hand.
- **"Tag vX already exists on another commit."** A tag with this version exists but nothing was
  published under it. Delete the tag if it was pushed by mistake, or change `<Version>`.
- **A draft GitHub release for the tag exists.** Usually a release step that died halfway, since
  `gh release create` makes a draft, uploads, then publishes. Delete the draft, then run the
  workflow by hand.
- **"Tag vX is on one commit, but the package was built from another."** The tag was moved or
  made by hand. Move it to the commit named in the error.

## Rehearsing

Run the workflow by hand with **dry run** ticked. It builds, tests and packs, and leaves the
`.nupkg` and `.snupkg` as a build artifact you can download and inspect (`unzip -l` it, or open it
in [NuGet Package Explorer](https://github.com/NuGetPackageExplorer/NuGetPackageExplorer)). It
publishes nothing and creates no tag or release. A run started from any branch other than `main`
is always a dry run.

Locally, `dotnet pack src/ThrottledLogging/ThrottledLogging.csproj -c Release -o artifacts`
produces the same two files.

## What cannot be undone

A published version is permanent. It can be **unlisted**, which hides it from search and from
version resolution, but the file stays downloadable forever for anyone who already depends on
it, and **that version number can never be reused**. Deletion happens only for legal or
security reasons, by asking NuGet support.

So the first push of a new package ID is the decision that sticks: after it, the ID and every
version number you burn are permanent. Rehearse with a dry run, and remember that merging a
version bump to `main` is what publishes.

## What the package already carries

Set in `Directory.Build.props` and the library's `.csproj`, so none of this needs doing again:
MIT licence expression, `README.md` as the package readme, project and repository URLs,
authorship, tags, deterministic CI builds, SourceLink for step-into debugging, and a `.snupkg`
symbol package published alongside the main one.
